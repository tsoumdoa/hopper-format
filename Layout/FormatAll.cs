using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    public sealed class FormatAllTarget
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public Box Bounds { get; set; }
        public HierarchySnapshot Hierarchy { get; set; }
        public string SkipReason { get; set; }
    }

    public sealed class FormatAllSnapshot
    {
        public IReadOnlyList<FormatAllTarget> Targets { get; set; } = Array.Empty<FormatAllTarget>();
        public IReadOnlyList<Box> Obstacles { get; set; } = Array.Empty<Box>();
        public Options Options { get; set; } = new Options();
    }

    public sealed class FormatAllResult
    {
        public Result Layout { get; set; } = new Result();
        public int Formatted { get; set; }
        public int Skipped { get; set; }
    }

    // Independent inner solves deliberately exclude a dataflow solve between roots.
    public static class FormatAllSolver
    {
        public static FormatAllResult Solve(FormatAllSnapshot snapshot)
        {
            var solved = new List<(FormatAllTarget Target, Result Result)>();
            var obstacles = snapshot.Obstacles.ToList();
            var log = new List<string>();
            int skipped = 0;
            var ownership = snapshot.Targets.Where(t => t.Hierarchy != null).SelectMany(t =>
                t.Hierarchy.Groups.SelectMany(g => ObjectIds(g, new HashSet<Guid>())).Distinct().Select(id => (Id: id, Target: t.Id)))
                .GroupBy(p => p.Id).Where(g => g.Select(p => p.Target).Distinct().Count() > 1)
                .SelectMany(g => g.Select(p => p.Target)).ToHashSet();
            foreach (var target in snapshot.Targets.OrderBy(t => t.Id))
            {
                var reason = target.SkipReason ?? (ownership.Contains(target.Id) ? "Overlapping membership between top-level groups" : null);
                var result = reason == null ? SolveInner(target.Hierarchy) : null;
                reason = reason ?? (result.Success ? null : result.Diagnostic);
                if (reason != null)
                {
                    skipped++;
                    log.Add("Skipped group '" + target.Label + "': " + reason);
                    if (target.Bounds.Width > 0 && target.Bounds.Height > 0) obstacles.Add(target.Bounds);
                    continue;
                }
                solved.Add((target, result));
                log.AddRange(result.Log);
            }
            if (solved.Count == 0)
                return new FormatAllResult { Skipped = skipped, Layout = new Result {
                    Diagnostic = "No groups to format", Log = log.ToArray() } };

            var boxes = solved.ToDictionary(s => s.Target.Id, s => s.Result.GroupBoxes[s.Target.Id]);
            var tidy = GroupTidy.Solve(boxes, obstacles, snapshot.Options);
            if (!tidy.Success) return new FormatAllResult { Skipped = skipped, Layout = tidy };
            log.AddRange(tidy.Log);
            var tops = new SortedDictionary<Guid, Vec2>();
            var groupBoxes = new SortedDictionary<Guid, Box>();
            var moves = new SortedDictionary<Guid, Move>();
            foreach (var item in solved)
            {
                var before = boxes[item.Target.Id]; var after = tidy.GroupBoxes[item.Target.Id];
                double dx = after.X - before.X, dy = after.Y - before.Y;
                var nodes = Groups(item.Target.Hierarchy.Groups.Single()).SelectMany(g => g.Nodes)
                    .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                var notes = Groups(item.Target.Hierarchy.Groups.Single()).SelectMany(g => g.NonNodes)
                    .GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                foreach (var pair in item.Result.Tops)
                {
                    var top = new Vec2(pair.Value.X + dx, pair.Value.Y + dy);
                    tops.Add(pair.Key, top);
                    var old = nodes.TryGetValue(pair.Key, out var node) ? node.CurrentTop :
                        new Vec2(notes[pair.Key].Bounds.X, notes[pair.Key].Bounds.Y);
                    var offset = node == null ? notes[pair.Key].PivotOffset : node.PivotOffset;
                    // Group bounds and pivots are PointF values in the host. A box
                    // union and subtraction can introduce a couple of float ULPs.
                    if (!SamePivot(top.X + offset.X,old.X + offset.X) || !SamePivot(top.Y + offset.Y,old.Y + offset.Y))
                        moves.Add(pair.Key, new Move { Id = pair.Key, Pivot = new Vec2(top.X + offset.X, top.Y + offset.Y) });
                }
                foreach (var pair in item.Result.GroupBoxes)
                    groupBoxes.Add(pair.Key, Translate(pair.Value, dx, dy));
            }
            return new FormatAllResult { Formatted = solved.Count, Skipped = skipped, Layout = new Result {
                Success = true, Tops = tops, GroupBoxes = groupBoxes, Moves = moves.Values.ToArray(),
                Rounds = tidy.Rounds, Unstable = tidy.Unstable || solved.Any(s => s.Result.Unstable), Log = log.ToArray() } };
        }

        private static IEnumerable<HierarchyGroup> Groups(HierarchyGroup g) => new[] { g }.Concat(g.Children.SelectMany(Groups));
        private static bool SamePivot(double a, double b) => Math.Abs(a - b) <= FloatTolerance(b);
        private static double FloatTolerance(double value) => Math.Max(1e-7, 2 * Math.Abs((double)MathF.BitIncrement((float)value) - (float)value));
        private static Result SolveInner(HierarchySnapshot snapshot)
        {
            var original = HierarchySolver.Solve(snapshot);
            if (!original.Success) return original;
            var nodes = snapshot.Groups.SelectMany(Groups).SelectMany(g => g.Nodes).GroupBy(n => n.Id).Select(g => g.First()).OrderBy(n => n.Id).ToArray();
            if (nodes.Length == 0) return original;
            var pivot = new Vec2(nodes[0].CurrentTop.X + nodes[0].PivotOffset.X, nodes[0].CurrentTop.Y + nodes[0].PivotOffset.Y);
            double dx = pivot.X - LayoutSolver.Round(pivot.X), dy = pivot.Y - LayoutSolver.Round(pivot.Y);
            // Exact box-edge snapping can rigidly translate an integer pivot lattice by
            // a fraction. Solve in that lattice's frame so a second run preserves it.
            if (nodes.Any(n => Math.Abs(n.CurrentTop.X + n.PivotOffset.X - dx - LayoutSolver.Round(n.CurrentTop.X + n.PivotOffset.X - dx)) > 1e-7 ||
                Math.Abs(n.CurrentTop.Y + n.PivotOffset.Y - dy - LayoutSolver.Round(n.CurrentTop.Y + n.PivotOffset.Y - dy)) > 1e-7) ||
                Math.Abs(dx) < 1e-8 && Math.Abs(dy) < 1e-8) return original;
            HierarchyGroup Copy(HierarchyGroup g) => new HierarchyGroup { Id = g.Id, Label = g.Label, Locked = g.Locked, Padding = g.Padding,
                Nodes = g.Nodes.Select(n => new Node { Id = n.Id, Label = n.Label, Kind = n.Kind, Widget = n.Widget,
                    Width = n.Width, Height = n.Height, Inputs = n.Inputs, Outputs = n.Outputs, PivotOffset = n.PivotOffset,
                    CurrentTop = new Vec2(n.CurrentTop.X - dx,n.CurrentTop.Y - dy) }).ToArray(),
                NonNodes = g.NonNodes.Select(n => new NonNodeMember { Id = n.Id, Label = n.Label, PivotOffset = n.PivotOffset,
                    Bounds = Translate(n.Bounds,-dx,-dy) }).ToArray(), Children = g.Children.Select(Copy).ToArray() };
            var result = HierarchySolver.Solve(new HierarchySnapshot { Groups = snapshot.Groups.Select(Copy).ToArray(),
                Options = snapshot.Options, Edges = snapshot.Edges,
                Inbound = snapshot.Inbound.Select(e => new ExternalInbound { Node = e.Node, Port = e.Port, SourceGripY = e.SourceGripY - dy }).ToArray(),
                Outbound = snapshot.Outbound.Select(e => new ExternalOutbound { Node = e.Node, Port = e.Port, TargetGripY = e.TargetGripY - dy }).ToArray() });
            if (!result.Success) return result;
            result.Tops = result.Tops.ToDictionary(p => p.Key,p => new Vec2(p.Value.X + dx,p.Value.Y + dy));
            result.GroupBoxes = result.GroupBoxes.ToDictionary(p => p.Key,p => Translate(p.Value,dx,dy));
            result.Moves = result.Moves.Select(m => new Move { Id = m.Id, Pivot = new Vec2(m.Pivot.X + dx,m.Pivot.Y + dy) }).ToArray();
            return result;
        }
        private static IEnumerable<Guid> ObjectIds(HierarchyGroup g, HashSet<Guid> seen)
        {
            if (g == null || !seen.Add(g.Id)) yield break;
            yield return g.Id;
            foreach (var id in g.Nodes.Select(n => n.Id).Concat(g.NonNodes.Select(n => n.Id))) yield return id;
            foreach (var id in g.Children.SelectMany(c => ObjectIds(c, seen))) yield return id;
        }
        internal static Box Translate(Box b, double dx, double dy) => new Box(b.X + dx, b.Y + dy, b.Width, b.Height);
    }

    public static class GroupTidy
    {
        private const double Epsilon = 1e-7;
        private sealed class Row
        {
            internal readonly List<Guid> Members = new List<Guid>();
            internal double Top, FirstHeight;
        }

        public static Result Solve(IReadOnlyDictionary<Guid, Box> targets, IReadOnlyList<Box> obstacles, Options options)
        {
            double gx = options.GroupGapX, gy = options.GroupGapY;
            if (!Graph.Finite(gx) || !Graph.Finite(gy) || gx < 0 || gy < 0)
                return new Result { Diagnostic = "Group gaps must be finite and non-negative." };
            var boxes = targets.ToDictionary(p => p.Key, p => p.Value);
            bool changed = false;
            int rounds = 0;
            for (; rounds < 5;)
            {
                rounds++;
                var rows = new List<Row>();
                foreach (var id in boxes.Keys.OrderBy(id => boxes[id].Y).ThenBy(id => boxes[id].X).ThenBy(id => id))
                {
                    var b = boxes[id];
                    var row = rows.LastOrDefault();
                    if (row == null || Math.Abs(b.Y - row.Top) > Math.Min(gy, .25 * Math.Min(b.Height, row.FirstHeight)) + Epsilon)
                    {
                        row = new Row { Top = b.Y, FirstHeight = b.Height };
                        rows.Add(row);
                    }
                    row.Members.Add(id);
                }
                var lefts = new Dictionary<Guid, double>();
                double? cluster = null;
                foreach (var id in boxes.Keys.OrderBy(id => boxes[id].X).ThenBy(id => id))
                {
                    if (!cluster.HasValue || boxes[id].X - cluster.Value > gx / 2 + Epsilon) cluster = boxes[id].X;
                    lefts[id] = cluster.Value;
                }
                var placed = obstacles.ToList();
                var next = new Dictionary<Guid, Box>();
                foreach (var row in rows)
                {
                    double top = row.Members.Max(id => {
                        var b = boxes[id]; double x = lefts[id];
                        var above = placed.Where(p => Overlap(p.X, p.Right, x, x + b.Width) && p.Y < row.Top - Epsilon).ToArray();
                        if (above.Length == 0) return row.Top;
                        double a = above.Max(p => p.Bottom);
                        return row.Top - a < 2 * gy - Epsilon ? a + gy : row.Top;
                    });
                    foreach (var id in row.Members.OrderBy(id => lefts[id]).ThenBy(id => id))
                    {
                        var b = boxes[id]; double x = lefts[id];
                        var sameHeight = placed.Where(p => Overlap(p.Y, p.Bottom, top, top + b.Height)).ToArray();
                        var left = sameHeight.Where(p => p.X < x - Epsilon).ToArray();
                        if (left.Length > 0)
                        {
                            double right = left.Max(p => p.Right);
                            if (x - right < 2 * gx - Epsilon) x = right + gx;
                        }
                        while (true)
                        {
                            var collisions = placed.Where(p => Overlap(p.Y, p.Bottom, top - gy, top + b.Height + gy)
                                && x < p.Right + gx - Epsilon && x + b.Width > p.X - gx + Epsilon).ToArray();
                            if (collisions.Length == 0) break;
                            x = collisions.Max(p => p.Right) + gx;
                        }
                        var result = new Box(x, top, b.Width, b.Height);
                        next.Add(id, result); placed.Add(result);
                    }
                }
                changed = boxes.Any(p => Math.Abs(next[p.Key].X - p.Value.X) > Epsilon || Math.Abs(next[p.Key].Y - p.Value.Y) > Epsilon);
                boxes = next;
                if (!changed) break;
            }
            return new Result { Success = true, GroupBoxes = new SortedDictionary<Guid, Box>(boxes), Rounds = rounds,
                Unstable = changed, Log = changed ? new[] { "Unstable: format-all tidy did not settle within five passes; kept the last result." } : Array.Empty<string>() };
        }

        private static bool Overlap(double a, double ar, double b, double br) => ar > b + Epsilon && br > a + Epsilon;
    }
}
