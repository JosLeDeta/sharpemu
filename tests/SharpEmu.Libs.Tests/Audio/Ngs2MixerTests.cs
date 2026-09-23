// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Ngs2MixerTests
{
    [Fact]
    public void BiquadMaintainsIndependentChannelHistoryAcrossGrains()
    {
        var filter = new Ngs2Exports.Biquad { Coefficients = [.5f, .5f, 0, 0, 0] };
        float[] first = [1, 0, 0, 1];
        filter.Process(first, 2);
        Assert.Equal(new float[] { .5f, 0, .5f, .5f }, first);
        float[] second = [0, 0];
        filter.Process(second, 2);
        Assert.Equal(new float[] { 0, .5f }, second);
        filter.Reset();
        Array.Clear(second); filter.Process(second, 2);
        Assert.Equal(new float[] { 0, 0 }, second);
    }

    private static Ngs2Mixer.Node Node(Ngs2Mixer mixer, ulong id, int channels, bool master = false)
    {
        var node = new Ngs2Mixer.Node(id) { Channels = channels, Master = master, Enabled = true };
        mixer.Add(node);
        return node;
    }

    [Fact]
    public void RoutesThroughBusWithIndependentSendsAndAdvancesSourceOnce()
    {
        var mixer = new Ngs2Mixer();
        var master = Node(mixer, 3, 2, true); // Deliberately created before dependencies.
        var bus = Node(mixer, 2, 2);
        var voice = Node(mixer, 1, 1);
        var advances = 0;
        voice.Source = (samples, _, _, _) => { advances++; Array.Fill(samples, .5f); };
        Assert.True(mixer.Patch(1, 0, 2, 0));
        Assert.True(mixer.Patch(1, 1, 3, 0));
        Assert.True(mixer.Patch(2, 0, 3, 0));
        voice.Matrices[0] = [0, 1]; // First send exclusively right, through bus.
        voice.GetPort(0).Matrix = 0;
        voice.GetPort(0).Volume = .5f;
        voice.Matrices[1] = [1, 0]; // Second send exclusively left, direct.
        voice.GetPort(1).Matrix = 1;
        voice.GetPort(1).Volume = .25f;
        bus.GetPort(0).Volume = .5f;
        mixer.Render(2, 48000);
        var output = new float[4];
        mixer.ReadOutput(0, output, 2);
        Assert.Equal(new float[] { .125f, .125f, .125f, .125f }, output);
        Assert.Equal(1, advances);
        mixer.ReadOutput(0, output, 2);
        Assert.Equal(1, advances); // Reading a second output never advances playback.
        bus.GetPort(0).Volume = 0;
        mixer.Render(2, 48000);
        mixer.ReadOutput(0, output, 2);
        Assert.Equal(new float[] { .125f, 0, .125f, 0 }, output);
    }

    [Fact]
    public void DelayPersistsAcrossGrainsAndDisconnectRemovesOldHistory()
    {
        var mixer = new Ngs2Mixer();
        var source = Node(mixer, 1, 1);
        Node(mixer, 2, 1, true);
        var emitted = false;
        source.Source = (samples, _, _, _) => { if (!emitted) samples[0] = 1; emitted = true; };
        Assert.True(mixer.Patch(1, 0, 2, 0));
        source.GetPort(0).Delay = 3;
        var output = new float[2];
        mixer.Render(2, 48000); mixer.ReadOutput(0, output, 1);
        Assert.Equal(new float[] { 0, 0 }, output);
        mixer.Render(2, 48000); mixer.ReadOutput(0, output, 1);
        Assert.Equal(new float[] { 0, 1 }, output);
        Assert.True(mixer.Patch(1, 0, 0, 0));
        mixer.Render(2, 48000); mixer.ReadOutput(0, output, 1);
        Assert.Equal(new float[] { 0, 0 }, output);
    }

    [Fact]
    public void DisconnectedAndDisabledVoicesNeverBypassGraph()
    {
        var mixer = new Ngs2Mixer();
        var source = Node(mixer, 1, 1);
        var master = Node(mixer, 2, 1, true);
        source.Source = (samples, _, _, _) => Array.Fill(samples, .5f);
        var output = new float[1];
        mixer.Render(1, 48000); mixer.ReadOutput(0, output, 1);
        Assert.Equal(0, output[0]);
        Assert.True(mixer.Patch(1, 0, 2, 0));
        master.Enabled = false;
        mixer.Render(1, 48000); mixer.ReadOutput(0, output, 1);
        Assert.Equal(0, output[0]);
        master.Enabled = true;
        mixer.Render(1, 48000); mixer.ReadOutput(0, output, 1);
        Assert.Equal(.5f, output[0]);
    }

    [Fact]
    public void RejectsCyclesAndRemovalDisconnectsIncomingPorts()
    {
        var mixer = new Ngs2Mixer();
        var a = Node(mixer, 1, 1); Node(mixer, 2, 1); Node(mixer, 3, 1);
        Assert.True(mixer.Patch(1, 0, 2, 0)); Assert.True(mixer.Patch(2, 0, 3, 0));
        Assert.False(mixer.Patch(3, 0, 1, 0)); Assert.False(mixer.Patch(1, 1, 1, 0));
        Assert.False(mixer.Patch(1, 1, 99, 0));
        mixer.Remove(2);
        Assert.Equal(0UL, a.GetPort(0).Destination);
        mixer.Render(1, 48000);
    }

    [Fact]
    public void PreservesHeadroomUntilMasterAndDoesNotSumIndependentOutputs()
    {
        var mixer = new Ngs2Mixer();
        var a = Node(mixer, 1, 1); var b = Node(mixer, 2, 1);
        Node(mixer, 3, 1, true); var other = Node(mixer, 4, 1, true); other.Output = 1;
        a.Source = (samples, _, _, _) => samples[0] = 2;
        b.Source = (samples, _, _, _) => samples[0] = -1.5f;
        other.Source = (samples, _, _, _) => samples[0] = .25f;
        Assert.True(mixer.Patch(1, 0, 3, 0)); Assert.True(mixer.Patch(2, 0, 3, 0));
        mixer.Render(1, 48000);
        var output = new float[1];
        mixer.ReadOutput(0, output, 1); Assert.Equal(.5f, output[0]);
        mixer.ReadOutput(1, output, 1); Assert.Equal(.25f, output[0]);
    }
}
