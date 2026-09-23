// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static long _mixDiagnostics;
    private static void DiagnoseOutput(SystemState system, ReadOnlySpan<float> samples, int channels)
    {
        if (!ShouldTrace()) return;
        float peak = 0;
        double energy = 0;
        int clipped = 0, nonFinite = 0;
        Span<float> channelPeaks = stackalloc float[8];
        channelPeaks.Clear();
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (!float.IsFinite(sample)) { nonFinite++; continue; }
            var amplitude = Math.Abs(sample);
            peak = Math.Max(peak, amplitude);
            channelPeaks[i % channels] = Math.Max(channelPeaks[i % channels], amplitude);
            energy += sample * (double)sample;
            if (amplitude > 1) clipped++;
        }
        var n = Interlocked.Increment(ref _mixDiagnostics);
        if (n <= 8 || n % 200 == 0)
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[LOADER][TRACE] ngs2.mix#{n} peak={peak:F5} rms={Math.Sqrt(energy / Math.Max(1, samples.Length)):F5} clipped={clipped} nonfinite={nonFinite} channels=" ) +
                string.Join(',', channelPeaks[..channels].ToArray().Select(p => p.ToString("F5", CultureInfo.InvariantCulture))));
        var capturePath = Environment.GetEnvironmentVariable("SHARPEMU_NGS2_CAPTURE_PATH");
        if (system.CaptureDone || string.IsNullOrEmpty(capturePath)) return;
        if (system.Capture is null)
        {
            if (peak < .0001f) return;
            system.Capture = new FileStream(capturePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            Console.Error.WriteLine($"[LOADER][TRACE] ngs2.capture path={capturePath} format=f32le channels={channels} rate={system.SampleRate}");
        }
        var frames = Math.Min(samples.Length / channels, system.SampleRate * 10 - system.CaptureFrames);
        system.Capture.Write(MemoryMarshal.AsBytes(samples[..(frames * channels)]));
        system.CaptureFrames += frames;
        if (system.CaptureFrames >= system.SampleRate * 10)
        {
            system.Capture.Dispose(); system.Capture = null; system.CaptureDone = true;
            Console.Error.WriteLine("[LOADER][TRACE] ngs2.capture complete");
        }
    }
}
