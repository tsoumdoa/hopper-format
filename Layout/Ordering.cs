using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    internal sealed class OrderItem
    {
        internal Node Node;
        internal bool Virtual;
        internal int Column;
        internal double Slot;
    }

    internal sealed class Segment
    {
        internal OrderItem From, To;
        internal double FromY, ToY;
        internal Edge Edge;
    }

    internal sealed class Ordering
    {
        internal List<OrderItem>[] Columns;
        internal List<Segment>[] Segments;
        internal int Before, After;
        private readonly Graph graph;
        private readonly Dictionary<Guid, double> inboundMeans, outboundMeans;

        internal Ordering(Graph graph, List<Node> members, Dictionary<Guid, int> ranks, bool reduce = true)
        {
            this.graph = graph;
            inboundMeans = graph.Snapshot.Inbound.GroupBy(e => e.Node).ToDictionary(g => g.Key,
                g => g.OrderBy(e => e.Port).ThenBy(e => e.SourceGripY).Average(e => e.SourceGripY));
            outboundMeans = graph.Snapshot.Outbound.GroupBy(e => e.Node).ToDictionary(g => g.Key,
                g => g.OrderBy(e => e.Port).ThenBy(e => e.TargetGripY).Average(e => e.TargetGripY));
            int count = members.Max(n => ranks[n.Id]) + 1;
            Columns = Enumerable.Range(0, count).Select(c => new List<OrderItem>()).ToArray();
            Segments = Enumerable.Range(0, count - 1).Select(c => new List<Segment>()).ToArray();
            var items = new Dictionary<Guid, OrderItem>();
            foreach (var node in members)
            {
                var item = new OrderItem { Node = node, Column = ranks[node.Id] };
                items.Add(node.Id, item);
                Columns[item.Column].Add(item);
            }
            for (int index = 0; index < graph.Edges.Length; index++)
            {
                var edge = graph.Edges[index];
                if (!items.ContainsKey(edge.From)) continue;
                var from = items[edge.From];
                var to = items[edge.To];
                var previous = from;
                for (int c = from.Column + 1; c <= to.Column; c++)
                {
                    OrderItem next;
                    if (c == to.Column) next = to;
                    else
                    {
                        double t = (double)(c - from.Column) / (to.Column - from.Column);
                        // One identity per wire and intermediate column, including parallel wires.
                        // Unreduced crossing metrics use absolute wire Y rather than
                        // node tops, including a wire passing below a tall node.
                        var bytes = new byte[16];
                        Array.Copy(BitConverter.GetBytes(index), 0, bytes, 0, 4);
                        Array.Copy(BitConverter.GetBytes(c), 0, bytes, 4, 4);
                        next = new OrderItem { Virtual = true, Column = c, Node = new Node {
                            Id = new Guid(bytes), CurrentTop = new Vec2(
                                from.Node.CurrentTop.X * (1 - t) + to.Node.CurrentTop.X * t,
                                (from.Node.CurrentTop.Y + (reduce ? 0 : from.Node.Outputs[edge.FromPort].Y)) * (1 - t) +
                                (to.Node.CurrentTop.Y + (reduce ? 0 : to.Node.Inputs[edge.ToPort].Y)) * t) } };
                        Columns[c].Add(next);
                    }
                    Segments[c - 1].Add(new Segment { From = previous, To = next, Edge = edge,
                        FromY = previous == from ? from.Node.Outputs[edge.FromPort].Y : 0,
                        ToY = next == to ? to.Node.Inputs[edge.ToPort].Y : 0 });
                    previous = next;
                }
            }
            for (int c = 0; c < count; c++)
            {
                int column = c;
                Columns[c].Sort((a, b) => {
                    int result = (reduce ? InitialKey(a, column, count) : a.Node.CurrentTop.Y)
                        .CompareTo(reduce ? InitialKey(b, column, count) : b.Node.CurrentTop.Y);
                    return result != 0 ? result : Compare(a, b);
                });
            }
            Before = CountCrossings();
            After = Before;
            if (!reduce) return;
            var best = CopyColumns();
            int pairStart = Before;
            for (int sweep = 0; sweep < 8; sweep++)
            {
                bool forward = sweep % 2 == 0;
                for (int i = 0; i < count - 1; i++)
                {
                    int c = forward ? i + 1 : count - 2 - i;
                    SweepColumn(c, forward);
                }
                int crossings = CountCrossings();
                if (crossings < After) { After = crossings; best = CopyColumns(); }
                // Give each direction a turn before testing for improvement.
                if (!forward)
                {
                    if (After >= pairStart) break;
                    pairStart = After;
                }
            }
            Columns = best;
            StackSlots();
        }

        private double InitialKey(OrderItem item, int column, int count)
        {
            if (!item.Virtual && column == 0)
            {
                if (inboundMeans.TryGetValue(item.Node.Id, out var hint)) return hint;
            }
            if (!item.Virtual && column == count - 1)
            {
                if (outboundMeans.TryGetValue(item.Node.Id, out var hint)) return hint;
            }
            return item.Node.CurrentTop.Y;
        }

        private static int Compare(OrderItem a, OrderItem b)
        {
            int c = a.Virtual.CompareTo(b.Virtual);
            return c != 0 ? c : Graph.CompareNodes(a.Node, b.Node);
        }

        private List<OrderItem>[] CopyColumns() { return Columns.Select(c => new List<OrderItem>(c)).ToArray(); }

        private void StackSlots()
        {
            foreach (var column in Columns)
            {
                double y = 0;
                foreach (var item in column) { item.Slot = y; y += item.Node.Height + graph.Options.GapY; }
            }
        }

        private void SweepColumn(int c, bool forward)
        {
            StackSlots();
            var segments = Segments[forward ? c - 1 : c];
            var attachedByItem = segments.ToLookup(s => forward ? s.To : s.From);
            var bary = new Dictionary<OrderItem, double>();
            foreach (var item in Columns[c])
            {
                var attached = attachedByItem[item];
                if (!attached.Any()) continue;
                bary[item] = attached.Average(s => forward
                    ? s.From.Slot + s.FromY - s.ToY : s.To.Slot + s.ToY - s.FromY);
            }
            // Only occupied slots participate: disconnected nodes retain their slots.
            var active = Columns[c].Where(bary.ContainsKey).ToList();
            active.Sort((a, b) => {
                int result = bary[a].CompareTo(bary[b]);
                return result != 0 ? result : Compare(a, b);
            });
            int j = 0;
            for (int i = 0; i < Columns[c].Count; i++)
                if (bary.ContainsKey(Columns[c][i])) Columns[c][i] = active[j++];
        }

        internal int CountCrossings()
        {
            StackSlots();
            int total = 0;
            foreach (var pair in Segments)
            {
                // Strict inversions: sharing either grip is not a crossing. Group equal
                // source coordinates before inserting targets into the Fenwick tree.
                var edges = pair.OrderBy(s => s.From.Slot + s.FromY)
                    .ThenBy(s => s.To.Slot + s.ToY)
                    .ThenBy(s => s.From.Node.CurrentTop.Y).ThenBy(s => s.From.Node.CurrentTop.X)
                    .ThenBy(s => s.From.Node.Id).ToArray();
                var targets = edges.Select(s => s.To.Slot + s.ToY).Distinct().OrderBy(y => y).ToArray();
                var tree = new int[targets.Length + 1];
                int inserted = 0;
                for (int i = 0; i < edges.Length;)
                {
                    int end = i + 1;
                    double source = edges[i].From.Slot + edges[i].FromY;
                    while (end < edges.Length && edges[end].From.Slot + edges[end].FromY == source) end++;
                    for (int j = i; j < end; j++)
                    {
                        int rank = Array.BinarySearch(targets, edges[j].To.Slot + edges[j].ToY) + 1;
                        int below = 0;
                        for (int k = rank; k > 0; k -= k & -k) below += tree[k];
                        total += inserted - below;
                    }
                    for (int j = i; j < end; j++)
                    {
                        int rank = Array.BinarySearch(targets, edges[j].To.Slot + edges[j].ToY) + 1;
                        for (int k = rank; k < tree.Length; k += k & -k) tree[k]++;
                        inserted++;
                    }
                    i = end;
                }
            }
            return total;
        }
    }
}
