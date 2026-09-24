// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private readonly HashSet<uint> _perVertexAttributes = [];
        private readonly Dictionary<int, uint> _barycentricInputs = [];
        private uint _interpolationSampleId;
        private const uint InterpolateAtCentroid = 76;
        private const uint InterpolateAtSample = 77;
        private const uint InterpolateAtOffset = 78;

        private uint PixelInputControl(uint attribute) => attribute < (uint)_pixelInputCntl.Length
            ? _pixelInputCntl[attribute]
            : attribute;

        private bool IsCustomPixelInput(uint attribute) => attribute < 32 &&
            (_request.PixelCustomInterpolationMask & (1u << (int)attribute)) != 0;

        private bool IsDefaultPixelInput(uint attribute) =>
            (PixelInputControl(attribute) & 0x20u) != 0 && !IsCustomPixelInput(attribute);

        private bool IsFlatPixelInput(uint attribute) =>
            (PixelInputControl(attribute) & 0x400u) != 0 && !IsCustomPixelInput(attribute);

        private void DeclareInterpolationParameters()
        {
            foreach (var instruction in _request.Program.Instructions)
            {
                if (instruction.Opcode == "VInterpMovF32" &&
                    instruction.Control is Gen5InterpolationControl interpolation &&
                    !IsDefaultPixelInput(interpolation.Attribute))
                {
                    _perVertexAttributes.Add(interpolation.Attribute);
                }
            }

            var attributes = _request.Program.Instructions
                .Select(instruction => instruction.Control)
                .OfType<Gen5InterpolationControl>()
                .Select(control => control.Attribute)
                .Distinct()
                .Where(attribute => !IsDefaultPixelInput(attribute));
            foreach (var group in attributes.GroupBy(attribute => PixelInputControl(attribute) & 0x1Fu))
            {
                // One Vulkan input represents all aliases. Raw reads or mixed
                // flat/smooth slots need per-vertex values for the whole group.
                if (group.Any(_perVertexAttributes.Contains) ||
                    group.Select(IsFlatPixelInput).Distinct().Count() > 1)
                {
                    _perVertexAttributes.UnionWith(group);
                }
            }

            if (_perVertexAttributes.Count == 0)
            {
                return;
            }

            _module.AddCapability(SpirvCapability.FragmentBarycentricKhr);
            _module.AddExtension("SPV_KHR_fragment_shader_barycentric");
            _module.AddCapability(SpirvCapability.InterpolationFunction);
            var enabledInputs = _pixelInputAddress & _pixelInputEnable;
            if ((enabledInputs & (1u << 3)) != 0)
            {
                throw new NotSupportedException("Pull-model interpolation parameters are not supported.");
            }

            var variables = new Dictionary<bool, uint>();
            foreach (var bit in new[] { 0, 1, 2, 4, 5, 6 })
            {
                if ((enabledInputs & (1u << bit)) == 0)
                {
                    continue;
                }

                if (!variables.TryGetValue(bit < 4, out var variable))
                {
                    variable = _module.AddGlobalVariable(
                        _module.TypePointer(SpirvStorageClass.Input, _vec3Type), SpirvStorageClass.Input);
                    _module.AddDecoration(variable, SpirvDecoration.BuiltIn,
                        (uint)(bit < 4 ? SpirvBuiltIn.BaryCoordKhr : SpirvBuiltIn.BaryCoordNoPerspKhr));
                    variables.Add(bit < 4, variable);
                    _interfaces.Add(variable);
                }
                _barycentricInputs.Add(bit, variable);
            }

            if ((enabledInputs & 0x11u) != 0)
            {
                _module.AddCapability(SpirvCapability.SampleRateShading);
                _interpolationSampleId = _module.AddGlobalVariable(
                    _module.TypePointer(SpirvStorageClass.Input, _intType), SpirvStorageClass.Input);
                _module.AddDecoration(_interpolationSampleId, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SampleId);
                _module.AddDecoration(_interpolationSampleId, SpirvDecoration.Flat);
                _interfaces.Add(_interpolationSampleId);
            }
        }

        private uint LoadBarycentricCoordinates(int bit, uint variable) => bit switch
        {
            0 or 4 => _module.AddInstruction(SpirvOp.ExtInst, _vec3Type, _glsl, InterpolateAtSample,
                variable, Load(_intType, _interpolationSampleId)),
            2 or 6 => _module.AddInstruction(SpirvOp.ExtInst, _vec3Type, _glsl, InterpolateAtCentroid, variable),
            _ => _module.AddInstruction(SpirvOp.ExtInst, _vec3Type, _glsl, InterpolateAtOffset,
                variable, _module.ConstantNull(_vec2Type)),
        };

        private bool TryEmitInterpolationParameter(
            Gen5ShaderInstruction instruction,
            Gen5InterpolationControl interpolation,
            uint input,
            uint destination,
            out string error)
        {
            error = string.Empty;
            var custom = IsCustomPixelInput(interpolation.Attribute);

            uint LoadVertex(uint vertex)
            {
                var pointer = _module.AddInstruction(SpirvOp.AccessChain,
                    _module.TypePointer(SpirvStorageClass.Input, _floatType),
                    input, UInt(vertex), UInt(interpolation.Channel));
                return Load(_floatType, pointer);
            }

            uint LoadParameter(uint mode)
            {
                var value = LoadVertex((mode + 1) % 3);
                return !custom && mode < 2
                    ? _module.AddInstruction(SpirvOp.FSub, _floatType, value, LoadVertex(0))
                    : value;
            }

            uint result;
            if (instruction.Opcode == "VInterpMovF32")
            {
                var mode = instruction.Words[0] & 0xFFu;
                if (mode >= 3)
                {
                    error = "reserved interpolation parameter selector";
                    return false;
                }
                result = IsFlatPixelInput(interpolation.Attribute) ? LoadVertex(0) : LoadParameter(mode);
            }
            else if (instruction.Opcode is "VInterpP1F32" or "VInterpP2F32")
            {
                if (IsFlatPixelInput(interpolation.Attribute))
                {
                    result = LoadVertex(0);
                }
                else
                {
                    var firstPhase = instruction.Opcode == "VInterpP1F32";
                    var source = Bitcast(_floatType, GetRawSource(instruction, 0));
                    var product = _module.AddInstruction(SpirvOp.FMul, _floatType,
                        LoadParameter(firstPhase ? 0u : 1u), source);
                    result = _module.AddInstruction(SpirvOp.FAdd, _floatType, product,
                        firstPhase ? LoadParameter(2) : Bitcast(_floatType, LoadV(destination)));
                }
            }
            else
            {
                error = $"unsupported interpolation opcode {instruction.Opcode}";
                return false;
            }

            StoreV(destination, Bitcast(_uintType, result));
            return true;
        }
    }
}
