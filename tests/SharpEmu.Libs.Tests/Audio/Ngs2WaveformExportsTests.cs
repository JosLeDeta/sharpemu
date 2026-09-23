// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Ngs2WaveformExportsTests
{
    [Fact]
    public void Ps5BlockUses64BitOffsetsAndPreservesAdjacentMemory()
    {
        var memory = new FakeCpuMemory(0x10000, 256);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(ctx.TryWriteUInt32(0x10000, 18));
        Assert.True(ctx.TryWriteUInt32(0x10004, 2));
        var canary = Enumerable.Repeat((byte)0xCC, 48).ToArray();
        Assert.True(memory.TryWrite(0x10080, canary));
        ctx[CpuRegister.Rdi] = 0x10000;
        ctx[CpuRegister.Rsi] = uint.MaxValue - 15;
        ctx[CpuRegister.Rdx] = 32;
        ctx[CpuRegister.Rcx] = 0x10080;

        Assert.Equal(0, Ngs2Exports.Ngs2CalcWaveformBlock(ctx));
        Assert.True(ctx.TryReadUInt64(0x10080, out var offset));
        Assert.True(ctx.TryReadUInt64(0x10088, out var length));
        Assert.Equal(((ulong)uint.MaxValue - 15) * 4, offset);
        Assert.Equal(128UL, length);
        Assert.True(ctx.TryReadUInt32(0x10098, out var samples));
        Assert.Equal(32U, samples);
        Assert.True(ctx.TryReadUInt64(0x100A8, out var unchanged));
        Assert.Equal(0xCCCCCCCCCCCCCCCCUL, unchanged);
    }

    [Fact]
    public void PcmWaveformWritesFormatAndBoundedDataRange()
    {
        var memory = new FakeCpuMemory(0x10000, 0x400);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var wave = new byte[44 + 64];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), 64);
        Assert.True(memory.TryWrite(0x10000, wave));
        ctx[CpuRegister.Rdi] = 0x10000;
        ctx[CpuRegister.Rsi] = (uint)wave.Length;
        ctx[CpuRegister.Rdx] = 0x10200;

        Assert.Equal(0, Ngs2Exports.Ngs2ParseWaveformData(ctx));
        Assert.True(ctx.TryReadUInt32(0x10200, out var type));
        Assert.True(ctx.TryReadUInt32(0x10218, out var dataOffset));
        Assert.True(ctx.TryReadUInt32(0x1021C, out var dataSize));
        Assert.Equal(2U, type);
        Assert.Equal(44U, dataOffset);
        Assert.Equal(64U, dataSize);
    }
}
