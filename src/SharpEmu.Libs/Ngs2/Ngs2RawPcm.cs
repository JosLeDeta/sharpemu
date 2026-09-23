// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static bool ApplyRawPcm(CpuContext ctx, VoiceState voice, ulong parameter, ulong source)
    {
        var width = voice.FormatType is 2 or 18 ? 2 : voice.FormatType is 8 or 28 ? 4 : 0;
        if (width == 0 || voice.FormatChannels is 0 or > 8 || voice.FormatSampleRate is < 8000 or > 192000 ||
            !ctx.TryReadUInt32(parameter + 16, out var flags) ||
            !ctx.TryReadUInt32(parameter + 20, out var count) || count is 0 or > 1024 ||
            !ctx.TryReadUInt64(parameter + 24, out var table)) return false;
        var channels = (int)voice.FormatChannels;
        var blocks = new List<SampleBlock>();
        for (uint i = 0; i < count; i++)
        {
            var field = ctx.TargetGeneration == Generation.Gen5 ? 16UL : 8UL;
            var address = table + i * (field + 24);
            ulong offset, length;
            if (field == 16)
            {
                if (!ctx.TryReadUInt64(address, out offset) || !ctx.TryReadUInt64(address + 8, out length)) return false;
            }
            else
            {
                if (!ctx.TryReadUInt32(address, out var o) || !ctx.TryReadUInt32(address + 4, out var l)) return false;
                offset = o; length = l;
            }
            if (!ctx.TryReadUInt32(address + field, out var repeats) ||
                !ctx.TryReadUInt32(address + field + 4, out var skip) ||
                !ctx.TryReadUInt32(address + field + 8, out var frames) ||
                length > 64 * 1024 * 1024 || source + offset < source || length % (ulong)(width * channels) != 0 ||
                ((ulong)skip + frames) * (uint)(width * channels) > length || frames == 0) return false;
            var bytes = new byte[(int)length];
            if (!ctx.Memory.TryRead(source + offset, bytes)) return false;
            var pcm = new short[checked((int)frames * channels)];
            var start = checked((int)skip * channels * width);
            for (var j = 0; j < pcm.Length; j++)
            {
                if (width == 2) pcm[j] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(start + j * 2));
                else
                {
                    var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(start + j * 4));
                    if (!float.IsFinite(value)) return false;
                    pcm[j] = (short)Math.Clamp(value * 32768f, short.MinValue, short.MaxValue);
                }
            }
            blocks.Add(new(pcm, channels, (int)voice.FormatSampleRate, 0, (int)frames, repeats));
        }
        EnqueueBlocks(voice, blocks, flags);
        voice.SourceAddr = source;
        voice.Waveform = new(voice.FormatType, (uint)channels, voice.FormatSampleRate, 0,
            (uint)(width * channels), 1, 0, (uint)(blocks[0].Pcm.Length * width), 0, 0, (uint)blocks[0].End);
        return true;
    }
}
