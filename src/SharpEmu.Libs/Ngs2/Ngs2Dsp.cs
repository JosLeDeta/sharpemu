// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    internal sealed class Biquad
    {
        internal float[] Coefficients = [1, 0, 0, 0, 0];
        private readonly double[] z1 = new double[8], z2 = new double[8];
        internal void Reset() { Array.Clear(z1); Array.Clear(z2); }
        internal void Process(Span<float> samples, int channels)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var ch = i % channels;
                var x = samples[i];
                var y = Coefficients[0] * x + z1[ch];
                z1[ch] = Coefficients[1] * x - Coefficients[3] * y + z2[ch];
                z2[ch] = Coefficients[2] * x - Coefficients[4] * y;
                samples[i] = (float)y;
            }
        }
    }

    private static bool SetBiquad(CpuContext ctx, VoiceState voice, ulong address, ushort size)
    {
        if (size < 56 || !ctx.TryReadUInt32(address + 8, out var index) || index >= 8 ||
            !ctx.TryReadUInt32(address + 24, out var type) || type != 11) return false;
        Span<byte> bytes = stackalloc byte[20];
        if (!ctx.Memory.TryRead(address + 28, bytes)) return false;
        var coefficients = new float[5];
        for (var i = 0; i < 5; i++)
        {
            coefficients[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            if (!float.IsFinite(coefficients[i])) return false;
        }
        if (!voice.Filters.TryGetValue(index, out var filter)) voice.Filters[index] = filter = new Biquad();
        filter.Coefficients = coefficients;
        return true;
    }

    private static void ProcessVoiceDsp(CpuContext ctx, Ngs2Mixer.Node node, int frames, int rate)
    {
        if (!Voices.TryGetValue(node.Handle, out var voice)) return;
        foreach (var filter in voice.Filters) filter.Value.Process(node.Buffer, node.Channels);
        if (voice.ProcessCallback == 0) return;
        // UserFx operates on planar float buffers, not interleaved AudioOut PCM.
        // The callback and three user values are supplied by the title.
        var system = Systems[Racks[voice.RackHandle].SystemHandle];
        if (system.CallbackScratch == 0 && !KernelMemoryCompatExports.TryAllocateHleData(ctx,
            128 + 8192 * 8 * 4, 16, out system.CallbackScratch))
        { Array.Clear(node.Buffer); return; }
        var address = system.CallbackScratch;
        Span<byte> header = stackalloc byte[128];
        header.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(header, address + 64);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], voice.CallbackData0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], voice.CallbackData1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], voice.CallbackData2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)node.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], (uint)rate);
        var plane = system.CallbackPlane;
        for (var ch = 0; ch < node.Channels; ch++)
        {
            var planeAddress = address + 128 + (ulong)(ch * frames * 4);
            BinaryPrimitives.WriteUInt64LittleEndian(header[(64 + ch * 8)..], planeAddress);
            for (var f = 0; f < frames; f++)
                BinaryPrimitives.WriteSingleLittleEndian(plane.AsSpan(f * 4), node.Buffer[f * node.Channels + ch]);
            if (!ctx.Memory.TryWrite(planeAddress, plane.AsSpan(0, frames * 4)))
            { Array.Clear(node.Buffer); return; }
        }
        var written = ctx.Memory.TryWrite(address, header);
        var scheduler = GuestThreadExecution.Scheduler;
        string? error = null;
        ulong result = 0;
        if (!written || scheduler is null || !scheduler.TryCallGuestFunction(ctx, voice.ProcessCallback,
                address, 0, 0, 0, 0, "ngs2_userfx", out result, out error) || (int)result < 0)
        {
            Array.Clear(node.Buffer);
            if (!voice.CallbackFailed)
                Console.Error.WriteLine($"[LOADER][WARN] ngs2.userfx_failed callback=0x{voice.ProcessCallback:X} {error}");
            voice.CallbackFailed = true;
            return;
        }
        for (var ch = 0; ch < node.Channels; ch++)
        {
            if (!ctx.Memory.TryRead(address + 128 + (ulong)(ch * frames * 4), plane.AsSpan(0, frames * 4)))
            { Array.Clear(node.Buffer); return; }
            for (var f = 0; f < frames; f++)
                node.Buffer[f * node.Channels + ch] = BinaryPrimitives.ReadSingleLittleEndian(plane.AsSpan(f * 4));
        }
    }
}
