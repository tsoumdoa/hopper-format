using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    public sealed class FormatAllTarget
    {
        public Guid Id { get; set; }
        // Complete message description supplied by the host, including any quotes.
        public string Label { get; set; } = string.Empty;
        public Box Bounds { get; set; }
        public HierarchySnapshot Hierarchy { get; set; }
        public string SkipReason { get; set; }
        public bool Fixed { get; set; }
        public IReadOnlyList<Guid> RigidNodeIds { get; set; } = Array.Empty<Guid>();
        // Captured independently of inner formatting, so failed roots can move rigidly.
        public IReadOnlyList<NonNodeMember> RigidMembers { get; set; } = Array.Empty<NonNodeMember>();
        public IReadOnlyDictionary<Guid, Box> RigidGroupBoxes { get; set; } = new Dictionary<Guid, Box>();
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
            var notices = new List<string>();
            void Notice(string line) { log.Add(line); notices.Add(line); }
            int skipped = 0, formatted = 0;
            var targetGroups = new Dictionary<Guid, HierarchyGroup[]>();
            var nodeIds = new Dictionary<Guid, Guid[]>();
            var failures = new Dictionary<Guid, string>();
            var physicalMembers = new Dictionary<Guid, Dictionary<Guid, NonNodeMember>>();
            var originalBoxes = new Dictionary<Guid, Dictionary<Guid, Box>>();
            foreach (var target in snapshot.Targets.OrderBy(t => t.Id))
            {
                try
                {
                    targetGroups[target.Id] = target.Hierarchy == null ? Array.Empty<HierarchyGroup>() : target.Hierarchy.Groups.SelectMany(Groups).ToArray();
                    nodeIds[target.Id] = targetGroups[target.Id].SelectMany(g => g.Nodes).Select(n => n.Id).Concat(target.RigidNodeIds).Distinct().ToArray();
                    physicalMembers[target.Id] = PhysicalMembers(target).ToDictionary(n => n.Id);
                    originalBoxes[target.Id] = OriginalBoxes(target);
                }
                catch (Exception error)
                {
                    failures[target.Id] = error.Message;
                    targetGroups[target.Id] = Array.Empty<HierarchyGroup>();
                    nodeIds[target.Id] = target.RigidNodeIds.ToArray();
                    physicalMembers[target.Id] = target.RigidMembers.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                    originalBoxes[target.Id] = target.RigidGroupBoxes.ToDictionary(p => p.Key, p => p.Value);
                    originalBoxes[target.Id][target.Id] = target.Bounds;
                }
            }
            // A non-nested label is an annotation on its node, not a second
            // independently movable owner. Use the containing root (or stable label).
            var aliases = new Dictionary<Guid, Guid>();
            foreach (var target in snapshot.Targets.Where(t => nodeIds[t.Id].Length == 1).OrderBy(t => t.Id))
            {
                var owner = snapshot.Targets.Where(t => nodeIds[t.Id].Contains(nodeIds[target.Id][0]))
                    .OrderBy(t => nodeIds[t.Id].Length == 1 ? 1 : 0).ThenBy(t => t.Id).First();
                if (owner.Id != target.Id) aliases.Add(target.Id, owner.Id);
            }
            var ownership = snapshot.Targets.Where(t => !aliases.ContainsKey(t.Id) && nodeIds[t.Id].Length != 1)
                .SelectMany(t => targetGroups[t.Id].SelectMany(g => ObjectIds(g, new HashSet<Guid>())).Distinct()
                    .Select(id => (Id: id, Target: t.Id)))
                .GroupBy(p => p.Id).Where(g => g.Select(p => p.Target).Distinct().Count() > 1)
                .SelectMany(g => g.Select(p => p.Target)).ToHashSet();
            var lockedNodes = snapshot.Targets.Where(t => t.Fixed || targetGroups[t.Id].Any(g => g.Locked))
                .SelectMany(t => nodeIds[t.Id]).ToHashSet();
            foreach (var target in snapshot.Targets.Where(t => !aliases.ContainsKey(t.Id)).OrderBy(t => t.Id))
            {
                bool sharedLock = nodeIds[target.Id].Any(lockedNodes.Contains);
                var reason = target.SkipReason ?? (failures.TryGetValue(target.Id, out var failure) ? failure : null) ?? (ownership.Contains(target.Id) ? "Overlapping membership between top-level groups" :
                    sharedLock ? "Selection belongs to locked or fixed group" : null);
                Result result = null;
                try
                {
                    if (reason == null && target.Hierarchy != null)
                    {
                        var h = target.Hierarchy;
                        // Tidy will move these roots after their independent solves.
                        result = HierarchySolver.Solve(new HierarchySnapshot { Groups = h.Groups, LooseNodes = h.LooseNodes,
                            Edges = h.Edges, Options = h.Options,
                            Inbound = h.Inbound.Select(e => new ExternalInbound { Node = e.Node, Port = e.Port,
                                SourceGripY = e.SourceGripY, SourceObjectId = e.SourceObjectId }).ToArray(),
                            Outbound = h.Outbound.Select(e => new ExternalOutbound { Node = e.Node, Port = e.Port,
                                TargetGripY = e.TargetGripY, TargetObjectId = e.TargetObjectId }).ToArray() });
                    }
                    reason = reason ?? (result == null ? "Missing hierarchy snapshot" : result.Success ? null : result.Diagnostic);
                    if (reason == null && !result.GroupBoxes.ContainsKey(target.Id)) reason = "no measurable members";
                }
                catch (Exception error) { reason = error.Message; }
                bool fixedTarget = target.Fixed || sharedLock || ownership.Contains(target.Id) || targetGroups[target.Id].Any(g => g.Locked)
                    || result?.FailureKind == FailureKind.Overlap || result?.FailureKind == FailureKind.Locked;
                if (reason != null)
                {
                    skipped++;
                    Notice(Skipped(target, reason) + (fixedTarget ? " (fixed)" :
                        Measurable(target.Bounds) ? " (moves rigidly)" : " (no measurable outline)"));
                    if (fixedTarget)
                    {
                        if (Measurable(target.Bounds)) obstacles.Add(target.Bounds);
                        continue;
                    }
                    if (!Measurable(target.Bounds)) continue;
                    result = new Result { Success = true,
                        Tops = physicalMembers[target.Id].ToDictionary(p => p.Key, p => new Vec2(p.Value.Bounds.X, p.Value.Bounds.Y)),
                        GroupBoxes = originalBoxes[target.Id] };
                }
                else formatted++;
                solved.Add((target, result));
                log.AddRange(result.Log);
                notices.AddRange(result.Notices);
            }
            if (solved.Count == 0)
            {
                foreach (var alias in snapshot.Targets.Where(t => aliases.ContainsKey(t.Id)).OrderBy(t => t.Id))
                { skipped++; Notice(Skipped(alias, "its node belongs to a fixed group") + " (fixed)"); }
                Notice("Format all: 0 formatted, " + skipped + " skipped, 0 dissolved");
                return new FormatAllResult { Skipped = skipped, Layout = new Result {
                    Diagnostic = "No groups to format", Log = log.ToArray(), Notices = log.ToArray() } };
            }

            var boxes = solved.ToDictionary(s => s.Target.Id, s => s.Result.GroupBoxes[s.Target.Id]);
            var tidy = GroupTidy.Solve(boxes, obstacles, snapshot.Options);
            if (!tidy.Success) return new FormatAllResult { Skipped = skipped, Layout = tidy };
            log.AddRange(tidy.Log);
            notices.AddRange(tidy.Notices);
            var tops = new SortedDictionary<Guid, Vec2>();
            var groupBoxes = new SortedDictionary<Guid, Box>();
            var moves = new SortedDictionary<Guid, Move>();
            foreach (var item in solved)
            {
                var before = boxes[item.Target.Id]; var after = tidy.GroupBoxes[item.Target.Id];
                double dx = after.X - before.X, dy = after.Y - before.Y;
                var physical = physicalMembers[item.Target.Id];
                foreach (var pair in item.Result.Tops)
                {
                    var top = new Vec2(pair.Value.X + dx, pair.Value.Y + dy);
                    if (!tops.TryAdd(pair.Key, top))
                    {
                        log.Add("Shared member '" + physical[pair.Key].Label + "': later owner ignored in " + item.Target.Label);
                        continue;
                    }
                    var member = physical[pair.Key]; var old = member.Bounds; var offset = member.PivotOffset;
                    if (!SamePivot(top.X + offset.X, old.X + offset.X) || !SamePivot(top.Y + offset.Y, old.Y + offset.Y))
                        moves.Add(pair.Key, new Move { Id = pair.Key, Pivot = new Vec2(top.X + offset.X, top.Y + offset.Y) });
                }
                foreach (var pair in item.Result.GroupBoxes)
                    groupBoxes.Add(pair.Key, Translate(pair.Value, dx, dy));
            }
            foreach (var alias in snapshot.Targets.Where(t => aliases.ContainsKey(t.Id)).OrderBy(t => t.Id))
            {
                var node = physicalMembers[alias.Id][nodeIds[alias.Id][0]];
                if (!tops.TryGetValue(node.Id, out var top))
                { skipped++; Notice(Skipped(alias, "its node belongs to a fixed group") + " (fixed)"); continue; }
                if (alias.SkipReason == null) formatted++;
                else { skipped++; Notice(Skipped(alias, alias.SkipReason) + " (moves rigidly with its node)"); }
                double dx = LayoutSolver.Round(top.X - node.Bounds.X), dy = LayoutSolver.Round(top.Y - node.Bounds.Y);
                foreach (var pair in originalBoxes[alias.Id]) groupBoxes[pair.Key] = Translate(pair.Value, dx, dy);
                foreach (var member in physicalMembers[alias.Id].Values)
                {
                    if (tops.ContainsKey(member.Id))
                    {
                        if (member.Id != node.Id) log.Add("Shared member '" + member.Label + "': later owner ignored in " + alias.Label);
                        continue;
                    }
                    var moved = new Vec2(member.Bounds.X + dx, member.Bounds.Y + dy);
                    tops.Add(member.Id, moved);
                    if (!SamePivot(moved.X + member.PivotOffset.X, member.Bounds.X + member.PivotOffset.X)
                        || !SamePivot(moved.Y + member.PivotOffset.Y, member.Bounds.Y + member.PivotOffset.Y)) moves.Add(member.Id, new Move { Id = member.Id,
                        Pivot = new Vec2(moved.X + member.PivotOffset.X, moved.Y + member.PivotOffset.Y) });
                }
            }
            int dissolved = solved.Sum(s => s.Result.DissolvedCount);
            Notice("Format all: " + formatted + " formatted, " + skipped + " skipped, " + dissolved + " dissolved");
            return new FormatAllResult { Formatted = formatted, Skipped = skipped, Layout = new Result {
                Success = true, Tops = tops, GroupBoxes = groupBoxes, Moves = moves.Values.ToArray(),
                Rounds = tidy.Rounds, Unstable = tidy.Unstable || solved.Any(s => s.Result.Unstable), Log = log.ToArray(),
                Notices = notices.ToArray(), DissolvedCount = dissolved } };
        }

        private static string Skipped(FormatAllTarget target, string reason)
        {
            // Inner diagnostics already carry their group context. Keep it once when
            // the failed group is also the format-all target; retain distinct child context.
            if (reason.StartsWith(target.Label + ": ", StringComparison.Ordinal)) reason = reason.Substring(target.Label.Length + 2);
            return "Skipped " + target.Label + ": " + reason;
        }

        private static IEnumerable<NonNodeMember> PhysicalMembers(FormatAllTarget target)
        {
            var groups = target.Hierarchy == null ? Array.Empty<HierarchyGroup>() : target.Hierarchy.Groups.SelectMany(Groups);
            return target.RigidMembers.Concat(groups.SelectMany(g => g.NonNodes.Concat(g.Nodes.Select(n => new NonNodeMember {
                Id = n.Id, Label = n.Label, Bounds = new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height), PivotOffset = n.PivotOffset }))))
                .GroupBy(n => n.Id).Select(g => g.First());
        }
        private static Dictionary<Guid, Box> OriginalBoxes(FormatAllTarget target)
        {
            var boxes = target.RigidGroupBoxes.ToDictionary(p => p.Key, p => p.Value);
            if (target.Hierarchy != null)
            {
                Box? Measure(HierarchyGroup g)
                {
                    var content = g.Nodes.Select(n => new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height))
                        .Concat(g.NonNodes.Select(n => n.Bounds)).Concat(g.Children.Select(Measure).Where(b => b.HasValue).Select(b => b.Value))
                        .Where(b => b.Width > 0 && b.Height > 0).ToArray();
                    if (content.Length == 0) return null;
                    double x = content.Min(b => b.X) - g.Padding.Left, y = content.Min(b => b.Y) - g.Padding.Top;
                    var box = new Box(x, y, content.Max(b => b.Right) + g.Padding.Right - x, content.Max(b => b.Bottom) + g.Padding.Bottom - y);
                    if (!boxes.ContainsKey(g.Id)) boxes.Add(g.Id, box);
                    return boxes[g.Id];
                }
                foreach (var root in target.Hierarchy.Groups) Measure(root);
            }
            if (target.Bounds.Width > 0 && target.Bounds.Height > 0) boxes[target.Id] = target.Bounds;
            return boxes;
        }
        private static IEnumerable<HierarchyGroup> Groups(HierarchyGroup g) => Groups(g, new HashSet<Guid>());
        private static IEnumerable<HierarchyGroup> Groups(HierarchyGroup g, HashSet<Guid> seen)
        {
            if (g == null || !seen.Add(g.Id)) throw new InvalidOperationException("Invalid or cyclic hierarchy group.");
            yield return g;
            foreach (var child in g.Children.SelectMany(c => Groups(c, seen))) yield return child;
        }
        internal static bool Measurable(Box b) => Graph.Finite(b.X) && Graph.Finite(b.Y) && Graph.Finite(b.Width) && Graph.Finite(b.Height)
            && Graph.Finite(b.Right) && Graph.Finite(b.Bottom) && b.Width > 0 && b.Height > 0;
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
            if (targets.Values.Concat(obstacles).Any(b => !FormatAllSolver.Measurable(b)))
                return new Result { Diagnostic = "Group and obstacle bounds must be finite and measurable." };
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
                    var sameRow = new List<Box>();
                    var rowBoxes = new Dictionary<Guid, Box>();
                    foreach (var id in row.Members.OrderBy(id => lefts[id]).ThenBy(id => id))
                    {
                        var b = boxes[id]; double x = lefts[id];
                        double y = Math.Abs(top - row.Top) > Epsilon ? GapY(id, top)
                            : aligned ? b.Y : SnapY(id, rowAnchor);
                        // Only earlier neighbours in this snapped row may push right.
                        if (sameRow.Count > 0)
                        {
                            double right = sameRow.Max(p => p.Right);
                            if (x - right < 2 * gx - Epsilon) x = GapX(id, right + gx);
                        }
                        var result = new Box(x, y, b.Width, b.Height);
                        rowBoxes.Add(id, result); sameRow.Add(result);
                    }
                    // Fixed objects and already placed rows can only move this
                    // entire row downward, including obstacles below its old top.
                    while (true)
                    {
                        double push = 0;
                        foreach (var b in rowBoxes.Values)
                            foreach (var p in placed)
                                if (Overlap(p.Y, p.Bottom, b.Y - gy, b.Bottom + gy) &&
                                    Overlap(p.X, p.Right, b.X - gx, b.Right + gx))
                                    push = Math.Max(push, Math.Ceiling(p.Bottom + gy - b.Y - Epsilon));
                        if (push <= 0) break;
                        foreach (var id in row.Members) rowBoxes[id] = FormatAllSolver.Translate(rowBoxes[id], 0, push);
                    }
                    foreach (var pair in rowBoxes) { next.Add(pair.Key, pair.Value); placed.Add(pair.Value); }
                }
                var nextTranslations = next.ToDictionary(p => p.Key, p => new Vec2(
                    LayoutSolver.Round(p.Value.X - targets[p.Key].X), LayoutSolver.Round(p.Value.Y - targets[p.Key].Y)));
                changed = translations.Any(p => nextTranslations[p.Key].X != p.Value.X || nextTranslations[p.Key].Y != p.Value.Y);
                translations = nextTranslations;
                boxes = next;
                if (!changed) break;
            }
            var notices = changed ? new[] { "Unstable: format-all tidy did not settle within five passes; kept the last result." } : Array.Empty<string>();
            return new Result { Success = true, GroupBoxes = new SortedDictionary<Guid, Box>(boxes), Rounds = rounds,
                Unstable = changed, Log = notices, Notices = notices };
        }

        private static bool Overlap(double a, double ar, double b, double br) => ar > b + Epsilon && br > a + Epsilon;
    }
}
