// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Libs.Ngs2;

// A system owns its graph. Only mastering nodes reach the device; disconnected
// sources still advance, but never bypass the routing selected by the title.
internal sealed class Ngs2Mixer
{
    internal sealed class Port
    {
        internal ulong Destination;
        internal uint Input;
        internal int Matrix = -1;
        internal float Volume = 1;
        internal int Delay;
        internal float[] History = [];
        internal int Cursor;
    }

    internal sealed class Node(ulong handle)
    {
        internal readonly ulong Handle = handle;
        internal int Channels = 8;
        internal bool Enabled;
        internal bool Master;
        internal uint Output;
        internal readonly Dictionary<uint, Port> Ports = new();
        internal readonly Dictionary<int, float[]> Matrices = new();
        internal float[] Buffer = [];
        internal Action<float[], int, int, int>? Source;
        internal Port GetPort(uint id)
        {
            if (!Ports.TryGetValue(id, out var port)) Ports[id] = port = new Port();
            return port;
        }
    }

    internal readonly Dictionary<ulong, Node> Nodes = new();
    private Node[]? order;
    internal void Add(Node node) { Nodes.Add(node.Handle, node); order = null; }
    internal void Remove(ulong handle)
    {
        Nodes.Remove(handle);
        foreach (var node in Nodes.Values)
            foreach (var port in node.Ports.Values)
                if (port.Destination == handle) port.Destination = 0;
        order = null;
    }

    internal bool Patch(ulong source, uint portId, ulong destination, uint input)
    {
        if (!Nodes.TryGetValue(source, out var node) ||
            (destination != 0 && !Nodes.ContainsKey(destination))) return false;
        // No implicit one-grain feedback: reject a cycle instead of silently
        // making its latency depend on dictionary/insertion order.
        var pending = new Stack<ulong>();
        var seen = new HashSet<ulong>();
        if (destination != 0) pending.Push(destination);
        while (pending.TryPop(out var handle))
        {
            if (handle == source) return false;
            if (!seen.Add(handle)) continue;
            foreach (var p in Nodes[handle].Ports.Values)
                if (p.Destination != 0) pending.Push(p.Destination);
        }
        var port = node.GetPort(portId);
        port.Destination = destination;
        port.Input = input;
        port.History = [];
        port.Cursor = 0;
        order = null;
        return true;
    }

    internal void Render(int frames, int rate, Action<Node, int, int>? process = null)
    {
        if (order is null)
        {
            var incoming = Nodes.ToDictionary(p => p.Key, _ => 0);
            foreach (var node in Nodes.Values)
                foreach (var port in node.Ports.Values)
                    if (port.Destination != 0) incoming[port.Destination]++;
            var ready = new Queue<Node>(Nodes.Values.Where(n => incoming[n.Handle] == 0));
            var result = new List<Node>(Nodes.Count);
            while (ready.TryDequeue(out var node))
            {
                result.Add(node);
                foreach (var port in node.Ports.Values)
                    if (port.Destination != 0 && --incoming[port.Destination] == 0)
                        ready.Enqueue(Nodes[port.Destination]);
            }
            if (result.Count != Nodes.Count) throw new InvalidOperationException("Cyclic NGS2 graph");
            order = result.ToArray();
        }
        foreach (var node in order)
        {
            var length = checked(frames * node.Channels);
            if (node.Buffer.Length != length) node.Buffer = new float[length];
            else Array.Clear(node.Buffer);
        }
        foreach (var node in order)
        {
            if (!node.Enabled) { Array.Clear(node.Buffer); continue; }
            node.Source?.Invoke(node.Buffer, frames, node.Channels, rate);
            process?.Invoke(node, frames, rate);
            foreach (var port in node.Ports.Values)
            {
                if (port.Destination == 0) continue;
                var dest = Nodes[port.Destination];
                float[]? matrix = null;
                if (port.Matrix >= 0 && (!node.Matrices.TryGetValue(port.Matrix, out matrix) ||
                    matrix.Length != node.Channels * dest.Channels)) continue;
                var delayLength = checked(port.Delay * dest.Channels);
                if (port.History.Length != delayLength)
                { port.History = new float[delayLength]; port.Cursor = 0; }
                for (var frame = 0; frame < frames; frame++)
                    for (var output = 0; output < dest.Channels; output++)
                    {
                        float value = 0;
                        for (var input = 0; input < node.Channels; input++)
                            value += node.Buffer[frame * node.Channels + input] *
                                (matrix is null ? (input == output ? 1f : 0f) : matrix[input * dest.Channels + output]);
                        value *= port.Volume;
                        if (delayLength != 0)
                        {
                            var delayed = port.History[port.Cursor];
                            port.History[port.Cursor] = value;
                            value = delayed;
                            port.Cursor = (port.Cursor + 1) % delayLength;
                        }
                        dest.Buffer[frame * dest.Channels + output] += value;
                    }
            }
        }
    }

    internal void ReadOutput(uint output, Span<float> destination, int channels)
    {
        destination.Clear();
        foreach (var node in Nodes.Values)
        {
            if (!node.Master || !node.Enabled || node.Output != output) continue;
            var frames = Math.Min(destination.Length / channels, node.Buffer.Length / node.Channels);
            for (var frame = 0; frame < frames; frame++)
                for (var ch = 0; ch < Math.Min(channels, node.Channels); ch++)
                    destination[frame * channels + ch] += node.Buffer[frame * node.Channels + ch];
        }
    }
}
