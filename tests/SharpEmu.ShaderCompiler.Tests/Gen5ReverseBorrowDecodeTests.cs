// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ReverseBorrowDecodeTests
{
    [Fact]
    public void AstroTraversalUsesVop3bScalarBorrowDestination()
    {
        uint[] words = [0xD12A6A05u, 128u | (281u << 9) | (4u << 18), 0xBF810000u];
        var context = new CpuContext(new InstructionMemory(words), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);

        var instruction = program.Instructions[0];
        Assert.Equal("VSubbrevU32", instruction.Opcode);
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(106u, control.ScalarDestination);
        Assert.Equal(0u, control.AbsoluteMask);
        Assert.Equal(0u, control.OperandSelect);
        Assert.Equal(Gen5Operand.Scalar(4), instruction.Sources[2]);
    }

    private sealed class InstructionMemory(uint[] words) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || (address - 0x1000) % sizeof(uint) != 0 || destination.Length != sizeof(uint))
                return false;
            var index = (address - 0x1000) / sizeof(uint);
            if (index >= (ulong)words.Length) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(destination, words[(int)index]);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
