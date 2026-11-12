using System;
using System.Collections.Generic;

namespace HopperFormat.Layout
{
    public readonly struct WireX
    {
        public readonly Guid From, To;
        public readonly double OutputGripX, InputGripX;
        public WireX(Guid from, double outputGripX, Guid to, double inputGripX)
        { From = from; OutputGripX = outputGripX; To = to; InputGripX = inputGripX; }
    }

    public readonly struct BackwardWireCounts
    {
        public readonly int Before, After, Created;
        public BackwardWireCounts(int before, int after, int created)
        { Before = before; After = after; Created = created; }
    }

    public static class BackwardWires
    {
        // One entry per physical wire. Missing displacements mean fixed objects.
        public static BackwardWireCounts Count(IEnumerable<WireX> wires, IReadOnlyDictionary<Guid, double> displacements)
        {
            int before = 0, after = 0, created = 0;
            foreach (var wire in wires)
            {
                if (!Graph.Finite(wire.OutputGripX) || !Graph.Finite(wire.InputGripX)) continue;
                displacements.TryGetValue(wire.From, out double fromDx);
                displacements.TryGetValue(wire.To, out double toDx);
                bool was = wire.OutputGripX > wire.InputGripX;
                bool now = wire.OutputGripX + fromDx > wire.InputGripX + toDx;
                if (was) before++;
                if (now) after++;
                if (!was && now) created++;
            }
            return new BackwardWireCounts(before, after, created);
        }
    }
}
