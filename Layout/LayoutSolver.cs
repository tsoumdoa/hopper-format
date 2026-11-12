using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    public static class LayoutSolver
    {
        public static Result Solve(Snapshot snapshot) => Solve(snapshot, Placement.Place);

        internal static Result Solve(Snapshot snapshot,
            Func<Graph, Ordering, Dictionary<Guid, int>, double, ISet<Edge>, Dictionary<Guid, Vec2>> place)
        {
            Graph original;
            string diagnostic;
            if (!Graph.TryCreate(snapshot, out original, out diagnostic))
                return new Result { Success = false, Diagnostic = diagnostic };
            var anchor = original.Nodes.Length == 0 ? new Vec2(0, 0) : new Vec2(
                original.Nodes.Min(n => n.CurrentTop.X), original.Nodes.Min(n => n.CurrentTop.Y));
            var result = new Result { Success = true, Anchor = anchor };
            var log = new List<string>();
            var graph = original;
            bool windows = ColumnWindows.HasOutsideX(original);
            Dictionary<Guid, Vec2> tops = new Dictionary<Guid, Vec2>();
            var matched = FixedPoint.TryMatch(graph, anchor, out var fixedRanks, out var islands, out var ranks);
            for (int round = 1; round <= 3; round++)
            {
                // Rounding may move the union by a fraction of a unit. A feedback
                // snapshot must use its own union, just as the next public Solve does.
                var roundAnchor = graph.Nodes.Length == 0 ? anchor : new Vec2(
                    graph.Nodes.Min(n => n.CurrentTop.X), graph.Nodes.Min(n => n.CurrentTop.Y));
                if (round == 1 && matched)
                {
                    tops = graph.Nodes.ToDictionary(n => n.Id, n => n.CurrentTop);
                    result.Ranks = fixedRanks;
                    result.Rounds = round;
                    var visibleLanes = new HashSet<Edge>(islands.SelectMany(island =>
                        FixedPoint.VisibleLanes(graph, island, fixedRanks)));
                    result.LaneEdges = graph.Edges.Where(visibleLanes.Contains).ToArray();
                    int crossings = graph.Edges.Length == 0 ? 0 : islands.Sum(island =>
                        new Ordering(graph, island, fixedRanks, false).Before);
                    result.CrossingsBefore = result.CrossingsAfter = crossings;
                    log.Add("Round " + round + ": crossings " + result.CrossingsBefore + " -> " +
                        result.CrossingsAfter + "; rounded column layout is a fixed point.");
                    break;
                }
                tops = new Dictionary<Guid, Vec2>();
                ColumnWindows.Prepare(graph, islands, ranks, roundAnchor.X);
                var lanes = new HashSet<Edge>();
                int before = 0, after = 0;
                if (graph.Edges.Length == 0)
                {
                    double y = roundAnchor.Y;
                    double x = ColumnWindows.Place(graph, new[] { graph.Nodes }, roundAnchor.X, true).Left[0];
                    foreach (var node in graph.Nodes)
                    {
                        tops[node.Id] = new Vec2(x, y);
                        y += node.Height + Placement.UnconnectedGap(graph, node);
                    }
                }
                else
                {
                    double nextY = roundAnchor.Y;
                    foreach (var island in islands)
                    {
                        var ordering = new Ordering(graph, island, ranks);
                        var placed = place(graph, ordering, ranks, roundAnchor.X, lanes);
                        if (placed == null)
                            return new Result { Success = false, Diagnostic = "Layout compaction failed: constraint cycle." };
                        before += ordering.Before;
                        after += ordering.After;
                        double minY = island.Min(n => placed[n.Id].Y);
                        double minX = island.Min(n => placed[n.Id].X);
                        foreach (var node in island)
                            tops[node.Id] = new Vec2(placed[node.Id].X + (windows ? 0 : roundAnchor.X - minX), placed[node.Id].Y + nextY - minY);
                        nextY = island.Max(n => tops[n.Id].Y + n.Height) + Placement.IslandGap(graph, island, islands.SkipWhile(i => i != island).Skip(1).FirstOrDefault() ?? new List<Node>());
                    }
                }
                if (tops.Count > 0)
                {
                    double dx = windows ? 0 : roundAnchor.X - graph.Nodes.Min(n => tops[n.Id].X);
                    double dy = roundAnchor.Y - graph.Nodes.Min(n => tops[n.Id].Y);
                    foreach (var node in graph.Nodes)
                    {
                        var top = tops[node.Id];
                        tops[node.Id] = new Vec2(Round(top.X + dx + node.PivotOffset.X) - node.PivotOffset.X,
                            Round(top.Y + dy + node.PivotOffset.Y) - node.PivotOffset.Y);
                    }
                }
                result.Rounds = round;
                result.Ranks = ranks;
                if (round == 1) result.CrossingsBefore = before;
                result.CrossingsAfter = after;
                result.LaneEdges = graph.Edges.Where(lanes.Contains).ToArray();
                log.Add("Round " + round + ": crossings " + before + " -> " + after + ".");
                if (graph.Nodes.All(n => Same(n.CurrentTop, tops[n.Id]))) break;
                if (!Graph.TryCreate(WithTops(snapshot, tops), out graph, out diagnostic))
                    return new Result { Success = false, Diagnostic = "Rounded layout is invalid: " + diagnostic };
                var placedAnchor = graph.Nodes.Length == 0 ? anchor : new Vec2(
                    graph.Nodes.Min(n => n.CurrentTop.X), graph.Nodes.Min(n => n.CurrentTop.Y));
                if (FixedPoint.TryMatch(graph, placedAnchor, out fixedRanks, out islands, out ranks))
                {
                    result.Ranks = fixedRanks;
                    log.Add("Verified rounded fixed point.");
                    break;
                }
                if (round == 3)
                {
                    result.Unstable = true;
                    log.Add("Unstable: layout did not settle within three rounds.");
                    result.Notices = new[] { log[log.Count - 1] };
                    break;
                }
            }
            if (tops.Count > 0)
            {
                // Correct accumulated rounding drift against the original envelope.
                // Integral translations preserve integral pivots and fixed points.
                double dx = windows ? 0 : Round(anchor.X - tops.Values.Min(t => t.X));
                double dy = Round(anchor.Y - tops.Values.Min(t => t.Y));
                foreach (var node in original.Nodes)
                    tops[node.Id] = new Vec2(tops[node.Id].X + dx, tops[node.Id].Y + dy);
            }
            result.Tops = new SortedDictionary<Guid, Vec2>(tops);
            result.Moves = original.Nodes.OrderBy(n => n.Id).Where(n =>
                Round(n.CurrentTop.X + n.PivotOffset.X) != Round(tops[n.Id].X + n.PivotOffset.X) ||
                Round(n.CurrentTop.Y + n.PivotOffset.Y) != Round(tops[n.Id].Y + n.PivotOffset.Y))
                .Select(n => new Move { Id = n.Id, Pivot = new Vec2(Round(tops[n.Id].X + n.PivotOffset.X),
                    Round(tops[n.Id].Y + n.PivotOffset.Y)) }).ToArray();
            result.Log = log.ToArray();
            return result;
        }

        // Translation-invariant tie breaking, including negative canvas coordinates.
        internal static double Round(double value) { return Math.Floor(value + 0.5); }

        private static bool Same(Vec2 a, Vec2 b) { return Math.Abs(a.X - b.X) < 1e-8 && Math.Abs(a.Y - b.Y) < 1e-8; }

        private static Snapshot WithTops(Snapshot snapshot, Dictionary<Guid, Vec2> tops)
        {
            return new Snapshot { Edges = snapshot.Edges, Inbound = snapshot.Inbound, Outbound = snapshot.Outbound,
                Options = snapshot.Options, Nodes = snapshot.Nodes.Select(n => new Node { Id = n.Id, Label = n.Label,
                    Width = n.Width, Height = n.Height, PivotOffset = n.PivotOffset, CurrentTop = tops[n.Id],
                    Kind = n.Kind, Widget = n.Widget, ParamRole = n.ParamRole, AlignRight = n.AlignRight,
                    Inputs = n.Inputs, Outputs = n.Outputs }).ToArray() };
        }
    }
}

