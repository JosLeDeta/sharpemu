// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2PacingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(21, 0)]
    [InlineData(27, 5.6666666667)]
    [InlineData(50, 28.6666666667)]
    public void PacingPreservesFourGrainsAndAllowsUnderrunRecovery(int queuedMs, double expectedDelay)
    {
        Assert.Equal(expectedDelay,
            AudioOut2Exports.GetQueuePacingDelayMilliseconds(queuedMs, 256, 48000, 4), 6);
    }

    [Fact]
    public void LargerGrainsUseTheirActualPlaybackDuration()
    {
        Assert.Equal(0, AudioOut2Exports.GetQueuePacingDelayMilliseconds(80, 1024, 48000, 4));
        Assert.Equal(14.6666666667,
            AudioOut2Exports.GetQueuePacingDelayMilliseconds(100, 1024, 48000, 4), 6);
    }
}
