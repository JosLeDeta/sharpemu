// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5WavePriorityTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(0xFFFFu)]
    public void DecodePriorityPreservesFollowingInstructions(uint immediate)
    {
        var program = Decode(immediate);
        var priority = program.Instructions[1];
        Assert.Equal("SSetprio", priority.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Sopp, priority.Encoding);
        Assert.Equal(new Gen5Operand(Gen5OperandKind.LiteralConstant, immediate & 3u),
            Assert.Single(priority.Sources));
        Assert.Empty(priority.Destinations);
        Assert.Equal(0xBF8F0000u | immediate, Assert.Single(priority.Words));
        Assert.Equal("SMovB32", program.Instructions[2].Opcode);
        Assert.Equal(8u, program.Instructions[2].Pc);
        Assert.Equal("SEndpgm", program.Instructions[3].Opcode);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void PriorityHintLeavesShaderWorkUnchanged(uint priority)
    {
        var decoded = Decode(priority);
        var instructions = decoded.Instructions.Take(3).ToArray();
        var work = Program([.. instructions, MoveScalar(12, 6, 16), MoveScalar(16, 7, 0),
            BufferLoad(20, 4), EndProgram(28)]);
        var baselineInstructions = work.Instructions.ToArray();
        baselineInstructions[1] = baselineInstructions[1] with { Opcode = "SNop", Sources = [] };
        var baseline = Program(baselineInstructions);
        var (plan, resources, layout) = Prepare(work);
        var (baselinePlan, baselineResources, baselineLayout) = Prepare(baseline);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        var baselineRequest = new ShaderCompileRequest(baselinePlan, baselineResources, baselineLayout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(baselineRequest, out var expected, out error), error);
        Assert.Equal(expected.Spirv, shader.Spirv);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(baselineRequest, out var expectedMetal, out error), error);
        Assert.Equal(expectedMetal.Source, metal.Source);
    }

    private static Gen5ShaderProgram Decode(uint immediate)
    {
        uint[] words = [0xBE840380u, 0xBF8F0000u | immediate, 0xBE850380u, 0xBF810000u];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program;
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
