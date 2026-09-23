// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private const int OrbisNgs2ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int OrbisNgs2ErrorInvalidSystemHandle = unchecked((int)0x804A0230);
    private const int OrbisNgs2ErrorInvalidRackHandle = unchecked((int)0x804A0261);
    private const int OrbisNgs2ErrorInvalidVoiceHandle = unchecked((int)0x804A0300);
    private const ulong HandleStorageSize = 0x20;
    private const int RenderBufferInfoSize = 0x18;
    private const ulong MaximumRenderBufferSize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
    private static long _nextUid;
    private static long _renderCount;

    // NGS2 advances one grain per system render; the device format is selected
    // independently for each mastering output.
    private const int DefaultGrainSamples = 256;
    private const int DefaultSampleRate = 48000;

    private sealed class SystemState
    {
        public SystemState(uint uid) => Uid = uid;

        public uint Uid { get; }
        public int GrainSamples { get; set; } = DefaultGrainSamples;
        public int SampleRate { get; set; } = DefaultSampleRate;
        public Ngs2Mixer Mixer { get; } = new();
        public ulong CallbackScratch;
        public FileStream? Capture;
        public int CaptureFrames;
        public bool CaptureDone;
        public bool Rendering;
        public int LockOwner, LockDepth;
        public byte[] CallbackPlane { get; } = new byte[8192 * 4];
    }

    private sealed record RackState(ulong SystemHandle, uint RackId);

    private sealed class VoiceState
    {
        public VoiceState(ulong rackHandle, uint voiceIndex)
        {
            RackHandle = rackHandle;
            VoiceIndex = voiceIndex;
        }

        public ulong RackHandle { get; }
        public uint VoiceIndex { get; }
        public Ngs2Mixer.Node Node { get; set; } = null!;
        public double TotalSamples { get; set; }
        public float Pitch { get; set; } = 1;
        public SortedDictionary<uint, Biquad> Filters { get; } = new();
        public ulong ProcessCallback, CallbackData0, CallbackData1, CallbackData2;
        public bool CallbackFailed;
        public WaveformInfo Waveform;
        public List<SampleBlock> Blocks { get; } = new();
        public int BlockIndex;
        public uint BlockRepeats;

        // Software-mixer playback state. Pcm is decoded, interleaved PCM;
        // Position is a fractional read cursor advanced at the source/output rate
        // ratio each output frame.
        public short[]? Pcm { get; set; }
        public ulong SourceAddr { get; set; }
        public int SourceRate { get; set; }
        public double Position { get; set; }
        public bool Playing { get; set; }
        public bool Paused { get; set; }
        public int LoopStart { get; set; } = -1;
        public int LoopEnd { get; set; }
        public float Gain { get; set; } = 1f;

        public int SourceChannels { get; set; } = 1;
        public uint FormatSampleRate { get; set; }
        public uint FormatType, FormatChannels, FormatConfig;
        public bool PreparingSequence, WaitingForData, AwaitMoreBlocks;
        public short[]? PendingPcm;
        public int PendingChannels, PendingRate;
        public WaveformInfo PendingWaveform;
        public LibAtrac9.Atrac9Decoder? StreamingDecoder;
        public byte[] CompressedTail = [];
        public uint StreamingSkip;
        public ulong ElapsedSamples(long now) => (ulong)Math.Max(0, TotalSamples);
    }
    private const uint VoiceStateFlagInUse = 0x1;
    private const uint VoiceStateFlagPlaying = 0x2;
    private const uint VoiceStateFlagStopped = 0x8;
    private static long _voiceStateTraces;
    private static long _voiceParamTraces;

    [SysAbiExport(Nid = "AQkj7C0f3PY", ExportName = "sceNgs2SystemResetOption",
        Target = Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2SystemResetOption(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0) return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        // Native layout: size, name[64], scheduler[4], flags, grain limits,
        // sample rate, channel limit and five reserved words (144 bytes).
        Span<byte> option = stackalloc byte[144];
        option.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(option, (ulong)option.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(option[108..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(option[112..], DefaultGrainSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(option[116..], DefaultSampleRate);
        return SetReturn(ctx, ctx.Memory.TryWrite(address, option) ? 0 :
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "mPYgU4oYpuY",
        ExportName = "sceNgs2SystemCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreateWithAllocator(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 1, ownerHandle: 0, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Systems[handle] = new SystemState(unchecked((uint)Interlocked.Increment(ref _nextUid)));
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator create: identical to the WithAllocator form for our purposes.
    // The only signature difference is the caller-supplied buffer info in rsi
    // (vs an allocator callback); the system option (rdi) and out-handle (rdx)
    // sit at the same argument positions, so we reuse the same implementation.
    // Dead Cells uses these variants — leaving sceNgs2SystemCreate unresolved
    // gave the game a garbage system handle, so every later rack/voice call
    // failed and it polled sceNgs2VoiceGetState forever, freezing at FLIP 0.
    [SysAbiExport(
        Nid = "koBbCMvOKWw",
        ExportName = "sceNgs2SystemCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreate(CpuContext ctx) => Ngs2SystemCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "u-WrYDaJA3k",
        ExportName = "sceNgs2SystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Systems.Remove(handle, out var removedSystem))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            removedSystem.Capture?.Dispose();
            var rackHandles = Racks
                .Where(pair => pair.Value.SystemHandle == handle)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var rackHandle in rackHandles)
            {
                RemoveRackLocked(rackHandle);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "U546k6orxQo",
        ExportName = "sceNgs2RackCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreateWithAllocator(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.R8];
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 2, systemHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Racks[handle] = new RackState(systemHandle, rackId);
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator rack create: system handle (rdi), rack id (rsi) and the
    // out-handle (r8) share the WithAllocator argument layout, so reuse it.
    [SysAbiExport(
        Nid = "cLV4aiT9JpA",
        ExportName = "sceNgs2RackCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreate(CpuContext ctx) => Ngs2RackCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "lCqD7oycmIM",
        ExportName = "sceNgs2RackDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            RemoveRackLocked(handle);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "MwmHz8pAdAo",
        ExportName = "sceNgs2RackGetVoiceHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetVoiceHandle(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var voiceIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(rackHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            var existing = Voices.FirstOrDefault(
                pair => pair.Value.RackHandle == rackHandle && pair.Value.VoiceIndex == voiceIndex);
            if (existing.Key != 0)
            {
                return ctx.TryWriteUInt64(outHandleAddress, existing.Key)
                    ? SetReturn(ctx, 0)
                    : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 4, rackHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            var voice = new VoiceState(rackHandle, voiceIndex);
            var rack = Racks[rackHandle];
            voice.Node = new Ngs2Mixer.Node(handle) { Master = rack.RackId == 0x3000, Output = voiceIndex };
            voice.Node.Source = (buffer, frames, channels, rate) =>
            {
                if (voice.Playing && voice.Pcm is { Length: > 0 }) MixOneVoice(buffer, frames, channels, rate, voice);
            };
            Systems[rack.SystemHandle].Mixer.Add(voice.Node);
            Voices[handle] = voice;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "uu94irFOGpA",
        ExportName = "sceNgs2VoiceControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceControl(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var paramList = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Voices.ContainsKey(voiceHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
        }

        if (ShouldTrace() && (Interlocked.Increment(ref _voiceParamTraces) <= 128 ||
            (Volatile.Read(ref _voiceParamTraces) & 4095) == 0))
        {
            TraceVoiceParamList(ctx, voiceHandle, paramList);
        }

        lock (StateGate) return SetReturn(ctx, HandleVoiceParams(ctx, voiceHandle, paramList));
    }

    // Legacy parameter lists have a u16 size / i16 relative next / u32 id.
    private static int HandleVoiceParams(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        var offset = paramList;
        var result = 0;
        for (var guard = 0; guard < 256 && offset != 0; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) || size < 8 || size > 4096 ||
                !ctx.TryReadUInt16(offset + 2, out var next) ||
                !ctx.TryReadUInt32(offset + 4, out var id)) return InvalidAudioArgument;
            if (id == 0x10000000 && size >= 40) ApplySamplerSetupParam(ctx, voiceHandle, offset);
            else if (id == 0x10000001 && size >= 32)
            {
                if (!ApplyWaveformParam(ctx, voiceHandle, offset))
                    result = OrbisNgs2ErrorInvalidWaveformData;
            }
            else if (!ApplyRoutingParam(ctx, voiceHandle, offset, size, id)) result = InvalidAudioArgument;
            if (result != 0 && UnsupportedCommands.Add(((ulong)id << 32) | size))
                Console.Error.WriteLine($"[LOADER][WARN] ngs2.parameter_rejected id=0x{id:X8} size={size} voice=0x{voiceHandle:X}");
            if (next == 0) return result;
            offset = unchecked((ulong)((long)offset + (short)next));
        }
        return InvalidAudioArgument;
    }

    // Sampler setup param: head (8) + SceNgs2WaveformFormat (32). Keeps the
    // source format sample rate used by the decoder.
    private static void ApplySamplerSetupParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt32(paramOffset + 8 + 0x08, out var sampleRate))
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var voice))
            {
                voice.FormatSampleRate = sampleRate;
                ctx.TryReadUInt32(paramOffset + 8, out voice.FormatType);
                ctx.TryReadUInt32(paramOffset + 12, out voice.FormatChannels);
                ctx.TryReadUInt32(paramOffset + 20, out voice.FormatConfig);
                voice.StreamingDecoder = null; voice.CompressedTail = []; voice.StreamingSkip = 0;
            }
        }
    }

    // Waveform-blocks param: head (8), const void* data (+8), uint32 flags
    // (+16), uint32 numBlocks (+20), const SceNgs2WaveformBlock* (+24).
    // VAG and supported RIFF codecs are decoded to actual sample frames.
    // Loading data does not implicitly start a stopped voice.
    private static bool ApplyWaveformParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt64(paramOffset + 8, out var dataAddr) || dataAddr <= 0x10000)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[Ngs2VagDecoder.VagHeaderSize];
        if (!ctx.Memory.TryRead(dataAddr, header))
        {
            return false;
        }

        if (!Ngs2VagDecoder.IsVag(header))
        {
            if (header[..4].SequenceEqual("RIFF"u8))
                return ArmDecodedRiff(ctx, voiceHandle, dataAddr) && ConfigureBlocks(ctx, voiceHandle, paramOffset);
            lock (StateGate)
                return Voices.TryGetValue(voiceHandle, out var rawVoice) && (rawVoice.FormatType == 13
                    ? ApplyRawAtrac9(ctx, rawVoice, paramOffset, dataAddr) : ApplyRawPcm(ctx, rawVoice, paramOffset, dataAddr));
        }

        var declaredSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header[0x0C..]);
        var totalBytes = Ngs2VagDecoder.VagHeaderSize + Math.Clamp(declaredSize, 0, 8 * 1024 * 1024);
        var raw = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            if (!ctx.Memory.TryRead(dataAddr, raw.AsSpan(0, totalBytes)) ||
                !Ngs2VagDecoder.TryDecode(raw.AsSpan(0, totalBytes), out var waveform))
            {
                return false;
            }

            lock (StateGate)
            {
                if (!Voices.TryGetValue(voiceHandle, out var voice))
                {
                    return false;
                }

                voice.PendingPcm = waveform.Samples;
                voice.PendingWaveform = ParseVagWaveform(ctx, dataAddr, (ulong)totalBytes, header);
                voice.SourceAddr = dataAddr;
                voice.PendingRate = waveform.SampleRate;
                voice.PendingChannels = 1;
            }

            if (ShouldTrace())
            {
                var peak = 0;
                for (var i = 0; i < waveform.Samples.Length; i++)
                {
                    peak = Math.Max(peak, Math.Abs((int)waveform.Samples[i]));
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.arm voice=0x{voiceHandle:X16} addr=0x{dataAddr:X} rate={waveform.SampleRate} samples={waveform.Samples.Length} loop={waveform.LoopStart} peak={peak}");
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
        return ConfigureBlocks(ctx, voiceHandle, paramOffset);
    }

    // Empirically dump the SceNgs2VoiceParamHead-chained command list so we can
    // confirm the real struct layout (size/next/id) against public NGS2 sources
    // before building the software mixer. Assumed header: u16 size, s16 next
    // (byte offset to the next block, 0 = end), u32 id.
    private static void TraceVoiceParamList(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        Span<byte> peek = stackalloc byte[32];
        var offset = paramList;
        for (int guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) ||
                !ctx.TryReadUInt16(offset + 2, out var next) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} @0x{offset:X}: unreadable header");
                return;
            }

            peek.Clear();
            // Always dump the full 32-byte window: the size word alone does
            // not tell the layout apart (u32 size/id vs u16 size/next).
            var readable = peek.Length;
            ctx.Memory.TryRead(offset, peek[..readable]);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} id=0x{id:X} size={size} next={unchecked((short)next)} " +
                $"rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{ctx[CpuRegister.Rcx]:X} bytes={Convert.ToHexString(peek[..readable])}");

            // For the waveform-blocks param, follow the embedded pointers and
            // dump the pointed-to bytes so we can tell PCM16 from ATRAC9.
            if (id == 0x10000001 && Interlocked.Increment(ref _waveformDumps) <= 8)
            {
                for (int po = 8; po + 8 <= readable; po += 8)
                {
                    if (ctx.TryReadUInt64(offset + (ulong)po, out var ptr) && ptr > 0x10000 &&
                        ctx.Memory.TryRead(ptr, peek))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] ngs2.waveform @+{po} ptr=0x{ptr:X} head={Convert.ToHexString(peek)}");
                    }
                }
            }

            var advance = unchecked((short)next);
            if (advance <= 0)
            {
                return;
            }

            offset += (ulong)advance;
        }
    }

    private static long _waveformDumps;
    private static long _renderInfoDumps;

    [SysAbiExport(
        Nid = "AbYvTOZ8Pts",
        ExportName = "sceNgs2VoiceRunCommands",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceRunCommands(CpuContext ctx) => RunVoiceCommands(ctx);

    [SysAbiExport(
        Nid = "i0VnXM-C9fc",
        ExportName = "sceNgs2SystemRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRender(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var bufferInfoAddress = ctx[CpuRegister.Rsi];
        var bufferInfoCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (bufferInfoCount != 0 && bufferInfoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (StateGate)
        {
            var system = Systems[systemHandle];
            if (system.Rendering) return SetReturn(ctx, InvalidAudioArgument);
            system.Rendering = true;
            try { system.Mixer.Render(system.GrainSamples, system.SampleRate, (node, frames, rate) => ProcessVoiceDsp(ctx, node, frames, rate)); }
            finally { system.Rendering = false; }
        }
        Span<byte> renderBufferInfo = stackalloc byte[RenderBufferInfoSize];
        for (uint i = 0; i < bufferInfoCount; i++)
        {
            var entryAddress = bufferInfoAddress + (i * RenderBufferInfoSize);
            if (!ctx.TryReadUInt64(entryAddress, out var bufferAddress) ||
                !ctx.TryReadUInt64(entryAddress + 8, out var bufferSize))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (bufferAddress != 0 && bufferSize != 0)
            {
                if (bufferSize > MaximumRenderBufferSize || !TryClearGuestBuffer(ctx, bufferAddress, bufferSize))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                // SceNgs2RenderBufferInfo: {ptr@0, size@8, waveformType@16,
                // channelsCount@20}. Mix the armed voices into the leading grain
                // as interleaved float32 — this is what the game copies to
                // sceAudioOutOutput, so it is where NGS2 audio must appear.
                var channels = 2;
                if (ctx.TryReadUInt32(entryAddress + 20, out var declaredChannels) &&
                    declaredChannels is > 0 and <= 8)
                {
                    channels = (int)declaredChannels;
                }

                ctx.TryReadUInt32(entryAddress + 16, out var outputType);
                if (outputType is not (2 or 8 or 18 or 28))
                    return SetReturn(ctx, OrbisNgs2ErrorInvalidWaveformData);
                MixVoicesIntoGrain(ctx, systemHandle, bufferAddress, bufferSize, channels, outputType is 2 or 18, i);

                if (ShouldTrace() && Interlocked.Increment(ref _renderInfoDumps) <= 4)
                {
                    ctx.Memory.TryRead(entryAddress, renderBufferInfo);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] ngs2.renderbufinfo addr=0x{bufferAddress:X} size={bufferSize} ch={channels} raw={Convert.ToHexString(renderBufferInfo)}");
                }
            }
        }

        var count = Interlocked.Increment(ref _renderCount);
        if (ShouldTrace() && (count <= 4 || count % 200 == 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.render#{count} system=0x{systemHandle:X16} buffers={bufferInfoCount}");
        }

        return SetReturn(ctx, 0);
    }

    private static void MixVoicesIntoGrain(
        CpuContext ctx, ulong systemHandle, ulong bufferAddress, ulong bufferSize, int channels, bool int16, uint output)
    {
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system)) return;
            var frames = (int)Math.Min((ulong)system.GrainSamples, bufferSize / (ulong)(channels * (int16 ? 2 : 4)));
            if (frames == 0) return;
            var count = frames * channels;
            var accum = ArrayPool<float>.Shared.Rent(count);
            try
            {
                system.Mixer.ReadOutput(output, accum.AsSpan(0, count), channels);
                if (output == 0) DiagnoseOutput(system, accum.AsSpan(0, count), channels);
                WriteGrain(ctx, bufferAddress, accum, count, int16);
            }
            finally { ArrayPool<float>.Shared.Return(accum); }
        }
    }

    // Resample one voice into its native channels. Routing follows separately.
    // Advances the voice cursor and handles loop /
    // one-shot end. Must be called under StateGate.
    private static void MixOneVoice(
        float[] accum,
        int frames,
        int channels,
        int outputSampleRate,
        VoiceState voice)
    {
        for (var f = 0; f < frames; f++)
        {
            while (voice.Playing && !voice.WaitingForData && voice.Position >= voice.LoopEnd)
            {
                var overshoot = voice.Position - voice.LoopEnd;
                if (voice.BlockRepeats > 0)
                {
                    if (voice.BlockRepeats != uint.MaxValue) voice.BlockRepeats--;
                    voice.Position = voice.LoopStart + overshoot;
                }
                else StartBlock(voice, voice.BlockIndex + 1, overshoot);
            }
            if (!voice.Playing || voice.WaitingForData || voice.Pcm is null || voice.LoopEnd <= voice.LoopStart) break;
            var pcm = voice.Pcm;
            var idx = (int)voice.Position;
            if (idx < 0 || idx >= pcm.Length / voice.SourceChannels) { voice.Playing = false; break; }
            var next = idx + 1;
            if (next >= voice.LoopEnd) next = voice.BlockRepeats > 0 ? voice.LoopStart : idx;
            var fraction = voice.Position - idx;
            for (var channel = 0; channel < Math.Min(channels, voice.SourceChannels); channel++)
            {
                var a = pcm[idx * voice.SourceChannels + channel];
                var b = pcm[next * voice.SourceChannels + channel];
                accum[f * channels + channel] += (float)((a + (b - a) * fraction) * (voice.Gain / 32768f));
            }
            var step = voice.SourceRate * (double)voice.Pitch / outputSampleRate;
            voice.Position += step;
            voice.TotalSamples += step;
        }
    }

    private static void WriteGrain(CpuContext ctx, ulong address, float[] accum, int count, bool int16)
    {
        var width = int16 ? 2 : 4;
        var bytes = ArrayPool<byte>.Shared.Rent(count * width);
        try
        {
            var span = bytes.AsSpan(0, count * width);
            for (var i = 0; i < count; i++)
            {
                var value = float.IsFinite(accum[i]) ? Math.Clamp(accum[i], -1f, 1f) : 0f;
                if (int16) BinaryPrimitives.WriteInt16LittleEndian(span[(i * 2)..], (short)Math.Clamp(value * 32768f, short.MinValue, short.MaxValue));
                else BinaryPrimitives.WriteSingleLittleEndian(span[(i * 4)..], value);
            }

            ctx.Memory.TryWrite(address, span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    [SysAbiExport(
        Nid = "pgFAiLR5qT4",
        ExportName = "sceNgs2SystemQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "0eFLVCfWVds",
        ExportName = "sceNgs2RackQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rdx]);

    // Report a fixed working-memory footprint for the requested object. The
    // out struct (SceNgs2BufferAllocator-style) begins with the size field.
    private static int WriteBufferSize(CpuContext ctx, ulong outAddress)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        Span<byte> info = stackalloc byte[RenderBufferInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0..8], 0x10000);
        BinaryPrimitives.WriteUInt64LittleEndian(info[8..16], 0x100);
        return ctx.Memory.TryWrite(outAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "l4Q2dWEH6UM",
        ExportName = "sceNgs2SystemSetGrainSamples",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetGrainSamples(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var grain = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            if (grain is <= 0 or > 8192) return SetReturn(ctx, InvalidAudioArgument);
            system.GrainSamples = grain;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "-tbc2SxQD60",
        ExportName = "sceNgs2SystemSetSampleRate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetSampleRate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var sampleRate = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            if (sampleRate is < 8000 or > 192000)
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            system.SampleRate = sampleRate;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "gThZqM5PYlQ",
        ExportName = "sceNgs2SystemLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemLock(CpuContext ctx)
    {
        Monitor.Enter(StateGate);
        if (!Systems.TryGetValue(ctx[CpuRegister.Rdi], out var system))
        {
            Monitor.Exit(StateGate);
            return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
        }
        system.LockOwner = Environment.CurrentManagedThreadId;
        system.LockDepth++;
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JXRC5n0RQls",
        ExportName = "sceNgs2SystemUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemUnlock(CpuContext ctx)
    {
        if (!Monitor.IsEntered(StateGate)) return SetReturn(ctx, InvalidAudioArgument);
        if (!Systems.TryGetValue(ctx[CpuRegister.Rdi], out var system))
            return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
        if (system.LockDepth == 0 || system.LockOwner != Environment.CurrentManagedThreadId)
            return SetReturn(ctx, InvalidAudioArgument);
        if (--system.LockDepth == 0) system.LockOwner = 0;
        Monitor.Exit(StateGate);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "-TOuuAQ-buE",
        ExportName = "sceNgs2VoiceGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetState(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        var stateSize = (int)Math.Min(ctx[CpuRegister.Rdx], 0x400);
        uint flags;
        ulong decodedSamples, decodedBytes, waveformAddress;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            flags = ComputeVoiceStateFlags(voice, now);
            decodedSamples = voice.ElapsedSamples(now);
            decodedBytes = voice.Waveform.NumFrameSamples == 0 ? 0 :
                decodedSamples / voice.Waveform.NumFrameSamples * voice.Waveform.FrameSize;
            waveformAddress = voice.SourceAddr;
        }

        if (ShouldTrace() && Interlocked.Increment(ref _voiceStateTraces) <= 64)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.voice_state voice=0x{voiceHandle:X16} size={stateSize} flags=0x{flags:X} decoded={decodedSamples}");
        }

        // SceNgs2SamplerVoiceState: stateFlags (+0), envelope/peak floats,
        // numDecodedSamples (+16), decodedDataSize (+24), userData (+32).
        if (stateAddress != 0 && stateSize > 0)
        {
            if (!TryClearGuestBuffer(ctx, stateAddress, (ulong)stateSize) ||
                !ctx.TryWriteUInt32(stateAddress, flags))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            // RDR requests 56 bytes and reads playback position at +24.
            var samplesOffset = ctx.TargetGeneration == Generation.Gen5 ? 24UL : 16UL;
            if ((ulong)stateSize >= samplesOffset + 8) ctx.TryWriteUInt64(stateAddress + samplesOffset, decodedSamples);
            if ((ulong)stateSize >= samplesOffset + 16) ctx.TryWriteUInt64(stateAddress + samplesOffset + 8, decodedBytes);
            if ((ulong)stateSize >= samplesOffset + 32) ctx.TryWriteUInt64(stateAddress + samplesOffset + 24, waveformAddress);
        }

        return SetReturn(ctx, 0);
    }

    private static uint ComputeVoiceStateFlags(VoiceState voice, long now)
    {
        // Flags follow playback driven by rendered frames, never wall-clock time.
        if (voice.Paused) return VoiceStateFlagInUse | 4;
        if (voice.Playing && voice.Pcm is not null)
        {
            return VoiceStateFlagInUse | VoiceStateFlagPlaying;
        }

        if (voice.Pcm is not null)
        {
            return VoiceStateFlagInUse | VoiceStateFlagStopped;
        }

        // Never armed: idle, all flags clear.
        return 0;
    }

    [SysAbiExport(
        Nid = "rEh728kXk3w",
        ExportName = "sceNgs2VoiceGetStateFlags",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetStateFlags(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var flagsAddress = ctx[CpuRegister.Rsi];
        uint flags;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }

            flags = ComputeVoiceStateFlags(voice, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        // The output is uint32_t; a wider store overwrites adjacent guest data.
        if (flagsAddress != 0 && !ctx.TryWriteUInt32(flagsAddress, flags))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    private static int ValidateSystem(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                Systems.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : OrbisNgs2ErrorInvalidSystemHandle);
        }
    }

    private static bool TryCreateHandle(CpuContext ctx, uint type, ulong ownerHandle, out ulong handle)
    {
        handle = 0;
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleStorageSize, 16, out handle))
        {
            return false;
        }

        Span<byte> data = stackalloc byte[(int)HandleStorageSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data[0..8], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..16], ownerHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..28], type);
        return ctx.Memory.TryWrite(handle, data);
    }

    private static bool TryClearGuestBuffer(CpuContext ctx, ulong address, ulong length)
    {
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        for (ulong offset = 0; offset < length;)
        {
            var chunkSize = (int)Math.Min((ulong)zeroes.Length, length - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..chunkSize]))
            {
                return false;
            }

            offset += unchecked((uint)chunkSize);
        }

        return true;
    }

    private static void RemoveRackLocked(ulong rackHandle)
    {
        Racks.Remove(rackHandle, out var removedRack);
        foreach (var voiceHandle in Voices
                     .Where(pair => pair.Value.RackHandle == rackHandle)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Voices.Remove(voiceHandle);
            if (removedRack is not null && Systems.TryGetValue(removedRack.SystemHandle, out var system))
                system.Mixer.Remove(voiceHandle);
        }
    }

    private static bool ShouldTrace() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_NGS2"),
            "1",
            StringComparison.Ordinal);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
    [SysAbiExport(
        Nid = "xa8oL9dmXkM",
        ExportName = "sceNgs2PanInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2PanInit(CpuContext ctx) => InitializePan(ctx);

    [SysAbiExport(
        Nid = "1WsleK-MTkE",
        ExportName = "sceNgs2GeomCalcListener",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomCalcListener(CpuContext ctx) => CalculateListener(ctx);

    [SysAbiExport(
        Nid = "0lbbayqDNoE",
        ExportName = "sceNgs2GeomResetSourceParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetSourceParam(CpuContext ctx) => ResetSource(ctx);

    [SysAbiExport(
        Nid = "7Lcfo8SmpsU",
        ExportName = "sceNgs2GeomResetListenerParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetListenerParam(CpuContext ctx) => ResetListener(ctx);

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
            if (offset > uint.MaxValue || length > uint.MaxValue) return SetReturn(ctx, InvalidAudioArgument);
            BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], (uint)length);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(block[(field + 4)..], (uint)(sampleStart % frameSamples));
        BinaryPrimitives.WriteUInt32LittleEndian(block[(field + 8)..], numSamples);
        return SetReturn(ctx, ctx.Memory.TryWrite(outBlockAddress, block) ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }
}
