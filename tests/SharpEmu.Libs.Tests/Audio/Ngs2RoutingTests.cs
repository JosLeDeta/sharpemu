// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[Collection("SaveDataMemoryState")]
public sealed class Ngs2RoutingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(28)]
    public void NativeAndCompactCommandsProduceRoutedAudioAndCopyMatrixAtSubmission(int sourceType)
    {
        using var memory = new PhysicalVirtualMemory();
        var address = memory.AllocateAt(0, 0x10000, false);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdx] = address;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemCreateWithAllocator(ctx));
        ctx.TryReadUInt64(address, out var system);
        ulong Voice(uint rackId)
        {
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = rackId; ctx[CpuRegister.R8] = address;
            Assert.Equal(0, Ngs2Exports.Ngs2RackCreateWithAllocator(ctx)); ctx.TryReadUInt64(address, out var rack);
            ctx[CpuRegister.Rdi] = rack; ctx[CpuRegister.Rsi] = 0; ctx[CpuRegister.Rdx] = address;
            Assert.Equal(0, Ngs2Exports.Ngs2RackGetVoiceHandle(ctx)); ctx.TryReadUInt64(address, out var voice);
            return voice;
        }
        void Param(ulong voice, uint id, uint a, uint b = 0, ulong c = 0, ushort size = 24)
        {
            ctx.TryWriteUInt64(address + 0x100, ((ulong)id << 32) | size);
            ctx.TryWriteUInt32(address + 0x108, a); ctx.TryWriteUInt32(address + 0x10C, b);
            ctx.TryWriteUInt64(address + 0x110, c);
            ctx[CpuRegister.Rdi] = voice; ctx[CpuRegister.Rsi] = address + 0x100;
            Assert.Equal(0, Ngs2Exports.Ngs2VoiceControl(ctx));
        }
        void Command(ulong voice, ulong header, ulong value)
        {
            ctx.TryWriteUInt64(address + 0x200, header); ctx.TryWriteUInt64(address + 0x208, value);
            ctx[CpuRegister.Rdi] = voice; ctx[CpuRegister.Rsi] = address + 0x200; ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2VoiceRunCommands(ctx));
        }
        try
        {
            var master = Voice(0x3000); var bus = Voice(0x2000); var source = Voice(0x1000);
            Param(master, 0x30000000, 2); Param(bus, 0x20000000, 2);
            Param(source, 5, 0, 0, bus); Param(bus, 5, 0, 0, master);
            Param(bus, 2, 0, BitConverter.SingleToUInt32Bits(.5f));
            var wave = new byte[44 + 64];
            "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), (uint)wave.Length - 8);
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 48000); BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16); "data"u8.CopyTo(wave.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), 64);
            for (var i = 44; i < wave.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(i), 16384);
            memory.TryWrite(address + 0x400, wave);
            ctx.TryWriteUInt64(address + 0x10E8, 0xDEADC0DEDEADC0DEUL);
            ctx[CpuRegister.Rdi] = address + 0x400; ctx[CpuRegister.Rsi] = (ulong)wave.Length; ctx[CpuRegister.Rdx] = address + 0x1000;
            Assert.Equal(0, Ngs2Exports.Ngs2ParseWaveformData(ctx));
            ctx.TryReadUInt32(address + 0x1018, out var dataOffset); Assert.Equal(44U, dataOffset);
            ctx.TryReadUInt64(address + 0x1048, out var blockOffset); Assert.Equal(44UL, blockOffset);
            ctx.TryReadUInt64(address + 0x10E8, out var canary); Assert.Equal(0xDEADC0DEDEADC0DEUL, canary);
            if (sourceType != 0)
            {
                ctx.TryWriteUInt64(address + 0x100, 0x1000000000000028UL);
                ctx.TryWriteUInt32(address + 0x108, (uint)sourceType); ctx.TryWriteUInt32(address + 0x10C, 1);
                ctx.TryWriteUInt32(address + 0x110, 48000);
                ctx[CpuRegister.Rdi] = source; ctx[CpuRegister.Rsi] = address + 0x100;
                Assert.Equal(0, Ngs2Exports.Ngs2VoiceControl(ctx));
            }
            for (var i = 0; i < 4; i++) ctx.TryWriteUInt32(address + 0x900 + (uint)i * 4, BitConverter.SingleToUInt32Bits(.5f));
            // Waveform pointer at +8, then flags/count/blocks pointer.
            ctx.TryWriteUInt64(address + 0x100, 0x1000000100000020UL);
            ctx.TryWriteUInt64(address + 0x108, address + (sourceType == 28 ? 0x900UL : sourceType == 18 ? 0x42CUL : 0x400UL)); ctx.TryWriteUInt64(address + 0x110, 0x100000001UL);
            ctx.TryWriteUInt64(address + 0x118, address + 0x800);
            ctx.TryWriteUInt64(address + 0x800, sourceType != 0 ? 0UL : 44UL); ctx.TryWriteUInt64(address + 0x808, sourceType == 28 ? 16UL : 8UL);
            ctx.TryWriteUInt32(address + 0x810, 1); // Repeat exactly once, not indefinitely.
            ctx.TryWriteUInt32(address + 0x814, 0); ctx.TryWriteUInt32(address + 0x818, 4);
            ctx[CpuRegister.Rdi] = source; ctx[CpuRegister.Rsi] = address + 0x100;
            Assert.Equal(0, Ngs2Exports.Ngs2VoiceControl(ctx));
            ctx.TryWriteUInt32(address + 0x300, BitConverter.SingleToUInt32Bits(1));
            ctx.TryWriteUInt32(address + 0x304, BitConverter.SingleToUInt32Bits(.25f));
            Command(source, 0x0002110000000005UL, address + 0x300);
            ctx.TryWriteUInt64(address + 0x300, 0); // Guest reuses its temporary matrix.
            Command(source, 0x0000030000000007UL, 0);
            Command(source, 0x0000010000000006UL, BitConverter.SingleToUInt32Bits(.5f));
            Command(master, 0x0000040000000002UL, 1); Command(bus, 0x0000040000000002UL, 1);
            Command(source, 0x0000040000000002UL, 1);
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = 4;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemSetGrainSamples(ctx));
            ctx.TryWriteUInt64(address + 0x600, address + 0x700); ctx.TryWriteUInt64(address + 0x608, 32);
            ctx.TryWriteUInt32(address + 0x610, 8); ctx.TryWriteUInt32(address + 0x614, 2);
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = address + 0x600; ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(ctx));
            var output = new byte[32]; memory.TryRead(address + 0x700, output);
            for (var frame = 0; frame < 4; frame++)
            {
                Assert.Equal(.125f, BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(frame * 8)));
                Assert.Equal(.03125f, BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(frame * 8 + 4)));
            }
            Command(source, 0x0000040000000002UL, 16); // Pause survives without rearming.
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = address + 0x600; ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(ctx));
            memory.TryRead(address + 0x700, output); Assert.All(output, b => Assert.Equal(0, b));
            Command(source, 0x0000040000000002UL, 32);
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = address + 0x600; ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(ctx));
            memory.TryRead(address + 0x700, output);
            Assert.Equal(.125f, BinaryPrimitives.ReadSingleLittleEndian(output));
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(ctx));
            memory.TryRead(address + 0x700, output); Assert.All(output, b => Assert.Equal(0, b));

            // Reuse an interrupted voice for a DIFFERENT sound at the same address.
            // The old implementation retained the previous block at index zero.
            Command(source, 0x0000040000000002UL, 1);
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = address + 0x600; ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(ctx));
            Command(source, 0x0000040000000002UL, 4);
            for (var i = 0; i < 4; i++)
            {
                if (sourceType == 28) ctx.TryWriteUInt32(address + 0x900 + (uint)i * 4, BitConverter.SingleToUInt32Bits(-.25f));
                else ctx.TryWriteUInt16(address + 0x42C + (uint)i * 2, unchecked((ushort)-8192));
            }
            ctx.TryWriteUInt64(address + 0x100, 0x1000000100000020UL);
            ctx.TryWriteUInt64(address + 0x108, address + (sourceType == 28 ? 0x900UL : sourceType == 18 ? 0x42CUL : 0x400UL));
            ctx.TryWriteUInt64(address + 0x110, 0x100000000UL); // Last block, no continuation.
            ctx.TryWriteUInt64(address + 0x118, address + 0x800);
            ctx.TryWriteUInt32(address + 0x810, 0);
            ctx[CpuRegister.Rdi] = source; ctx[CpuRegister.Rsi] = address + 0x100;
            Assert.Equal(0, Ngs2Exports.Ngs2VoiceControl(ctx));
            Command(source, 0x0000040000000002UL, 1);
            ctx[CpuRegister.Rdi] = system; ctx[CpuRegister.Rsi] = address + 0x600; ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, Ngs2Exports.Ngs2SystemRender(ctx));
            memory.TryRead(address + 0x700, output);
            Assert.Equal(-.0625f, BinaryPrimitives.ReadSingleLittleEndian(output));
            Assert.Equal(-.015625f, BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(4)));
        }
        finally { ctx[CpuRegister.Rdi] = system; Ngs2Exports.Ngs2SystemDestroy(ctx); }
    }

    [Fact]
    public void ListenerTransformAccountsForTranslationRotationAndInvalidOrientation()
    {
        Assert.True(Ngs2Exports.TryListenerTransform(new(10, 0, 0), Vector3.UnitX, Vector3.UnitY, out var matrix));
        Assert.Equal(new Vector3(0, 0, 5), Vector3.Transform(new Vector3(15, 0, 0), matrix));
        Assert.Equal(new Vector3(1, 0, 0), Vector3.Transform(new Vector3(10, 0, -1), matrix));
        Assert.False(Ngs2Exports.TryListenerTransform(Vector3.Zero, Vector3.UnitX, Vector3.UnitX, out _));
        Assert.False(Ngs2Exports.TryListenerTransform(new(float.NaN, 0, 0), Vector3.UnitX, Vector3.UnitY, out _));
    }
}
