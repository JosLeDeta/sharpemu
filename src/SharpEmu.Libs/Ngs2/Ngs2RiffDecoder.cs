// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using LibAtrac9;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    internal static bool TryDecodeRiff(ReadOnlySpan<byte> file, out short[] pcm,
        out int channels, out int rate, out string error)
    {
        pcm = []; channels = rate = 0; error = "Invalid RIFF waveform";
        if (file.Length < 12 || (ulong)BinaryPrimitives.ReadUInt32LittleEndian(file[4..]) + 8 > (ulong)file.Length ||
            !TryParseRiffWaveform(file, (ulong)file.Length, out var info) ||
            info.Channels is 0 or > 8 || info.SampleRate == 0 ||
            (ulong)info.DataOffset + info.DataSize > (ulong)file.Length) return false;
        channels = (int)info.Channels; rate = (int)info.SampleRate;
        var data = file.Slice((int)info.DataOffset, (int)info.DataSize);
        try
        {
            if (info.Type is WaveformTypePcmI16Little or WaveformTypePcmF32Little)
            {
                var width = info.Type == WaveformTypePcmI16Little ? 2 : 4;
                if (data.Length % (width * channels) != 0) return false;
                pcm = new short[data.Length / width];
                for (var i = 0; i < pcm.Length; i++)
                    pcm[i] = width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) :
                        (short)Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]) * 32768f, short.MinValue, short.MaxValue);
            }
            else if (info.Type == WaveformTypeAtrac9)
            {
                var config = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(config, info.ConfigData);
                var decoder = new Atrac9Decoder(); decoder.Initialize(config);
                var c = decoder.Config;
                if (c.ChannelCount != channels || c.SampleRate != rate || data.Length % c.SuperframeBytes != 0)
                { error = "ATRAC9 format or superframe length mismatch"; return false; }
                var frames = data.Length / c.SuperframeBytes;
                var sampleCount = checked(frames * c.SuperframeSamples);
                if (sampleCount > 16_000_000) { error = "Waveform exceeds decode limit"; return false; }
                var planar = new short[channels][];
                for (var ch = 0; ch < channels; ch++) planar[ch] = new short[c.SuperframeSamples];
                pcm = new short[checked(sampleCount * channels)];
                var compressed = new byte[c.SuperframeBytes];
                for (var f = 0; f < frames; f++)
                {
                    data.Slice(f * c.SuperframeBytes, c.SuperframeBytes).CopyTo(compressed);
                    decoder.Decode(compressed, planar);
                    for (var s = 0; s < c.SuperframeSamples; s++)
                        for (var ch = 0; ch < channels; ch++)
                            pcm[((f * c.SuperframeSamples + s) * channels) + ch] = planar[ch][s];
                }
                // fact describes the valid signal after encoder delay.
                if ((ulong)info.DelaySamples + info.NumSamples > (ulong)sampleCount) return false;
                pcm = pcm.AsSpan(checked((int)info.DelaySamples * channels), checked((int)info.NumSamples * channels)).ToArray();
            }
            else { error = $"Unsupported waveform type {info.Type}"; return false; }
            error = ""; return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException)
        { pcm = []; error = ex.Message; return false; }
    }

    private static bool ArmDecodedRiff(CpuContext ctx, ulong voiceHandle, ulong dataAddress)
    {
        if (!ctx.TryReadUInt32(dataAddress + 4, out var size) || size < 36 || size > 64 * 1024 * 1024)
            return false;
        var file = new byte[size + 8];
        if (!ctx.Memory.TryRead(dataAddress, file)) return false;
        if (!TryDecodeRiff(file, out var pcm, out var channels, out var rate, out var error))
        {
            Console.Error.WriteLine($"[LOADER][WARN] ngs2.decode_failed addr=0x{dataAddress:X} {error}");
            return false;
        }
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice)) return false;
            TryParseRiffWaveform(file, (ulong)file.Length, out voice.PendingWaveform);
            voice.PendingPcm = pcm; voice.PendingChannels = channels; voice.PendingRate = rate;
            voice.SourceAddr = dataAddress;
        }
        if (ShouldTrace()) Console.Error.WriteLine($"[LOADER][TRACE] ngs2.decoded voice=0x{voiceHandle:X} frames={pcm.Length / channels} channels={channels} rate={rate}");
        return true;
    }
}
