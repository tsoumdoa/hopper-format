using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    // Integral difference constraints, with an L1 movement objective. The dual is
    // a min-cost circulation: hard edges have unbounded capacity; the two edges
    // through zero price movement on either side of each preferred position.
    internal static class HorizontalConstraints
    {
        internal readonly struct Bound
        {
            internal readonly Guid From, To;
            internal readonly double Gap;
            internal Bound(Guid from, Guid to, double gap) { From = from; To = to; Gap = gap; }
        }

        private sealed class Arc
        {
            internal int From, To, Reverse;
            internal long Capacity;
            // Never decrement a hard edge. A large numeric capacity alone would
            // become finite after augmentation and eventually admit infeasibility.
            internal bool Unbounded;
            internal double Cost;
        }

        internal static Dictionary<Guid, double> Solve(IEnumerable<Guid> ids, IEnumerable<Bound> bounds)
        {
            var keys = new[] { Guid.Empty }.Concat(ids.OrderBy(id => id)).Distinct().ToArray();
            var index = keys.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
            var arcs = new List<Arc>();
            const long infinite = long.MaxValue / 4;
            void Add(int from, int to, double cost, long capacity)
            {
                int a = arcs.Count;
                arcs.Add(new Arc { From = from, To = to, Cost = cost, Capacity = capacity, Unbounded = capacity == infinite, Reverse = a + 1 });
                arcs.Add(new Arc { From = to, To = from, Cost = -cost, Reverse = a });
            }
            foreach (var b in bounds.OrderBy(b => b.From).ThenBy(b => b.To).ThenBy(b => b.Gap))
                Add(index[b.From], index[b.To], -Math.Ceiling(b.Gap), infinite);
            for (int i = 1; i < keys.Length; i++) { Add(0, i, 0, 1); Add(i, 0, 0, 1); }
            while (true)
            {
                var distance = new double[keys.Length];
                var previous = Enumerable.Repeat(-1, keys.Length).ToArray();
                int changed = -1;
                for (int pass = 0; pass < keys.Length; pass++)
                {
                    changed = -1;
                    for (int a = 0; a < arcs.Count; a++)
                    {
                        var edge = arcs[a];
                        if (edge.Capacity == 0 || distance[edge.To] <= distance[edge.From] + edge.Cost) continue;
                        distance[edge.To] = distance[edge.From] + edge.Cost;
                        previous[edge.To] = a; changed = edge.To;
                    }
                    if (changed < 0) break;
                }
                if (changed < 0) return keys.Skip(1).ToDictionary(id => id, id => distance[0] - distance[index[id]]);
                for (int i = 0; i < keys.Length; i++) changed = arcs[previous[changed]].From;
                int at = changed;
                long amount = infinite;
                do { var edge = arcs[previous[at]]; if (!edge.Unbounded) amount = Math.Min(amount, edge.Capacity); at = edge.From; } while (at != changed);
                if (amount == infinite) return null;
                do
                {
                    var edge = arcs[previous[at]];
                    if (!edge.Unbounded) edge.Capacity -= amount;
                    if (!arcs[edge.Reverse].Unbounded) arcs[edge.Reverse].Capacity += amount;
                    at = edge.From;
                } while (at != changed);
            }
        }
    }
}
