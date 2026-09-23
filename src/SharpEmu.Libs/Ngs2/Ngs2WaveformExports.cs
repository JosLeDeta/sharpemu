// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    // SceNgs2WaveformInfo (0xD0 bytes): format (0x20) followed by the
    // data/loop/unit fields and up to four SceNgs2WaveformBlock (0x20 each).
    private const int WaveformInfoSize = 0xE8;
    private const int WaveformInfoMaxBlocks = 4;
    private const uint WaveformTypePcmI16Little = 2;
    private const uint WaveformTypePcmF32Little = 8;
    private const uint WaveformTypeVag = 12;
    private const uint WaveformTypeAtrac9 = 13;
    private const int OrbisNgs2ErrorInvalidWaveformData = unchecked((int)0x804A0100);
    private const int OrbisNgs2ErrorInvalidBufferAddress = unchecked((int)0x804A0050);
    private static long _unknownWaveformDumps;

    private readonly record struct WaveformInfo(
        uint Type,
        uint Channels,
        uint SampleRate,
        uint ConfigData,
        uint FrameSize,
        uint NumFrameSamples,
        uint DataOffset,
        uint DataSize,
        uint LoopBegin,
        uint LoopEnd,
        uint NumSamples, uint DelaySamples = 0);

    // Leaving this import unresolved crashed RDR in-game: the title used the
    // untouched output struct (garbage waveform type/offsets) and dereferenced
    // a null pointer a few instructions later.
    [SysAbiExport(
        Nid = "hyVLT2VlOYk",
        ExportName = "sceNgs2ParseWaveformData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformData(CpuContext ctx)
    {
        var dataAddress = ctx[CpuRegister.Rdi];
        var dataSize = ctx[CpuRegister.Rsi];
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (outInfoAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (dataAddress == 0 || dataSize < 16)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidBufferAddress);
        }

        // Only the headers are needed; cap what is read from guest memory.
        var headerLength = (int)Math.Min(dataSize, 4096);
        var header = ArrayPool<byte>.Shared.Rent(headerLength);
        try
        {
            var head = header.AsSpan(0, headerLength);
            if (!ctx.Memory.TryRead(dataAddress, head))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidBufferAddress);
            }

            WaveformInfo info;
            if (Ngs2VagDecoder.IsVag(head))
            {
                info = ParseVagWaveform(ctx, dataAddress, dataSize, head);
            }
            else if (!TryParseRiffWaveform(head, dataSize, out info))
            {
                if (Interlocked.Increment(ref _unknownWaveformDumps) <= 8)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] ngs2.parse_waveform unknown container addr=0x{dataAddress:X} " +
                        $"size={dataSize} head={Convert.ToHexString(head[..Math.Min(32, head.Length)])}");
                }

                return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformData);
            }

            return SetReturn(ctx, WriteWaveformInfo(ctx, outInfoAddress, info) ? 0 : OrbisNgs2ErrorInvalidOutAddress);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    // "VAGp": 48-byte big-endian header, 16-byte PS-ADPCM frames of 28
    // samples; loop points come from the per-frame flag byte.
    private static WaveformInfo ParseVagWaveform(CpuContext ctx, ulong dataAddress, ulong dataSize, ReadOnlySpan<byte> head)
    {
        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(head[0x0C..]);
        var sampleRate = BinaryPrimitives.ReadUInt32BigEndian(head[0x10..]);
        var channels = head.Length > 0x1E && head[0x1E] is > 0 and <= 8 ? (uint)head[0x1E] : 1u;
        var available = dataSize > Ngs2VagDecoder.VagHeaderSize
            ? dataSize - Ngs2VagDecoder.VagHeaderSize
            : 0;
        var adpcmSize = (uint)Math.Min(declaredSize == 0 ? available : Math.Min(declaredSize, available), int.MaxValue);
        adpcmSize -= adpcmSize % 16;
        var frameCount = adpcmSize / 16;
        var numSamples = frameCount * 28;

        var loopBegin = 0u;
        var loopEnd = 0u;
        var frames = ArrayPool<byte>.Shared.Rent((int)Math.Min(adpcmSize, 8 * 1024 * 1024));
        try
        {
            var scan = frames.AsSpan(0, (int)Math.Min(adpcmSize, 8 * 1024 * 1024));
            if (ctx.Memory.TryRead(dataAddress + Ngs2VagDecoder.VagHeaderSize, scan))
            {
                for (var frame = 0; frame * 16 + 16 <= scan.Length; frame++)
                {
                    var flags = scan[frame * 16 + 1];
                    if ((flags & 0x4) != 0 && loopEnd == 0)
                    {
                        loopBegin = (uint)frame * 28;
                    }

                    if ((flags & 0x3) == 0x3)
                    {
                        loopEnd = (uint)(frame + 1) * 28;
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frames);
        }

        return new WaveformInfo(
            WaveformTypeVag,
            channels,
            sampleRate == 0 ? 48000u : sampleRate,
            0,
            16,
            28,
            Ngs2VagDecoder.VagHeaderSize,
            adpcmSize,
            loopBegin,
            loopEnd,
            numSamples);
    }

    // RIFF/WAVE: PCM16, float32 and ATRAC9 (WAVE_FORMAT_EXTENSIBLE whose
    // sub-format starts with 0x42D2) containers; loop points from "smpl".
    private static bool TryParseRiffWaveform(ReadOnlySpan<byte> head, ulong dataSize, out WaveformInfo info)
    {
        info = default;
        if (head.Length < 12 ||
            BinaryPrimitives.ReadUInt32BigEndian(head) != 0x52494646u || // "RIFF"
            BinaryPrimitives.ReadUInt32BigEndian(head[8..]) != 0x57415645u) // "WAVE"
        {
            return false;
        }

        ushort formatTag = 0;
        uint channels = 0;
        uint sampleRate = 0;
        uint blockAlign = 0;
        uint bitsPerSample = 0;
        uint configData = 0;
        uint factSamples = 0;
        uint delaySamples = 0;
        uint dataOffset = 0;
        uint chunkDataSize = 0;
        uint loopBegin = 0;
        uint loopEnd = 0;
        var offset = 12;
        while (offset + 8 <= head.Length)
        {
            var chunkId = BinaryPrimitives.ReadUInt32BigEndian(head[offset..]);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(head[(offset + 4)..]);
            var body = offset + 8;
            var bodyAvailable = Math.Min((int)Math.Min(chunkSize, int.MaxValue), head.Length - body);
            switch (chunkId)
            {
                case 0x666D7420: // "fmt "
                    if (bodyAvailable < 16)
                    {
                        return false;
                    }

                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(head[body..]);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(head[(body + 2)..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(head[(body + 4)..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(head[(body + 12)..]);
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(head[(body + 14)..]);
                    if (formatTag == 0xFFFE && bodyAvailable >= 26)
                    {
                        formatTag = BinaryPrimitives.ReadUInt16LittleEndian(head[(body + 24)..]);
                        if (formatTag == 0x42D2 && bodyAvailable >= 48)
                        {
                            configData = BinaryPrimitives.ReadUInt32LittleEndian(head[(body + 44)..]);
                        }
                    }

                    break;
                case 0x66616374: // "fact"
                    if (bodyAvailable >= 4)
                    {
                        factSamples = BinaryPrimitives.ReadUInt32LittleEndian(head[body..]);
                        if (bodyAvailable >= 12) delaySamples = BinaryPrimitives.ReadUInt32LittleEndian(head[(body + 8)..]);
                    }

                    break;
                case 0x736D706C: // "smpl"
                    if (bodyAvailable >= 0x34 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(head[(body + 0x1C)..]) > 0)
                    {
                        loopBegin = BinaryPrimitives.ReadUInt32LittleEndian(head[(body + 0x2C)..]);
                        loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(head[(body + 0x30)..]) + 1;
                    }

                    break;
                case 0x64617461: // "data"
                    dataOffset = (uint)body;
                    chunkDataSize = chunkSize;
                    break;
            }

            if (dataOffset != 0)
            {
                break;
            }

            offset = body + (int)Math.Min(chunkSize + (chunkSize & 1), int.MaxValue - body);
        }

        if (dataOffset == 0 || channels == 0 || sampleRate == 0 || blockAlign == 0)
        {
            return false;
        }

        var available = dataSize > dataOffset ? dataSize - dataOffset : 0;
        var effectiveDataSize = (uint)Math.Min(Math.Min(chunkDataSize, available), int.MaxValue);
        uint type;
        uint frameSize;
        uint numFrameSamples;
        uint numSamples;
        switch (formatTag)
        {
            case 0x0001 when bitsPerSample == 16:
                type = WaveformTypePcmI16Little;
                frameSize = blockAlign;
                numFrameSamples = 1;
                numSamples = effectiveDataSize / blockAlign;
                break;
            case 0x0003 when bitsPerSample == 32:
                type = WaveformTypePcmF32Little;
                frameSize = blockAlign;
                numFrameSamples = 1;
                numSamples = effectiveDataSize / blockAlign;
                break;
            case 0x42D2:
                type = WaveformTypeAtrac9;
                frameSize = blockAlign;
                try
                {
                    var config = BitConverter.GetBytes(configData);
                    var codec = new LibAtrac9.Atrac9Config(config);
                    frameSize = (uint)codec.SuperframeBytes;
                    numFrameSamples = (uint)codec.SuperframeSamples;
                    if (codec.ChannelCount != channels || codec.SampleRate != sampleRate || frameSize != blockAlign) return false;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException) { return false; }
                numSamples = factSamples != 0 ? factSamples : effectiveDataSize / frameSize * numFrameSamples;
                if (loopEnd > 0)
                {
                    loopBegin = loopBegin >= delaySamples ? loopBegin - delaySamples : 0;
                    loopEnd = loopEnd >= delaySamples ? loopEnd - delaySamples : 0;
                }
                break;
            default:
                return false;
        }

        info = new WaveformInfo(
            type,
            channels,
            sampleRate,
            configData,
            frameSize,
            numFrameSamples,
            dataOffset,
            effectiveDataSize,
            loopBegin,
            loopEnd,
            numSamples, delaySamples);
        return true;
    }

    private static bool WriteWaveformInfo(CpuContext ctx, ulong address, WaveformInfo info)
    {
        Span<byte> buffer = stackalloc byte[WaveformInfoSize];
        buffer.Clear();
        // PS5: 24-byte format + 48-byte info + four 40-byte blocks.
        // Verified against RDR's 0xE8-byte copy and 64-bit block offset writes.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, info.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], info.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], info.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], info.ConfigData);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[20..], info.DelaySamples);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x18..], info.DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x1C..], info.DataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x20..], info.LoopBegin);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x24..], info.LoopEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x28..], info.NumSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x2C..], info.FrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x30..], info.NumFrameSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x34..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x38..], info.FrameSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x3C..], info.NumFrameSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x40..], info.DelaySamples);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x44..], 1);
        if (ctx.TargetGeneration == Generation.Gen5)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[0x48..], info.DataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[0x50..], info.DataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x5C..], info.DelaySamples);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x60..], info.NumSamples);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x48..], info.DataOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x4C..], info.DataSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x54..], info.DelaySamples);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[0x58..], info.NumSamples);
        }
        if (ShouldTrace())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.parse_waveform type={info.Type} ch={info.Channels} rate={info.SampleRate} " +
                $"data=+0x{info.DataOffset:X}/{info.DataSize} samples={info.NumSamples} loop={info.LoopBegin}..{info.LoopEnd}");
        }

        return ctx.Memory.TryWrite(address, buffer[..(ctx.TargetGeneration == Generation.Gen5 ? 0xE8 : 0xC8)]);
    }

    // sceNgs2CalcWaveformBlock(format, samplePosition, numSamples, outBlock):
    // byte range of a whole-frame span of the waveform.
    [SysAbiExport(
        Nid = "3pCNbVM11UA",
        ExportName = "sceNgs2CalcWaveformBlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2CalcWaveformBlock(CpuContext ctx)
    {
        var formatAddress = ctx[CpuRegister.Rdi];
        var samplePosition = (uint)ctx[CpuRegister.Rsi];
        var numSamples = (uint)ctx[CpuRegister.Rdx];
        var outBlockAddress = ctx[CpuRegister.Rcx];
        if (outBlockAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!ReadFormatUnit(ctx, formatAddress, out var frameSize, out var frameSamples, out var delay))
            return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformData);
        var sampleStart = (ulong)samplePosition + delay;
        var firstFrame = sampleStart / frameSamples;
        var lastFrame = (sampleStart + numSamples + frameSamples - 1) / frameSamples;
        Span<byte> block = stackalloc byte[ctx.TargetGeneration == Generation.Gen5 ? 40 : 32];
        block.Clear();
        var offset = firstFrame * frameSize;
        var length = (lastFrame - firstFrame) * frameSize;
        var field = 8;
        if (ctx.TargetGeneration == Generation.Gen5)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(block, offset);
            BinaryPrimitives.WriteUInt64LittleEndian(block[8..], length);
            field = 16;
        }
        else
        {
            if (offset > uint.MaxValue || length > uint.MaxValue) return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformData);
            BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], (uint)length);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(block[(field + 4)..], (uint)(sampleStart % frameSamples));
        BinaryPrimitives.WriteUInt32LittleEndian(block[(field + 8)..], numSamples);
        return SetReturn(ctx, ctx.Memory.TryWrite(outBlockAddress, block) ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }    private static bool ReadFormatUnit(CpuContext ctx, ulong address, out uint bytes, out uint samples, out uint delay)
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

}
