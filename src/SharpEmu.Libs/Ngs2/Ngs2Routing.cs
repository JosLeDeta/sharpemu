// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static readonly HashSet<ulong> UnsupportedCommands = new();
    private const int InvalidAudioArgument = unchecked((int)0x80020016);

    private static void VoiceEvent(VoiceState voice, uint command)
    {
        switch (command)
        {
            case 1:
                voice.PreparingSequence = false; voice.WaitingForData = false;
                voice.Paused = false;
                voice.Position = voice.TotalSamples = 0;
                foreach (var filter in voice.Filters.Values) filter.Reset();
                voice.Playing = voice.Node.Enabled = true;
                if (voice.Blocks.Count != 0) StartBlock(voice, 0);
                break;
            case 2: case 4: case 8:
                voice.PreparingSequence = false; voice.WaitingForData = false;
                voice.StreamingDecoder = null; voice.CompressedTail = []; voice.StreamingSkip = 0;
                voice.Paused = false;
                voice.Playing = voice.Node.Enabled = false;
                break;
            case 16:
                voice.Paused = true;
                voice.Playing = voice.Node.Enabled = false;
                break;
            case 32:
                voice.Paused = false;
                voice.Playing = voice.Node.Enabled = true;
                break;
        }
    }

    private static bool SetMatrix(CpuContext ctx, VoiceState voice, int index, uint count, ulong address)
    {
        if (index < 0 || index >= 256 || count is 0 or > 64) return false;
        Span<byte> bytes = stackalloc byte[(int)count * 4];
        if (!ctx.Memory.TryRead(address, bytes)) return false;
        // Copy at submission time: command arrays often point at temporary stacks.
        var values = new float[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
            if (!float.IsFinite(values[i])) return false;
        }
        voice.Node.Matrices[index] = values;
        return true;
    }

    private static void UnsupportedAudioCommand(ulong key)
    {
        if (UnsupportedCommands.Add(key))
            Console.Error.WriteLine($"[LOADER][WARN] ngs2.unsupported_command header=0x{key:X16}");
    }

    private static bool ApplyRoutingParam(CpuContext ctx, ulong handle, ulong address, ushort size, uint id)
    {
        lock (StateGate)
        {
            if (!Voices.TryGetValue(handle, out var voice)) return false;
            if (!ctx.TryReadUInt32(address + 8, out var arg)) return false;
            ctx.TryReadUInt32(address + 12, out var value);
            switch (id)
            {
                case 1 when size >= 24:
                    return ctx.TryReadUInt64(address + 16, out var levels) && SetMatrix(ctx, voice, (int)arg, value, levels);
                case 2 when size >= 16 && arg < 256:
                    var gain = BitConverter.UInt32BitsToSingle(value);
                    if (!float.IsFinite(gain)) return false;
                    voice.Node.GetPort(arg).Volume = gain;
                    return true;
                case 3 when size >= 16 && arg < 256:
                    if ((int)value is < -1 or >= 256) return false;
                    voice.Node.GetPort(arg).Matrix = (int)value;
                    return true;
                case 4 when size >= 16 && arg < 256:
                    if (value > 192000) return false;
                    voice.Node.GetPort(arg).Delay = (int)value;
                    return true;
                case 5 when size >= 24 && arg < 256:
                    return ctx.TryReadUInt64(address + 16, out var dest) &&
                        Systems[Racks[voice.RackHandle].SystemHandle].Mixer.Patch(handle, arg, dest, value);
                case 6 when size >= 12 && arg is 1 or 2 or 4 or 8 or 16 or 32:
                    VoiceEvent(voice, arg);
                    return true;
                case 0x10000005 when size >= 16:
                    var pitch = BitConverter.UInt32BitsToSingle(arg);
                    if (!float.IsFinite(pitch) || pitch <= 0 || pitch > 16) return false;
                    voice.Pitch = pitch;
                    return true;
                case 0x1000000A:
                    return SetBiquad(ctx, voice, address, size);
                case 0x20000004 when size >= 40:
                    if (!ctx.TryReadUInt64(address + 8, out var callback) ||
                        !ctx.TryReadUInt64(address + 16, out var user0) ||
                        !ctx.TryReadUInt64(address + 24, out var user1) ||
                        !ctx.TryReadUInt64(address + 32, out var user2)) return false;
                    voice.ProcessCallback = callback;
                    voice.CallbackData0 = user0; voice.CallbackData1 = user1; voice.CallbackData2 = user2;
                    voice.CallbackFailed = false;
                    return true;
                case 0x20000000 or 0x30000000 when size >= 16 && arg is > 0 and <= 8:
                    voice.Node.Channels = (int)arg;
                    return true;
                default:
                    UnsupportedAudioCommand(id);
                    return false;
            }
        }
    }

    // Compact commands observed in the RDR call sites: 16-byte records,
    // operation at byte 0, port/matrix at byte 3, value type at byte 5,
    // array count at bytes 6..7, and scalar/pointer payload at byte 8.
    // Do not reinterpret these records as a legacy size/next/id list.
    private static int RunVoiceCommands(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var count = ctx[CpuRegister.Rdx];
        if (count > 4096 || (count != 0 && address == 0)) return SetReturn(ctx, InvalidAudioArgument);
        lock (StateGate)
        {
            if (!Voices.TryGetValue(handle, out var voice)) return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            for (ulong i = 0; i < count; i++)
            {
                if (!ctx.TryReadUInt64(address + i * 16, out var header) ||
                    !ctx.TryReadUInt64(address + i * 16 + 8, out var payload))
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                var operation = (byte)header;
                var index = (byte)(header >> 24);
                var type = (byte)(header >> 40);
                var length = (ushort)(header >> 48);
                var value = (uint)payload;
                // Fields not yet identified must not silently alter the graph.
                if ((header & 0x000000FF00FFFF00UL) != 0)
                { UnsupportedAudioCommand(header); return SetReturn(ctx, InvalidAudioArgument); }
                switch (operation)
                {
                    case 2 when type == 4 && length == 0 && value is 1 or 2 or 4 or 8 or 16 or 32:
                        VoiceEvent(voice, value);
                        break;
                    case 5 when type == 0x11:
                        if (!SetMatrix(ctx, voice, index, length, payload)) return SetReturn(ctx, InvalidAudioArgument);
                        break;
                    case 6 when type == 1 && length == 0:
                        var level = BitConverter.UInt32BitsToSingle(value);
                        if (!float.IsFinite(level)) return SetReturn(ctx, InvalidAudioArgument);
                        voice.Node.GetPort(index).Volume = level;
                        break;
                    case 7 when type == 3 && length == 0 && (int)value is >= -1 and < 256:
                        voice.Node.GetPort(index).Matrix = (int)value;
                        break;
                    default:
                        UnsupportedAudioCommand(header);
                        return SetReturn(ctx, InvalidAudioArgument);
                }
            }
        }
        return SetReturn(ctx, 0);
    }
}
