// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using LibAtrac9;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static bool ApplyRawAtrac9(CpuContext ctx, VoiceState voice, ulong parameter, ulong source)
    {
        if (!ctx.TryReadUInt32(parameter + 16, out var flags) ||
            !ctx.TryReadUInt32(parameter + 20, out var count) || count is 0 or > 1024 ||
            !ctx.TryReadUInt64(parameter + 24, out var table)) return false;
        try
        {
            // The first RDR streaming block omits bit 1 and supplies preroll;
            // continuation blocks set it. Preserve decoder overlap across them.
            if (voice.StreamingDecoder is null || (flags & 2) == 0)
            {
                voice.StreamingDecoder = new Atrac9Decoder();
                voice.StreamingDecoder.Initialize(BitConverter.GetBytes(voice.FormatConfig));
                voice.CompressedTail = []; voice.StreamingSkip = 0;
            }
            var decoder = voice.StreamingDecoder;
            var config = decoder.Config;
            if (config.ChannelCount != voice.FormatChannels || config.SampleRate != voice.FormatSampleRate) return false;
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
                    !ctx.TryReadUInt32(address + field + 8, out var requested) ||
                    length > 64 * 1024 * 1024 || source + offset < source) return false;
                var bytes = new byte[checked((int)length + voice.CompressedTail.Length)];
                voice.CompressedTail.CopyTo(bytes, 0);
                if (!ctx.Memory.TryRead(source + offset, bytes.AsSpan(voice.CompressedTail.Length))) return false;
                var frames = bytes.Length / config.SuperframeBytes;
                var pcm = new short[checked(frames * config.SuperframeSamples * config.ChannelCount)];
                var compressed = new byte[config.SuperframeBytes];
                var planar = new short[config.ChannelCount][];
                for (var ch = 0; ch < planar.Length; ch++) planar[ch] = new short[config.SuperframeSamples];
                for (var frame = 0; frame < frames; frame++)
                {
                    bytes.AsSpan(frame * config.SuperframeBytes, config.SuperframeBytes).CopyTo(compressed);
                    decoder.Decode(compressed, planar);
                    for (var sample = 0; sample < config.SuperframeSamples; sample++)
                        for (var ch = 0; ch < planar.Length; ch++)
                            pcm[((frame * config.SuperframeSamples + sample) * planar.Length) + ch] = planar[ch][sample];
                }
                voice.CompressedTail = bytes.AsSpan(frames * config.SuperframeBytes).ToArray();
                voice.StreamingSkip = checked(voice.StreamingSkip + skip);
                var available = frames * config.SuperframeSamples;
                var trim = (int)Math.Min(voice.StreamingSkip, (uint)available);
                voice.StreamingSkip -= (uint)trim;
                // RDR uses zero / unsigned negative lengths for an open-ended
                // streaming block. Bound these by the frames actually decoded.
                var samples = requested == 0 || requested > int.MaxValue ? available - trim : Math.Min((int)requested, available - trim);
                if (samples > 0)
                    blocks.Add(new(pcm, config.ChannelCount, config.SampleRate, trim, trim + samples, repeats));
            }
            EnqueueBlocks(voice, blocks, flags);
            voice.SourceAddr = source;
            voice.Waveform = new(13, (uint)config.ChannelCount, (uint)config.SampleRate, voice.FormatConfig,
                (uint)config.SuperframeBytes, (uint)config.SuperframeSamples, 0, 0, 0, 0, 0);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] ngs2.stream_decode_failed addr=0x{source:X} {ex.Message}");
            return false;
        }
    }
}
