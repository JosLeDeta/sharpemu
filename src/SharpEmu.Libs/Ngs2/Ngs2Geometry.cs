// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Numerics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static int ResetListener(CpuContext ctx)
    {
        Span<byte> data = stackalloc byte[60];
        data.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(data[20..], 1); // front.z
        BinaryPrimitives.WriteSingleLittleEndian(data[28..], 1); // up.y
        BinaryPrimitives.WriteSingleLittleEndian(data[48..], 343);
        return SetReturn(ctx, ctx[CpuRegister.Rdi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], data)
            ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }

    private static int ResetSource(CpuContext ctx)
    {
        Span<byte> data = stackalloc byte[108];
        data.Clear();
        foreach (var offset in new[] { 32, 36, 44, 60, 64, 68, 72, 76, 80 })
            BinaryPrimitives.WriteSingleLittleEndian(data[offset..], 1);
        BinaryPrimitives.WriteSingleLittleEndian(data[40..], 360);
        BinaryPrimitives.WriteSingleLittleEndian(data[48..], 360);
        BinaryPrimitives.WriteSingleLittleEndian(data[56..], 1000000);
        BinaryPrimitives.WriteUInt32LittleEndian(data[92..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data[96..], 2);
        return SetReturn(ctx, ctx[CpuRegister.Rdi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], data)
            ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }

    private static int InitializePan(CpuContext ctx)
    {
        var count = ctx[CpuRegister.Rdx];
        ctx.GetXmmRegister(0, out var unitBits, out _);
        var unit = BitConverter.UInt32BitsToSingle((uint)unitBits);
        if (count is 0 or > 8 || !float.IsFinite(unit) || unit <= 0) return SetReturn(ctx, InvalidAudioArgument);
        Span<byte> data = stackalloc byte[40];
        data.Clear();
        if (ctx[CpuRegister.Rsi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], data[..((int)count * 4)]))
            return SetReturn(ctx, InvalidAudioArgument);
        for (var i = 0; i < (int)count; i++)
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..])))
                return SetReturn(ctx, InvalidAudioArgument);
        BinaryPrimitives.WriteSingleLittleEndian(data[32..], unit);
        BinaryPrimitives.WriteUInt32LittleEndian(data[36..], (uint)count);
        return SetReturn(ctx, ctx[CpuRegister.Rdi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], data)
            ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }

    internal static bool TryListenerTransform(Vector3 position, Vector3 front, Vector3 up, out Matrix4x4 matrix)
    {
        matrix = default;
        if (!Finite(position) || !Finite(front) || !Finite(up) || front.LengthSquared() < 1e-12f) return false;
        front = Vector3.Normalize(front);
        var right = Vector3.Cross(up, front);
        if (right.LengthSquared() < 1e-12f) return false;
        right = Vector3.Normalize(right);
        up = Vector3.Cross(front, right);
        // Row-vector world-to-listener transform. Orthonormalize to avoid
        // introducing gain/scale when guest orientation vectors drift.
        matrix = new Matrix4x4(right.X, up.X, front.X, 0, right.Y, up.Y, front.Y, 0,
            right.Z, up.Z, front.Z, 0, -Vector3.Dot(position, right),
            -Vector3.Dot(position, up), -Vector3.Dot(position, front), 1);
        return true;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));

    private static int CalculateListener(CpuContext ctx)
    {
        Span<byte> data = stackalloc byte[60];
        if (!ctx.Memory.TryRead(ctx[CpuRegister.Rdi], data)) return SetReturn(ctx, InvalidAudioArgument);
        var position = ReadVector(data); var front = ReadVector(data[12..]); var up = ReadVector(data[24..]);
        var velocity = ReadVector(data[36..]);
        var speed = BinaryPrimitives.ReadSingleLittleEndian(data[48..]);
        if (!Finite(velocity) || !float.IsFinite(speed) || speed <= 0 ||
            !TryListenerTransform(position, front, up, out var matrix)) return SetReturn(ctx, InvalidAudioArgument);
        Span<byte> output = stackalloc byte[96];
        output.Clear();
        System.Runtime.InteropServices.MemoryMarshal.Write(output, in matrix);
        data[36..52].CopyTo(output[64..80]);
        BinaryPrimitives.WriteUInt32LittleEndian(output[80..], (uint)ctx[CpuRegister.Rdx] & 1);
        return SetReturn(ctx, ctx[CpuRegister.Rsi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], output)
            ? 0 : OrbisNgs2ErrorInvalidOutAddress);
    }
}
