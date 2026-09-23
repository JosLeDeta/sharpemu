// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5WaveMaskWidthTests
{
    [Theory]
    [InlineData(32u, false, 106u)]
    [InlineData(64u, false, 106u)]
    [InlineData(32u, true, 126u)]
    [InlineData(64u, true, 126u)]
    public void ComparePreservesAdjacentScalarOnlyForWave32(uint lanes, bool updateExec, uint destination)
    {
        var instruction = new Gen5ShaderInstruction(0,
            Gen5ShaderEncoding.Vopc,
            updateExec ? "VCmpxLtF32" : "VCmpLtF32", [0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [], null);
        var baseline = ScalarStores([instruction], lanes);
        var second = new Gen5ShaderInstruction(4, instruction.Encoding, instruction.Opcode,
            [0u], instruction.Sources, instruction.Destinations, null);
        var compared = ScalarStores([instruction, second], lanes);

        Assert.True(compared.GetValueOrDefault(destination) > baseline.GetValueOrDefault(destination));
        var highStores = compared.GetValueOrDefault(destination + 1) - baseline.GetValueOrDefault(destination + 1);
        if (lanes == 32) Assert.Equal(0, highStores);
        else Assert.True(highStores > 0);
    }

    private static Dictionary<uint, int> ScalarStores(Gen5ShaderInstruction[] instructions, uint lanes)
    {
        var program = new Gen5ShaderProgram(0x100000000, instructions);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = lanes,
            LocalSizeX = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = Enumerable.Range(0, shader.Spirv.Length / 4)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(i * 4))).ToArray();
        uint sgpr = 0;
        var constants = new Dictionary<uint, uint>();
        var pointers = new Dictionary<uint, uint>();
        var stores = new Dictionary<uint, int>();
        for (int i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            var op = words[i] & 65535;
            var count = (int)(words[i] >> 16);
            if (op == 5 && Encoding.UTF8.GetString(shader.Spirv, (i + 2) * 4, (count - 2) * 4).TrimEnd('\0') == "sgpr")
                sgpr = words[i + 1];
            if (op == 43) constants[words[i + 2]] = words[i + 3];
            if (op == 65 && words[i + 3] == sgpr && constants.TryGetValue(words[i + 4], out var register))
                pointers[words[i + 2]] = register;
            if (op == 62 && pointers.TryGetValue(words[i + 1], out var target))
                stores[target] = stores.GetValueOrDefault(target) + 1;
        }
        Assert.NotEqual(0u, sgpr);
        return stores;
    }
}
