// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5InterpolationParameterTests
{
    [Theory]
    [InlineData(0u, false, 1u, true)]
    [InlineData(1u, false, 2u, true)]
    [InlineData(2u, false, 0u, false)]
    [InlineData(0u, true, 1u, false)]
    [InlineData(1u, true, 2u, false)]
    [InlineData(2u, true, 0u, false)]
    public void ParameterMove_SelectsVertexAndPreservesCustomData(
        uint selector, bool custom, uint vertex, bool subtractOrigin)
    {
        var request = Request(selector, custom);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr).Operands[0];
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == input && instruction.Operands[1] == (uint)SpirvDecoration.Flat);
        var firstAccess = instructions.First(instruction => instruction.Opcode == SpirvOp.AccessChain &&
            instruction.Operands[2] == input);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == firstAccess.Operands[3] && instruction.Operands[2] == vertex);
        Assert.Equal(subtractOrigin, instructions.Any(instruction => instruction.Opcode == SpirvOp.FSub));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(255u)]
    public void ReservedSelector_Fails(uint selector)
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(Request(selector, false), out _, out var error));
        Assert.Contains("reserved interpolation parameter selector", error);
    }

    [Fact]
    public void MixedBarycentricLocations_ShareBuiltIns()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(2, true, 0x77), out var shader, out var error), error);
        var builtIns = Instructions(shader.Spirv)
            .Where(instruction => instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn)
            .Select(instruction => instruction.Operands[2]).ToArray();
        Assert.Equal(builtIns.Length, builtIns.Distinct().Count());
        Assert.Contains((uint)SpirvBuiltIn.BaryCoordKhr, builtIns);
        Assert.Contains((uint)SpirvBuiltIn.BaryCoordNoPerspKhr, builtIns);
        Assert.Contains((uint)SpirvBuiltIn.SampleId, builtIns);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void OrdinaryInterpolation_DoesNotRequireBarycentricFeature()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(0, false, opcode: "VInterpP2F32"), out var shader, out var error), error);
        Assert.DoesNotContain(Instructions(shader.Spirv), instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void PullModel_DoesNotSilentlyUseZeroCoordinates()
    {
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(Request(2, true, 8), out _, out var error));
        Assert.Contains("Pull-model interpolation", error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AliasedSlots_ReadOneExportWithoutRelocation(bool raw, bool mixedFlat)
    {
        var first = Interpolate(0, 0, "VInterpP2F32");
        var second = Interpolate(4, 1, raw ? "VInterpMovF32" : "VInterpP2F32");
        var request = Prepare([first, second], [3u, mixedFlat ? 0x403u : 3u]);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var location = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Location);
        Assert.Equal(3u, location.Operands[2]);
        Assert.Equal(raw || mixedFlat, instructions.Any(instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[0] == location.Operands[0] &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr));
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void DefaultSlots_DoNotConsumeVertexExports(uint defaultValue)
    {
        var request = Prepare([Interpolate(0, 0, "VInterpMovF32")], [0x20u | (defaultValue << 8)]);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Location);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands[0] == (uint)SpirvCapability.FragmentBarycentricKhr);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void CustomSlot_WithDefaultBitStillReadsExport()
    {
        var request = Prepare([Interpolate(0, 0, "VInterpMovF32")], [0x23u], customMask: 1);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var location = Assert.Single(Instructions(shader.Spirv), instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.Location);
        Assert.Equal(3u, location.Operands[2]);
        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    public void FlatParameterMove_ReadsProvokingVertex(uint selector)
    {
        var instruction = Interpolate(0, 0, "VInterpMovF32") with { Words = [selector] };
        var request = Prepare([instruction], [0x400u]);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Instructions(shader.Spirv);
        var input = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands[1] == (uint)SpirvDecoration.PerVertexKhr).Operands[0];
        var access = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.AccessChain &&
            instruction.Operands[2] == input);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == access.Operands[3] && instruction.Operands[2] == 0);
        Assert.DoesNotContain(instructions, instruction => instruction.Opcode == SpirvOp.FSub);
        ValidateWhenAvailable(shader.Spirv);
    }

    private static Gen5ShaderInstruction Interpolate(uint pc, uint attribute, string opcode) =>
        new(pc, Gen5ShaderEncoding.Vintrp, opcode, [0], [Gen5Operand.Vector(0)],
            [Gen5Operand.Vector(4 + attribute)], new Gen5InterpolationControl(attribute, 2));

    private static ShaderCompileRequest Prepare(
        Gen5ShaderInstruction[] instructions, uint[] controls, uint customMask = 0)
    {
        var program = ResourceTestProgram.Program([.. instructions, ResourceTestProgram.EndProgram((uint)instructions.Length * 4)]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = 2,
            PixelInputEnable = 2,
            PixelInputCntl = controls,
            PixelCustomInterpolationMask = customMask,
        };
    }

    private static ShaderCompileRequest Request(
        uint selector, bool custom, uint inputs = 2, string opcode = "VInterpMovF32")
    {
        var interpolation = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vintrp, opcode,
            [selector], [Gen5Operand.Vector(selector)], [Gen5Operand.Vector(4)], new Gen5InterpolationControl(1, 2));
        var program = ResourceTestProgram.Program(interpolation, ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = inputs,
            PixelInputEnable = inputs,
            PixelInputCntl = [0, custom ? 0x401u : 1u],
            PixelCustomInterpolationMask = custom ? 2u : 0u,
        };
    }

    private static List<Instruction> Instructions(byte[] code)
    {
        var words = new uint[code.Length / 4];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<Instruction>();
        for (var index = 5; index < words.Length;)
        {
            var count = (int)(words[index] >> 16);
            result.Add(new Instruction((SpirvOp)(words[index] & 0xFFFF), words[(index + 1)..(index + count)]));
            index += count;
        }
        return result;
    }

    private static void ValidateWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return;
        }
        var executable = Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (!File.Exists(executable))
        {
            return;
        }
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed record Instruction(SpirvOp Opcode, uint[] Operands);
}
