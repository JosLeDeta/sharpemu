// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Ngs2RiffDecoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodesStereoPcmWithoutCombiningChannels(bool floating)
    {
        var size = floating ? 16 : 8;
        var file = new byte[44 + size];
        "RIFF"u8.CopyTo(file); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)file.Length - 8);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), (ushort)(floating ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 24000);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), (ushort)(floating ? 8 : 4));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), (ushort)(floating ? 32 : 16));
        "data"u8.CopyTo(file.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)size);
        short[] expected = [16384, -8192, 4096, -16384];
        for (var i = 0; i < expected.Length; i++)
            if (floating) BinaryPrimitives.WriteSingleLittleEndian(file.AsSpan(44 + i * 4), expected[i] / 32768f);
            else BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(44 + i * 2), expected[i]);
        Assert.True(Ngs2Exports.TryDecodeRiff(file, out var pcm, out var channels, out var rate, out var error), error);
        Assert.Equal(expected, pcm); Assert.Equal(2, channels); Assert.Equal(24000, rate);
        Assert.False(Ngs2Exports.TryDecodeRiff(file.AsSpan(0, 42), out _, out _, out _, out _));
    }
}
