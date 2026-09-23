// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Ngs2SystemResetOptionTests
{
    [Fact]
    public void ResetInitializesWholeOptionAndPreservesAdjacentMemory()
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x4000, executable: false);
        Assert.NotEqual(0UL, address);
        var context = new CpuContext(memory, Generation.Gen5);
        var original = Enumerable.Repeat((byte)0xCC, 160).ToArray();
        Assert.True(memory.TryWrite(address, original));

        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemResetOption(context));

        var actual = new byte[original.Length];
        Assert.True(memory.TryRead(address, actual));
        var expected = new byte[original.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(expected, 144);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(108), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(112), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(116), 48000);
        expected.AsSpan(144).Fill(0xCC);
        Assert.Equal(expected, actual);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x804A0053), Ngs2Exports.Ngs2SystemResetOption(context));
    }
}
