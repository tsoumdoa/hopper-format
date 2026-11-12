using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    internal static class FixedPoint
    {
        internal static bool TryMatch(Graph graph, Vec2 anchor, out Dictionary<Guid, int> ranks)
        {
            return TryMatch(graph, anchor, out ranks, out _, out _);
        }

        internal static bool TryMatch(Graph graph, Vec2 anchor, out Dictionary<Guid, int> ranks,
            out List<List<Node>> islands, out Dictionary<Guid, int> assignedRanks)
        {
            islands = Ranking.Assign(graph, out assignedRanks);
            ranks = assignedRanks;
            if (Matches(graph, islands, ranks, anchor)) return true;
            var recoveredIslands = Ranking.Assign(graph, out ranks, true);
            return Matches(graph, recoveredIslands, ranks, anchor);
        }

        // Recover only visible rigid structure. Bent long wires leave no virtual
        // positions to reconstruct, and accidental level wires can join several
        // alignment chains into one group. No alignment priorities are replayed.
        internal static bool Matches(Graph graph, List<List<Node>> islands,
            Dictionary<Guid, int> ranks, Vec2 anchor)
        {
            if (graph.Nodes.Any(n => Math.Abs(n.CurrentTop.X + n.PivotOffset.X - LayoutSolver.Round(n.CurrentTop.X + n.PivotOffset.X)) > 1e-8 ||
                Math.Abs(n.CurrentTop.Y + n.PivotOffset.Y - LayoutSolver.Round(n.CurrentTop.Y + n.PivotOffset.Y)) > 1e-8)) return false;
            ColumnWindows.Prepare(graph, islands, ranks, anchor.X);
            // The packed shortcut preserves today's rounding family even when
            // deliberate return wires remain backward. Only an optimized plan
            // needs exact X to protect its absolute crossing-wire thresholds.
            var raw = new Dictionary<Guid, Vec2>();
            if (graph.Edges.Length == 0)
            {
                double y = anchor.Y;
                double x = ColumnWindows.Place(graph, new[] { graph.Nodes }, anchor.X, true).Left[0];
                foreach (var node in graph.Nodes)
                {
                    raw[node.Id] = new Vec2(x, y);
                    y += node.Height + Placement.UnconnectedGap(graph, node);
                }
            }
            else
            {
                double y = anchor.Y;
                double? previousBottom = null;
                var previous = new List<Node>();
                foreach (var island in islands)
                {
                    double currentMin = island.Min(n => n.CurrentTop.Y);
                    // Island envelopes also contain independently rounded bounds.
                    // Recover near-tight island contacts without accumulating their
                    // rounding errors across every island in the selection.
                    if (previousBottom.HasValue &&
                        Math.Abs(currentMin - previousBottom.Value - Placement.IslandGap(graph, previous, island)) < 1)
                        y = currentMin;
                    double islandX = ColumnWindows.OldLeft(island);
                    bool islandWindows = ColumnWindows.HasOutsideX(graph, island);
                    var placed = Reconstruct(graph, island, ranks, islandX);
                    if (placed == null) return false;
                    double minX = island.Min(n => placed[n.Id].X), minY = island.Min(n => placed[n.Id].Y);
                    foreach (var node in island)
                        raw[node.Id] = new Vec2(placed[node.Id].X + (islandWindows ? 0 : islandX - minX), placed[node.Id].Y + y - minY);
                    y = island.Max(n => raw[n.Id].Y + n.Height) + Placement.IslandGap(graph, island, islands.SkipWhile(i => i != island).Skip(1).FirstOrDefault() ?? new List<Node>());
                    previous = island;
                    previousBottom = island.Max(n => n.CurrentTop.Y + n.Height);
                }
            }
            // Require a common horizontal rounding translation within each island
            // and one common Y translation across the selection. Half-open intervals
            // implement Round's positive-infinity midpoint rule; individual <1 px
            // errors alone are insufficient. The unconnected case is one island.
            var islandOf = islands.SelectMany((island, i) => island.Select(n => new { n.Id, Island = i }))
                .ToDictionary(p => p.Id, p => p.Island);
            var lowX = islands.Select(_ => -0.5).ToArray();
            var highX = islands.Select(_ => 0.5).ToArray();
            double lowY = -0.5, highY = 0.5;
            foreach (var node in graph.Nodes)
            {
                var expected = raw[node.Id];
                // Outside X fixes the canvas frame: an inferred horizontal
                // rounding translation could cross a wire's exact threshold.
                if (graph.WindowTops != null && graph.WindowTops.ContainsKey(node.Id) &&
                    LayoutSolver.Round(expected.X + node.PivotOffset.X) !=
                    LayoutSolver.Round(node.CurrentTop.X + node.PivotOffset.X)) return false;
                double dx = node.CurrentTop.X - expected.X, dy = node.CurrentTop.Y - expected.Y;
                int i = islandOf[node.Id];
                lowX[i] = Math.Max(lowX[i], dx - 0.5); highX[i] = Math.Min(highX[i], dx + 0.5);
                lowY = Math.Max(lowY, dy - 0.5); highY = Math.Min(highY, dy + 0.5);
            }
            return lowX.Select((low, i) => low < highX[i] - 1e-8).All(ok => ok) && lowY < highY - 1e-8;
        }

        internal static IEnumerable<Edge> VisibleLanes(Graph graph, List<Node> members, Dictionary<Guid, int> ranks)
        {
            var ids = new HashSet<Guid>(members.Select(n => n.Id));
            foreach (var edge in graph.Edges)
            {
                if (!ids.Contains(edge.From) || ranks[edge.To] - ranks[edge.From] <= 1) continue;
                var from = graph.ById[edge.From];
                var to = graph.ById[edge.To];
                if (from.Kind == NodeKind.Group || to.Kind == NodeKind.Group) continue;
                double y = from.CurrentTop.Y + from.Outputs[edge.FromPort].Y;
                if (Math.Abs(y - to.CurrentTop.Y - to.Inputs[edge.ToPort].Y) > 1) continue;
                if (!members.Any(n => ranks[n.Id] > ranks[edge.From] && ranks[n.Id] < ranks[edge.To] &&
                    y > n.CurrentTop.Y - graph.Options.GapY + 1 + 1e-8 &&
                    y < n.CurrentTop.Y + n.Height + graph.Options.GapY - 1 - 1e-8)) yield return edge;
            }
        }

        private static Dictionary<Guid, Vec2> Reconstruct(Graph graph, List<Node> members,
            Dictionary<Guid, int> ranks, double anchorX)
        {
            var columns = Enumerable.Range(0, members.Max(n => ranks[n.Id]) + 1)
                .Select(c => new List<OrderItem>()).ToArray();
            var items = members.ToDictionary(n => n.Id, n => new OrderItem { Node = n, Column = ranks[n.Id] });
            var parent = new Dictionary<OrderItem, OrderItem>();
            foreach (var node in members)
            {
                var item = items[node.Id];
                columns[item.Column].Add(item);
                parent.Add(item, item);
            }
            OrderItem Root(OrderItem item)
            {
                if (parent[item] != item)
                {
                    parent[item] = Root(parent[item]);
                }
                return parent[item];
            }
            void Link(OrderItem from, OrderItem to) { parent[Root(to)] = Root(from); }

            var lanes = new HashSet<Edge>(VisibleLanes(graph, members, ranks));
            foreach (var edge in graph.Edges)
            {
                if (!items.TryGetValue(edge.From, out var from)) continue;
                var to = items[edge.To];
                bool groupLink = Placement.GroupLink(from.Node, to.Node);
                double y = from.Node.CurrentTop.Y + (groupLink ? 0 : from.Node.Outputs[edge.FromPort].Y);
                double targetY = to.Node.CurrentTop.Y + (groupLink ? 0 : to.Node.Inputs[edge.ToPort].Y);
                if (Math.Abs(y - targetY) > 1) continue;
                if (to.Column - from.Column > 1)
                {
                    if (!lanes.Contains(edge)) continue;
                    for (int c = from.Column + 1; c < to.Column; c++)
                    {
                        var lane = new OrderItem { Virtual = true, Column = c,
                            Node = new Node { CurrentTop = new Vec2(0, y) } };
                        columns[c].Add(lane);
                        parent.Add(lane, lane);
                        Link(from, lane);
                    }
                }
                Link(from, to);
            }
            foreach (var column in columns)
                column.Sort((a, b) => {
                    int comparison = a.Node.CurrentTop.Y.CompareTo(b.Node.CurrentTop.Y);
                    if (comparison == 0) comparison = a.Virtual.CompareTo(b.Virtual);
                    return comparison == 0 ? a.Node.Id.CompareTo(b.Node.Id) : comparison;
                });
            var roots = new Dictionary<OrderItem, OrderItem>();
            var offsets = new Dictionary<OrderItem, double>();
            foreach (var item in columns.SelectMany(c => c))
            {
                roots.Add(item, Root(item));
                // Preserve the visible, rounded offsets. Requiring exact grip
                // equality would distort groups joined by accidental level wires.
                offsets.Add(item, item.Node.CurrentTop.Y - Root(item).Node.CurrentTop.Y);
            }
            // The shared compactor also balances unlinked single-node source
            // groups. Sources with visible level links already belong to their
            // target's rigid group; no alignment priorities are reconstructed.
            return Placement.Compact(graph, columns, roots, offsets, anchorX, true);
        }
    }
}
