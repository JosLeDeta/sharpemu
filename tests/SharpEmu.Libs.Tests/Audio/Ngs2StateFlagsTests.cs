// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Core.Memory;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[Collection("SaveDataMemoryState")]
public sealed class Ngs2StateFlagsTests
{
    [Fact]
    public void VoiceFlagsPreserveTheAdjacentStackCanary()
    {
        using var memory = new PhysicalVirtualMemory();
        var baseAddress = memory.AllocateAt(0, 0x4000, executable: false);
        Assert.NotEqual(0UL, baseAddress);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = baseAddress + 0x100;
        ctx[CpuRegister.Rdx] = output;
        Assert.Equal(0, Ngs2Exports.Ngs2SystemCreateWithAllocator(ctx));
        Assert.True(ctx.TryReadUInt64(output, out var system));
        try
        {
            ctx[CpuRegister.Rdi] = system;
            ctx[CpuRegister.Rsi] = 1;
            ctx[CpuRegister.R8] = output;
            Assert.Equal(0, Ngs2Exports.Ngs2RackCreateWithAllocator(ctx));
            Assert.True(ctx.TryReadUInt64(output, out var rack));
            ctx[CpuRegister.Rdi] = rack;
            ctx[CpuRegister.Rsi] = 0;
            ctx[CpuRegister.Rdx] = output;
            Assert.Equal(0, Ngs2Exports.Ngs2RackGetVoiceHandle(ctx));
            Assert.True(ctx.TryReadUInt64(output, out var voice));

            Assert.True(ctx.TryWriteUInt64(output, 0xCAFEBABEFFFFFFFF));
            ctx[CpuRegister.Rdi] = voice;
            ctx[CpuRegister.Rsi] = output;
            Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetStateFlags(ctx));
            Assert.True(ctx.TryReadUInt64(output, out var result));
            Assert.Equal(0xCAFEBABE00000000UL, result);
        }
        finally
        {
            ctx[CpuRegister.Rdi] = system;
            Ngs2Exports.Ngs2SystemDestroy(ctx);
        }
    }
}
