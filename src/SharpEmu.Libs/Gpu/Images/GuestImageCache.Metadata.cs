// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Surface metadata (HTile, DCC, CMask, FMask) keyed by its guest address.
public sealed partial class GuestImageCache
{
    public bool IsMetadata(ulong address)
    {
        using var held = _lock.Hold();
        return _surfaceMetadata.TryGetValue(address, out var found) && found.Kind != SurfaceMetadataKind.PendingDcc;
    }

    public bool IsMetadataCleared(ulong address, uint slice, out uint fillValue)
    {
        fillValue = 0;
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        fillValue = found.FillValue;
        return (found.ClearMask & (1u << (int)slice)) != 0;
    }

    public bool IsMetadataCleared(ulong address, uint slice) => IsMetadataCleared(address, slice, out _);

    // A broad clear applies to CMask, FMask and HTile; DCC needs a validated fill value.
    public bool ClearMetadata(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind is SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc)
        {
            return false;
        }

        found.ClearMask = uint.MaxValue;
        return true;
    }

    // True when registered DCC absorbed the fill and the guest dispatch can be skipped.
    public unsafe bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The DCC fill range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        // A DCC fill repeats one byte code; only the known deferred-clear codes count as clear.
        var code = (byte)fillValue;
        var dccClearMask = fillValue != code * 0x01010101u ? 0u : code switch
        {
            0x00 or 0x20 or 0x40 or 0x80 or 0xc0 => uint.MaxValue,
            _ => 0u,
        };
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found))
        {
            // The fill may precede color-target discovery; a pending entry stays invisible until then.
            _surfaceMetadata.Add(address, new SurfaceMetadata { Kind = SurfaceMetadataKind.PendingDcc, ClearMask = dccClearMask, FillValue = fillValue, FillSize = size });
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.PendingDcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.Dcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            // A later color-target bind must not clear work written by a compute
            // shader after this fill. Materialize the fixed zero clear at its
            // position in the guest queue when the image is already resident.
            if (code == 0x00 && dccClearMask != 0 && !_scheduler.Current.IsInvalid)
            {
                var command = _scheduler.Current;
                var native = new CommandBuffer(command.Handle);
                var cleared = false;
                _slots.ForEach((identifier, image) =>
                {
                    if (!image.Registered || !image.Backing.Exists || image.DepthOwner.IsValid ||
                        image.Description.Metadata.Kind != MetadataKind.Dcc ||
                        image.Description.Metadata.Range.Address != address)
                    {
                        return;
                    }

                    WatchImage(identifier);
                    ClearDccImage(image, native);
                    cleared = true;
                });
                if (cleared)
                {
                    found.ClearMask = 0;
                }
            }
            return true;
        }

        return false;
    }

    private unsafe void ClearDccImage(CachedImage image, CommandBuffer native)
    {
        _scheduler.Current.EndRendering();
        image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, native);
        var clear = default(ClearColorValue);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Vk.RemainingMipLevels, 0, image.Backing.Layers);
        _device.Vk.CmdClearColorImage(native, image.Backing.Handle, ImageLayout.TransferDstOptimal, &clear, 1, &range);
        TakeGpuOwnership(image);
    }

    public bool SetMetadataSlice(ulong address, uint slice, bool isClear)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) || found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        if (isClear)
        {
            found.ClearMask |= 1u << (int)slice;
        }
        else
        {
            found.ClearMask &= ~(1u << (int)slice);
        }

        return true;
    }
}
