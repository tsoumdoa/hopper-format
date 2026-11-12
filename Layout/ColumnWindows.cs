using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    // Independent horizontal plans shared by compaction and reconstruction.
    // Every island measures movement from its own current emitted left edge.
    internal static class ColumnWindows
    {
        internal const double MaxStretch = 150;
        internal const double MinClearance = 20;
        private const double Scale = 1048576; // exact binary subunits, never emitted as pivots
        private const long Cap = 150 * 1048576L;

        internal static bool HasOutsideX(Graph graph) =>
            graph.Snapshot.Inbound.Any(e => Graph.Finite(e.SourceGripX)) ||
            graph.Snapshot.Outbound.Any(e => Graph.Finite(e.TargetGripX));

        internal static bool HasOutsideX(Graph graph, IEnumerable<Node> island)
        {
            var ids = new HashSet<Guid>(island.Select(n => n.Id));
            return graph.Snapshot.Inbound.Any(e => ids.Contains(e.Node) && Graph.Finite(e.SourceGripX)) ||
                graph.Snapshot.Outbound.Any(e => ids.Contains(e.Node) && Graph.Finite(e.TargetGripX));
        }

        // Only emitted node tops define the old edge. Do not add widths,
        // alignment edges, group padding or raw (unrounded) column positions.
        internal static double OldLeft(IEnumerable<Node> island) => island.Min(n => n.CurrentTop.X);

        internal sealed class Geometry
        {
            internal Node[][] Columns;
            internal double[] Packed, Left;
            internal Dictionary<Guid, double> Indent = new Dictionary<Guid, double>();
            internal List<Bound>[] Bounds;
        }

        internal static Geometry Place(Graph graph, Node[][] columns, double anchorX, bool unconnected = false)
        {
            var g = Build(graph, columns, anchorX, unconnected);
            if (graph.WindowTops != null)
                for (int c = 0; c < columns.Length; c++)
                    if (columns[c].Length > 0 && graph.WindowTops.TryGetValue(columns[c][0].Id, out double top))
                        g.Left[c] = top - g.Indent[columns[c][0].Id];
            return g;
        }

        internal static void Prepare(Graph graph, List<List<Node>> islands, Dictionary<Guid, int> ranks, double anchorX)
        {
            graph.WindowTops = null;
            if (!HasOutsideX(graph) || graph.Nodes.Length == 0) return;
            if (graph.Edges.Length == 0)
                PrepareIsland(graph, Build(graph, new[] { graph.Nodes }, anchorX, true), anchorX);
            else
                foreach (var island in islands)
                {
                    double left = OldLeft(island);
                    var columns = Enumerable.Range(0, island.Max(n => ranks[n.Id]) + 1)
                        .Select(c => island.Where(n => ranks[n.Id] == c).ToArray()).ToArray();
                    PrepareIsland(graph, Build(graph, columns, left, false), left);
                }
        }

        private static void PrepareIsland(Graph graph, Geometry g, double anchorX)
        {
            // Return wires cannot justify movement or stretch. Preserve the usual
            // packed rounding family whenever it violates neither protection tier.
            if (g.Bounds.SelectMany((bs, c) => bs.Select(b => new { Bound = b, Column = c })).All(p => {
                var b = p.Bound;
                if (!b.Forward) return true;
                double grip = LayoutSolver.Round(g.Left[p.Column] + g.Indent[b.Node.Id] + b.Node.PivotOffset.X) - b.Node.PivotOffset.X + b.Grip;
                return b.Clearance ? (b.Lower ? grip - b.Outside : b.Outside - grip) >= MinClearance
                    : b.Lower ? grip >= b.Outside : grip <= b.Outside;
            })) return;

            var seeds = new SortedSet<long> { (long)Math.Floor(anchorX * Scale), (long)Math.Ceiling(anchorX * Scale) };
            int count = g.Columns.Length;
            foreach (var b in g.Bounds.SelectMany(bs => bs))
                for (int k = -count - 1; k <= count + 1; k++) seeds.Add(b.Tick + k * Cap);
            // Include emitted pivot events as well as tight bound/gap events.
            var candidates = new SortedSet<long>(seeds);
            for (int c = 0; c < g.Columns.Length; c++)
                foreach (var n in g.Columns[c])
                {
                    double offset = g.Packed[c] + g.Indent[n.Id] + n.PivotOffset.X;
                    foreach (long seed in seeds)
                    {
                        double pivot = Math.Floor(seed / Scale + offset);
                        for (int d = -1; d <= 1; d++) candidates.Add((long)Math.Ceiling((pivot + d - .5 - offset) * Scale));
                    }
                }
            var values = candidates.ToArray();
            var states = Solve(g, values, anchorX, double.NegativeInfinity, Preference.MaxEnvelope);
            var optimum = Best(states, Preference.MaxEnvelope);
            double below = states.Where(s => s != null && s.Low && Primary(s, optimum) == 0)
                .Select(s => s.Envelope).DefaultIfEmpty(double.NegativeInfinity).Max();
            var upper = Best(Solve(g, values, anchorX, anchorX, Preference.MinEnvelope), Preference.MinEnvelope);
            double above = upper != null && Primary(upper, optimum) == 0 ? upper.Envelope : double.PositiveInfinity;
            double target = anchorX - below <= above - anchorX ? below : above;
            if (!Graph.Finite(target)) throw new InvalidOperationException("No column-window envelope candidate.");
            State At(double edge) => Best(Solve(g, values, edge, edge, Preference.Remaining)
                .Where(s => s != null && s.Envelope == edge && Primary(s, optimum) == 0), Preference.Remaining);
            var chosen = At(target);
            if (below != above && anchorX - below == above - anchorX)
            {
                var other = At(above);
                if (Compare(other, chosen, Preference.Remaining, false) < 0) { target = above; chosen = other; }
            }
            // Keep the legacy packed phase when all five objectives agree.
            var packed = Cost(g, Enumerable.Repeat((long)Math.Round(target * Scale), count).ToArray());
            if (Primary(packed, optimum) == 0 && packed.Envelope == target && packed.Remaining == chosen.Remaining)
                chosen = packed;
            if (graph.WindowTops == null) graph.WindowTops = new Dictionary<Guid, double>();
            for (int c = 0; c < count; c++)
                foreach (var n in g.Columns[c])
                    graph.WindowTops.Add(n.Id, g.Packed[c] + chosen.Path[c] / Scale + g.Indent[n.Id]);
        }

        private static Geometry Build(Graph graph, Node[][] columns, double anchorX, bool unconnected)
        {
            var g = new Geometry { Columns = columns, Packed = new double[columns.Length],
                Bounds = columns.Select(_ => new List<Bound>()).ToArray() };
            var columnOf = new Dictionary<Guid, int>();
            for (int c = 0; c < columns.Length; c++)
            {
                bool leftAligned = unconnected || columns[c].All(Placement.PureGroup);
                double edge = columns[c].Select(Placement.AlignEdge).DefaultIfEmpty(0).Max();
                foreach (var n in columns[c]) { g.Indent.Add(n.Id, leftAligned ? 0 : edge - Placement.AlignEdge(n)); columnOf.Add(n.Id, c); }
                double width = columns[c].Select(n => g.Indent[n.Id] + n.Width).DefaultIfEmpty(0).Max();
                if (c + 1 < columns.Length)
                    g.Packed[c + 1] = g.Packed[c] + width +
                        (columns[c].Concat(columns[c + 1]).Any(n => n.Kind == NodeKind.Group) ? graph.Options.GroupGapX : graph.Options.GapX);
            }
            g.Left = g.Packed.Select(x => anchorX + x).ToArray();
            void Add(Guid id, double grip, double outside, bool lower)
            {
                if (!columnOf.TryGetValue(id, out int c)) return;
                var n = graph.ById[id];
                double offset = g.Packed[c] + g.Indent[id] + n.PivotOffset.X;
                // The actual grip is Round(shift + offset) - pivotOffset + grip.
                // Use the same strict backward predicate on input and output.
                void AddBound(bool clearance)
                {
                    double margin = clearance ? MinClearance : 0;
                    double target = outside + (lower ? margin : -margin) + n.PivotOffset.X - grip;
                    double pivot = lower ? Math.Ceiling(target) : Math.Floor(target);
                    bool Forward(double p)
                    {
                        double actual = p - n.PivotOffset.X + grip;
                        return clearance ? (lower ? actual - outside : outside - actual) >= MinClearance
                            : lower ? actual >= outside : actual <= outside;
                    }
                    // Recheck the actual arithmetic: algebraic cancellation near an
                    // integral pivot must not classify an equal grip differently.
                    if (lower)
                    {
                        while (!Forward(pivot) && pivot + 1 > pivot) pivot++;
                        while (pivot - 1 < pivot && Forward(pivot - 1)) pivot--;
                    }
                    else
                    {
                        while (!Forward(pivot) && pivot - 1 < pivot) pivot--;
                        while (pivot + 1 > pivot && Forward(pivot + 1)) pivot++;
                    }
                    double threshold = pivot + (lower ? -.5 : .5) - offset;
                    long tick = lower ? (long)Math.Ceiling(threshold * Scale) : (long)Math.Ceiling(threshold * Scale) - 1;
                    g.Bounds[c].Add(new Bound { Tick = tick, Lower = lower, Node = n, Grip = grip, Outside = outside,
                        Clearance = clearance,
                        Forward = clearance || (lower ? n.CurrentTop.X + grip >= outside : n.CurrentTop.X + grip <= outside) });
                }
                AddBound(false);
                double oldGrip = n.CurrentTop.X + grip;
                if ((lower ? oldGrip - outside : outside - oldGrip) >= MinClearance) AddBound(true);
            }
            foreach (var e in graph.Snapshot.Inbound.Where(e => Graph.Finite(e.SourceGripX)))
                Add(e.Node, graph.ById[e.Node].Inputs[e.Port].X, e.SourceGripX.Value, true);
            foreach (var e in graph.Snapshot.Outbound.Where(e => Graph.Finite(e.TargetGripX)))
                Add(e.Node, graph.ById[e.Node].Outputs[e.Port].X, e.TargetGripX.Value, false);
            return g;
        }

        // Reverse dynamic programming. Each window is [x, x+150], so two monotone
        // queues find its best suffix in linear time. Low records whether any
        // emitted bound is at/below the old envelope, to find both movement sides.
        private static State[] Solve(Geometry g, long[] values, double anchor, double floor, Preference preference)
        {
            var previous = new State[2, values.Length];
            for (int c = g.Columns.Length - 1; c >= 0; c--)
            {
                var next = new State[2, values.Length];
                var queues = new[] { new LinkedList<int>(), new LinkedList<int>() };
                for (int k = values.Length - 1; k >= 0; k--)
                {
                    double envelope = ColumnEnvelope(g, c, values[k]);
                    bool low = envelope <= anchor;
                    for (int flag = 0; flag < 2; flag++)
                    {
                        var queue = queues[flag];
                        if (c + 1 < g.Columns.Length)
                        {
                            if (previous[flag, k] != null)
                            {
                                while (queue.Count > 0 && Compare(previous[flag, k], previous[flag, queue.Last.Value], preference, true) < 0) queue.RemoveLast();
                                queue.AddLast(k);
                            }
                            while (queue.Count > 0 && values[queue.First.Value] > values[k] + Cap) queue.RemoveFirst();
                        }
                        if (envelope < floor || c + 1 == g.Columns.Length && flag != 0 ||
                            c + 1 < g.Columns.Length && queue.Count == 0) continue;
                        var suffix = c + 1 == g.Columns.Length ? new State { Path = Array.Empty<long>(), Envelope = double.PositiveInfinity }
                            : previous[flag, queue.First.Value];
                        var state = new State { Created = suffix.Created, LostClearance = suffix.LostClearance, Remaining = suffix.Remaining,
                            Stretch = suffix.Stretch + (suffix.Path.Length == 0 ? 0 : suffix.Path[0] - values[k]),
                            Envelope = Math.Min(envelope, suffix.Envelope), Low = low || suffix.Low,
                            Path = new[] { values[k] }.Concat(suffix.Path).ToArray() };
                        foreach (var b in g.Bounds[c])
                            if (!(b.Lower ? values[k] >= b.Tick : values[k] <= b.Tick))
                            { if (b.Clearance) state.LostClearance++; else if (b.Forward) state.Created++; else state.Remaining++; }
                        int f = state.Low ? 1 : 0;
                        if (next[f, k] == null || Compare(state, next[f, k], preference, false) < 0) next[f, k] = state;
                    }
                }
                previous = next;
            }
            return previous.Cast<State>().ToArray();
        }

        private static double ColumnEnvelope(Geometry g, int c, long shift) => g.Columns[c].Select(n =>
            LayoutSolver.Round(g.Packed[c] + shift / Scale + g.Indent[n.Id] + n.PivotOffset.X) - n.PivotOffset.X)
            .DefaultIfEmpty(double.PositiveInfinity).Min();
        private static double Envelope(Geometry g, long[] path) => path.Select((x, c) => ColumnEnvelope(g, c, x)).Min();
        private static State Cost(Geometry g, long[] path)
        {
            var state = new State { Path = path, Stretch = path.Last() - path.First(), Envelope = Envelope(g, path) };
            for (int c = 0; c < path.Length; c++)
                foreach (var b in g.Bounds[c])
                    if (!(b.Lower ? path[c] >= b.Tick : path[c] <= b.Tick))
                    { if (b.Clearance) state.LostClearance++; else if (b.Forward) state.Created++; else state.Remaining++; }
            return state;
        }
        private static State Best(IEnumerable<State> states, Preference preference)
        {
            State best = null;
            foreach (var s in states)
                if (s != null && (best == null || Compare(s, best, preference, false) < 0)) best = s;
            return best;
        }
        private static int Primary(State a, State b, bool transition = false)
        {
            int c = a.Created.CompareTo(b.Created);
            if (c == 0) c = a.LostClearance.CompareTo(b.LostClearance);
            if (c == 0) c = (a.Stretch + (transition ? a.Path[0] : 0)).CompareTo(b.Stretch + (transition ? b.Path[0] : 0));
            return c;
        }
        private static int Compare(State a, State b, Preference preference, bool transition)
        {
            int c = Primary(a, b, transition);
            if (c == 0 && preference != Preference.Remaining)
                c = preference == Preference.MaxEnvelope ? b.Envelope.CompareTo(a.Envelope) : a.Envelope.CompareTo(b.Envelope);
            if (c == 0) c = a.Remaining.CompareTo(b.Remaining);
            for (int i = 0; c == 0 && i < a.Path.Length; i++) c = a.Path[i].CompareTo(b.Path[i]);
            return c;
        }
        private enum Preference { MaxEnvelope, MinEnvelope, Remaining }
        internal sealed class Bound
        {
            internal long Tick;
            internal bool Lower, Forward, Clearance;
            internal Node Node;
            internal double Grip, Outside;
        }
        private sealed class State
        {
            internal int Created, LostClearance, Remaining;
            internal long Stretch;
            internal double Envelope;
            internal bool Low;
            internal long[] Path;
        }
    }
}
