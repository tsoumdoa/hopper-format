using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    internal static class Placement
    {
        internal static Dictionary<Guid, Vec2> Place(Graph graph, Ordering ordering,
            Dictionary<Guid, int> ranks, double anchorX, ISet<Edge> lanes = null)
        {
            var columns = ordering.Columns.Select(c => new List<OrderItem>(c)).ToArray();
            var segments = ordering.Segments.Select(c => new List<Segment>(c)).ToArray();
            Dictionary<OrderItem, Segment> predecessor;
            while (true)
            {
                predecessor = Align(columns, segments, ranks);
                var bent = new HashSet<Edge>(segments.SelectMany(c => c)
                    .GroupBy(s => s.Edge).Where(chain => chain.Any(s => s.From.Virtual || s.To.Virtual) &&
                        chain.Any(s => !predecessor.TryGetValue(s.To, out var link) || link != s))
                    .Select(chain => chain.Key));
                if (bent.Count == 0) break;
                // A bent Grasshopper wire is one bezier, not a routed polyline.
                // Its virtual items count crossings but must not reserve empty lanes.
                var removed = new HashSet<OrderItem>(segments.SelectMany(c => c)
                    .Where(s => bent.Contains(s.Edge)).SelectMany(s => new[] { s.From, s.To }).Where(i => i.Virtual));
                foreach (var column in columns) column.RemoveAll(removed.Contains);
                foreach (var pair in segments) pair.RemoveAll(s => bent.Contains(s.Edge));
            }
            if (lanes != null)
                foreach (var segment in segments.SelectMany(c => c).Where(s => s.From.Virtual))
                    lanes.Add(segment.Edge);

            var roots = new Dictionary<OrderItem, OrderItem>();
            var offsets = new Dictionary<OrderItem, double>();
            foreach (var item in columns.SelectMany(c => c))
            {
                if (predecessor.TryGetValue(item, out var segment))
                {
                    roots.Add(item, roots[segment.From]);
                    offsets.Add(item, offsets[segment.From] + segment.FromY - segment.ToY);
                }
                else
                {
                    roots.Add(item, item);
                    offsets.Add(item, 0);
                }
            }
            return Compact(graph, columns, roots, offsets, anchorX);
        }

        private static Dictionary<OrderItem, Segment> Align(List<OrderItem>[] columns,
            List<Segment>[] segments, Dictionary<Guid, int> ranks)
        {
            var positions = new Dictionary<OrderItem, int>();
            var realItems = columns.SelectMany(c => c).Where(i => !i.Virtual).ToDictionary(i => i.Node.Id);
            foreach (var column in columns)
                for (int i = 0; i < column.Count; i++) positions.Add(column[i], i);

            // One incoming and one outgoing alignment per item make rigid chains.
            // Decide each pair once: lane segments claim their endpoints first.
            var predecessor = new Dictionary<OrderItem, Segment>();
            for (int c = 0; c < segments.Length; c++)
            {
                var pair = segments[c];
                var accepted = new List<Segment>();
                var used = new HashSet<OrderItem>();
                bool Align(Segment segment)
                {
                    if (used.Contains(segment.From) || predecessor.ContainsKey(segment.To)) return false;
                    if (accepted.Any(s =>
                        (positions[s.From] < positions[segment.From] && positions[s.To] > positions[segment.To]) ||
                        (positions[s.From] > positions[segment.From] && positions[s.To] < positions[segment.To]))) return false;
                    accepted.Add(segment);
                    used.Add(segment.From);
                    predecessor.Add(segment.To, segment);
                    return true;
                }

                foreach (var segment in pair.Where(s => s.From.Virtual || s.To.Virtual)
                    .GroupBy(s => (s.Edge.From, s.Edge.To))
                    .Select(g => g.OrderBy(s => s.Edge.ToPort).ThenBy(s => s.Edge.FromPort)
                        .ElementAt((g.Count() - 1) / 2))
                    .OrderBy(s => positions[s.From]).ThenBy(s => s.Edge.FromPort)
                    .ThenByDescending(s => ranks[s.Edge.To] - ranks[s.Edge.From])
                    .ThenBy(s => positions[realItems[s.Edge.To]])
                    .ThenBy(s => s.Edge.ToPort).ThenBy(s => s.Edge.To))
                    Align(segment);

                foreach (var item in columns[c + 1].Where(i => !i.Virtual))
                {
                    if (predecessor.ContainsKey(item)) continue;
                    var candidates = pair.Where(s => s.To == item && !s.From.Virtual)
                        .OrderBy(s => s.Edge.ToPort).ThenBy(s => positions[s.From])
                        .ThenBy(s => s.Edge.FromPort).ToArray();
                    int median = (candidates.Length - 1) / 2;
                    foreach (int i in Enumerable.Range(0, candidates.Length)
                        .OrderBy(i => Math.Abs(i - median)).ThenBy(i => i))
                    {
                        // Keep predecessor selection unchanged, but centre parallel
                        // wires from that predecessor on their upper-median input.
                        var parallel = candidates.Where(s => s.From == candidates[i].From).ToArray();
                        if (Align(parallel[(parallel.Length - 1) / 2])) break;
                    }
                }
            }

            return predecessor;
        }

        internal static Dictionary<Guid, Vec2> Compact(Graph graph, List<OrderItem>[] columns,
            Dictionary<OrderItem, OrderItem> roots, Dictionary<OrderItem, double> offsets, double anchorX,
            bool rounded = false)
        {
            var blocks = new Dictionary<OrderItem, Block>();
            var blockOrder = new List<Block>();
            var compactOrder = new List<Block>();
            var blockOf = new Dictionary<OrderItem, Block>();
            var realItems = new Dictionary<Guid, OrderItem>();
            foreach (var column in columns)
                foreach (var item in column)
                {
                    if (!blocks.TryGetValue(roots[item], out var block))
                    {
                        block = new Block();
                        blocks.Add(roots[item], block);
                        blockOrder.Add(block);
                    }
                    blockOf.Add(item, block);
                    block.Members.Add(item);
                    if (!item.Virtual) realItems.Add(item.Node.Id, item);
                }

            // Each upper neighbour imposes a lower bound on this block's top.
            // Non-crossing chains preserve column order, so these dependencies
            // form a DAG. Keeping every constraint lets tall nodes push whole lanes.
            foreach (var column in columns)
                for (int i = 1; i < column.Count; i++)
                {
                    var above = column[i - 1];
                    var item = column[i];
                    double separation = above.Node.Height + Separation(graph, above, item);
                    double observed = item.Node.CurrentTop.Y - above.Node.CurrentTop.Y;
                    if (blockOf[item] == blockOf[above])
                    {
                        if (rounded && !(above.Virtual && item.Virtual) && observed < separation - 1) return null;
                        continue;
                    }
                    // Recognition retains measured offsets inside visible groups.
                    // A tight separation can differ by <1 after pivot rounding;
                    // recover that contact instead of accumulating rounding errors
                    // through every block on a long constraint path.
                    if (rounded && Math.Abs(observed - separation) < 1)
                        separation = observed;
                    double distance = offsets[above] + separation - offsets[item];
                    blockOf[item].Above.Add(new Constraint { Block = blockOf[above], Distance = distance });
                    blockOf[above].Below.Add(new Constraint { Block = blockOf[item], Distance = distance });
                }
            foreach (var block in blockOrder)
                if (!Compact(block, compactOrder)) return null;

            // Lower blocks settle first, so every downward move respects the final
            // position of its neighbours, including virtual lanes. Only unaligned
            // single-node sources can move independently of their rigid block.
            for (int i = compactOrder.Count - 1; i >= 0; i--)
            {
                var block = compactOrder[i];
                if (block.Members.Count != 1) continue;
                var item = block.Members[0];
                if (item.Virtual || graph.In[item.Node.Id].Count != 0 || graph.Out[item.Node.Id].Count == 0) continue;
                var desired = graph.Out[item.Node.Id].Select(e => {
                    var target = realItems[e.To];
                    return blockOf[target].Top + offsets[target] + target.Node.Inputs[e.ToPort].Y -
                        item.Node.Outputs[e.FromPort].Y - offsets[item];
                }).OrderBy(y => y).ToArray();
                double limit = block.Below.Select(c => c.Block.Top - c.Distance).DefaultIfEmpty(double.PositiveInfinity).Min();
                block.Top = Math.Max(block.Top, Math.Min(desired[(desired.Length - 1) / 2], limit));
            }

            var tops = new Dictionary<Guid, Vec2>();
            double left = anchorX;
            foreach (var column in columns)
            {
                double width = column.Where(i => !i.Virtual).Select(i => i.Node.Width).DefaultIfEmpty(0).Max();
                foreach (var item in column.Where(i => !i.Virtual))
                    tops.Add(item.Node.Id, new Vec2(left + width - item.Node.Width,
                        blockOf[item].Top + offsets[item]));
                left += width + graph.Options.GapX;
            }
            return tops;
        }

        private static double Separation(Graph graph, OrderItem a, OrderItem b)
        {
            if (a.Virtual && b.Virtual) return graph.Options.StackGap;
            if (a.Virtual || b.Virtual) return graph.Options.GapY;
            return Gap(graph, a.Node, b.Node);
        }

        internal static double Gap(Graph graph, Node a, Node b)
        {
            if (a.Widget && b.Widget)
            {
                var successorsA = graph.Out[a.Id].Select(e => e.To).Distinct().ToArray();
                var successorsB = graph.Out[b.Id].Select(e => e.To).Distinct().ToArray();
                if (successorsA.Length == 1 && successorsB.Length == 1 && successorsA[0] == successorsB[0])
                    return graph.Options.StackGap;
            }
            return graph.Options.GapY;
        }

        private sealed class Constraint
        {
            internal Block Block;
            internal double Distance;
        }

        private sealed class Block
        {
            internal readonly List<OrderItem> Members = new List<OrderItem>();
            internal readonly List<Constraint> Above = new List<Constraint>();
            internal readonly List<Constraint> Below = new List<Constraint>();
            internal double Top;
            internal bool Placed;
            internal bool Visiting;
        }

        private static bool Compact(Block block, List<Block> order)
        {
            if (block.Placed) return true;
            if (block.Visiting) return false;
            block.Visiting = true;
            block.Top = block.Above.Count == 0 ? 0 : double.NegativeInfinity;
            foreach (var constraint in block.Above)
            {
                if (!Compact(constraint.Block, order)) return false;
                block.Top = Math.Max(block.Top, constraint.Block.Top + constraint.Distance);
            }
            block.Placed = true;
            order.Add(block);
            return true;
        }
    }
}
