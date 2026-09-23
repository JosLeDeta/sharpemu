// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private sealed record SampleBlock(short[] Pcm, int Channels, int Rate, int Start, int End, uint Repeats);

    private static bool ReadFormatUnit(CpuContext ctx, ulong address, out uint bytes, out uint samples, out uint delay)
    {
        bytes = samples = delay = 0;
        if (!ctx.TryReadUInt32(address, out var type) || !ctx.TryReadUInt32(address + 4, out var channels) ||
            channels is 0 or > 8 || !ctx.TryReadUInt32(address + 12, out var config) ||
            !ctx.TryReadUInt32(address + 20, out delay)) return false;
        switch (type)
        {
            case 2 or 18: bytes = channels * 2; samples = 1; return true;
            case 8 or 28: bytes = channels * 4; samples = 1; return true;
            case 12: bytes = 16; samples = 28; return true;
            case 13:
                try
                {
                    var codec = new LibAtrac9.Atrac9Config(BitConverter.GetBytes(config));
                    bytes = (uint)codec.SuperframeBytes; samples = (uint)codec.SuperframeSamples;
                    return codec.ChannelCount == channels;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException) { return false; }
            default: return false;
        }
    }

    private static bool ConfigureBlocks(CpuContext ctx, ulong handle, ulong parameter)
    {
        if (!ctx.TryReadUInt32(parameter + 16, out var flags) ||
            !ctx.TryReadUInt32(parameter + 20, out var count) || count > 1024 ||
            !ctx.TryReadUInt64(parameter + 24, out var address)) return false;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(handle, out var voice) || voice.PendingPcm is null) return false;
            var blocks = new List<SampleBlock>();
            if (count == 0)
            {
                blocks.Add(new(voice.PendingPcm, voice.PendingChannels, voice.PendingRate, 0,
                    voice.PendingPcm.Length / voice.PendingChannels, 0));
            }
            for (uint i = 0; i < count; i++)
            {
                var block = address + i * (ctx.TargetGeneration == Generation.Gen5 ? 40UL : 32UL);
                ulong offset, length;
                var field = 8UL;
                if (ctx.TargetGeneration == Generation.Gen5)
                {
                    if (!ctx.TryReadUInt64(block, out offset) || !ctx.TryReadUInt64(block + 8, out length)) return false;
                    field = 16;
                }
                else
                {
                    if (!ctx.TryReadUInt32(block, out var o) || !ctx.TryReadUInt32(block + 4, out var l)) return false;
                    offset = o; length = l;
                }
                if (!ctx.TryReadUInt32(block + field, out var repeats) ||
                    !ctx.TryReadUInt32(block + field + 4, out var skip) ||
                    !ctx.TryReadUInt32(block + field + 8, out var samples)) return false;
                var info = voice.PendingWaveform;
                if (info.FrameSize == 0 || offset < info.DataOffset ||
                    offset - info.DataOffset > info.DataSize || length > info.DataSize - (offset - info.DataOffset)) return false;
                var relative = offset - info.DataOffset;
                if (relative % info.FrameSize != 0) return false;
                var encodedStart = relative / info.FrameSize * info.NumFrameSamples + skip;
                if (encodedStart < info.DelaySamples) return false;
                var start = encodedStart - info.DelaySamples;
                var end = start + samples;
                if (end > (ulong)voice.PendingPcm.Length / (uint)voice.PendingChannels || samples == 0) return false;
                blocks.Add(new(voice.PendingPcm, voice.PendingChannels, voice.PendingRate, (int)start, (int)end, repeats));
            }
            voice.Waveform = voice.PendingWaveform;
            EnqueueBlocks(voice, blocks, flags);
            return true;
        }
    }

    private static void EnqueueBlocks(VoiceState voice, List<SampleBlock> blocks, uint flags)
    {
        // Bit 0 promises further data; it does not mean "replace this queue".
        // A stopped voice starts a new sequence on its first submission. Further
        // submissions before Play belong to that sequence (intro + loop).
        if ((!voice.Playing && !voice.Paused && !voice.PreparingSequence) || voice.BlockIndex >= voice.Blocks.Count)
        { voice.Blocks.Clear(); voice.BlockIndex = 0; }
        if (!voice.Playing && !voice.Paused) voice.PreparingSequence = true;
        var empty = voice.Blocks.Count == 0;
        voice.Blocks.AddRange(blocks);
        voice.AwaitMoreBlocks = (flags & 1) != 0;
        if (blocks.Count != 0)
        {
            voice.Node.Channels = blocks[0].Channels;
            if (!voice.Playing && !voice.Paused)
            { voice.Pcm = blocks[0].Pcm; voice.SourceChannels = blocks[0].Channels; voice.SourceRate = blocks[0].Rate; }
            else if (empty || voice.WaitingForData) StartBlock(voice, voice.BlockIndex);
        }
    }

    private static void StartBlock(VoiceState voice, int index, double overshoot = 0)
    {
        voice.BlockIndex = index;
        if (index >= voice.Blocks.Count)
        {
            voice.WaitingForData = voice.AwaitMoreBlocks;
            voice.Playing = voice.AwaitMoreBlocks;
            return;
        }
        voice.WaitingForData = false;
        var block = voice.Blocks[index];
        voice.Pcm = block.Pcm; voice.SourceChannels = block.Channels; voice.SourceRate = block.Rate;
        voice.Position = block.Start + overshoot; voice.LoopStart = block.Start; voice.LoopEnd = block.End;
        voice.BlockRepeats = block.Repeats;
    }
}
