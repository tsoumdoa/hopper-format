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
                var result = reason == null ? HierarchySolver.Solve(target.Hierarchy) : null;
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
            // All coordinates are reconstructed from the starting box and an integer
            // translation, including across passes. Fractional padding stays rigid.
            var translations = targets.ToDictionary(p => p.Key, _ => new Vec2(0, 0));
            double SnapX(Guid id, double x) => targets[id].X + LayoutSolver.Round(x - targets[id].X);
            double SnapY(Guid id, double y) => targets[id].Y + LayoutSolver.Round(y - targets[id].Y);
            double GapX(Guid id, double x) => targets[id].X + Math.Ceiling(x - targets[id].X - Epsilon);
            double GapY(Guid id, double y) => targets[id].Y + Math.Ceiling(y - targets[id].Y - Epsilon);
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
                var columns = new List<List<Guid>>();
                foreach (var id in boxes.Keys.OrderBy(id => boxes[id].X).ThenBy(id => id))
                {
                    var column = columns.LastOrDefault();
                    if (column == null || boxes[id].X - boxes[column[0]].X > gx / 2 + Epsilon)
                    {
                        column = new List<Guid>(); columns.Add(column);
                    }
                    column.Add(id);
                }
                foreach (var column in columns)
                {
                    double left = boxes[column[0]].X;
                    // The smallest-Guid representative prevents snap/even/push
                    // interactions from changing the snap reference between passes.
                    double anchor = boxes[column.OrderBy(id => id).First()].X;
                    bool aligned = column.Max(id => boxes[id].X) - left < 1 - Epsilon;
                    foreach (var id in column) lefts[id] = aligned ? boxes[id].X : SnapX(id, anchor);
                }
                var placed = obstacles.ToList();
                var next = new Dictionary<Guid, Box>();
                foreach (var row in rows)
                {
                    bool aligned = row.Members.Max(id => boxes[id].Y) - row.Top < 1 - Epsilon;
                    double rowAnchor = boxes[row.Members.OrderBy(id => id).First()].Y;
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
                        // Keep a snapped row within one unit, and round
                        // a shared vertical clearance upward for every member.
                        double y = Math.Abs(top - row.Top) > Epsilon ? GapY(id, top)
                            : aligned ? b.Y : SnapY(id, rowAnchor);
                        var sameHeight = placed.Where(p => Overlap(p.Y, p.Bottom, y, y + b.Height)).ToArray();
                        var left = sameHeight.Where(p => p.X < x - Epsilon).ToArray();
                        if (left.Length > 0)
                        {
                            double right = left.Max(p => p.Right);
                            if (x - right < 2 * gx - Epsilon) x = GapX(id, right + gx);
                        }
                        while (true)
                        {
                            var collisions = placed.Where(p => Overlap(p.Y, p.Bottom, y - gy, y + b.Height + gy)
                                && x < p.Right + gx - Epsilon && x + b.Width > p.X - gx + Epsilon).ToArray();
                            if (collisions.Length == 0) break;
                            x = GapX(id, collisions.Max(p => p.Right) + gx);
                        }
                        var result = new Box(x, y, b.Width, b.Height);
                        next.Add(id, result); placed.Add(result);
                    }
                }
                var nextTranslations = next.ToDictionary(p => p.Key, p => new Vec2(
                    LayoutSolver.Round(p.Value.X - targets[p.Key].X), LayoutSolver.Round(p.Value.Y - targets[p.Key].Y)));
                changed = translations.Any(p => nextTranslations[p.Key].X != p.Value.X || nextTranslations[p.Key].Y != p.Value.Y);
                translations = nextTranslations;
                boxes = next;
                if (!changed) break;
            }
            return new Result { Success = true, GroupBoxes = new SortedDictionary<Guid, Box>(boxes), Rounds = rounds,
                Unstable = changed, Log = changed ? new[] { "Unstable: format-all tidy did not settle within five passes; kept the last result." } : Array.Empty<string>() };
        }

        private static bool Overlap(double a, double ar, double b, double br) => ar > b + Epsilon && br > a + Epsilon;
    }
}
