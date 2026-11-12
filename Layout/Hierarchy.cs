using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    public readonly struct Padding
    {
        public readonly double Left, Top, Right, Bottom;
        public Padding(double left, double top, double right, double bottom)
        { Left = left; Top = top; Right = right; Bottom = bottom; }
    }

    public sealed class NonNodeMember
    {
        public Guid Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public Box Bounds { get; set; }
        public Vec2 PivotOffset { get; set; }
    }

    public sealed class HierarchyGroup
    {
        public Guid Id { get; set; }
        // Complete message description supplied by the host, including any quotes.
        public string Label { get; set; } = string.Empty;
        public IReadOnlyList<Node> Nodes { get; set; } = Array.Empty<Node>();
        public IReadOnlyList<HierarchyGroup> Children { get; set; } = Array.Empty<HierarchyGroup>();
        public IReadOnlyList<NonNodeMember> NonNodes { get; set; } = Array.Empty<NonNodeMember>();
        public Padding Padding { get; set; }
        public bool Locked { get; set; }
    }

    public sealed class HierarchySnapshot
    {
        // Selected roots only. The host validates ownership against unselected groups too.
        public IReadOnlyList<HierarchyGroup> Groups { get; set; } = Array.Empty<HierarchyGroup>();
        public IReadOnlyList<Node> LooseNodes { get; set; } = Array.Empty<Node>();
        public IReadOnlyList<Edge> Edges { get; set; } = Array.Empty<Edge>();
        public IReadOnlyList<ExternalInbound> Inbound { get; set; } = Array.Empty<ExternalInbound>();
        public IReadOnlyList<ExternalOutbound> Outbound { get; set; } = Array.Empty<ExternalOutbound>();
        public Options Options { get; set; } = new Options();
    }

    public static class HierarchySolver
    {
        public static Result Solve(HierarchySnapshot snapshot)
        {
            try
            {
                if (snapshot != null && snapshot.Groups != null && snapshot.Groups.Count == 0)
                {
                    var result = LayoutSolver.Solve(new Snapshot { Nodes = snapshot.LooseNodes, Edges = snapshot.Edges,
                        Inbound = snapshot.Inbound, Outbound = snapshot.Outbound, Options = snapshot.Options });
                    if (!result.Success) result.FailureKind = FailureKind.Other;
                    return result;
                }
                return new Run(snapshot).Solve();
            }
            catch (HierarchyFailure error) { return new Result { Diagnostic = error.Message, FailureKind = error.Kind }; }
            catch (Exception error) { return new Result { Diagnostic = error.Message, FailureKind = FailureKind.Other }; }
        }

        private sealed class HierarchyFailure : InvalidOperationException
        {
            internal readonly FailureKind Kind;
            internal HierarchyFailure(FailureKind kind, string message) : base(message) { Kind = kind; }
        }

        private sealed class Unit
        {
            internal Node Node;
            internal HashSet<Guid> Members;
            internal Dictionary<(Guid, int), int> Inputs = new Dictionary<(Guid, int), int>();
            internal Dictionary<(Guid, int), int> Outputs = new Dictionary<(Guid, int), int>();
        }

        private sealed class Run
        {
            private readonly HierarchySnapshot snapshot;
            private readonly Dictionary<Guid, Node> nodes = new Dictionary<Guid, Node>();
            private readonly Dictionary<Guid, NonNodeMember> nonNodes = new Dictionary<Guid, NonNodeMember>();
            private readonly Dictionary<Guid, HierarchyGroup> groups = new Dictionary<Guid, HierarchyGroup>();
            private readonly Dictionary<Guid, HashSet<Guid>> members = new Dictionary<Guid, HashSet<Guid>>();
            private readonly Dictionary<Guid, List<Guid[]>> owners = new Dictionary<Guid, List<Guid[]>>();
            private readonly Dictionary<Guid, Vec2> tops = new Dictionary<Guid, Vec2>();
            private readonly Dictionary<Guid, Box> boxes = new Dictionary<Guid, Box>();
            private readonly Dictionary<Guid, Box> originalBoxes = new Dictionary<Guid, Box>();
            private readonly List<string> log = new List<string>();
            private readonly List<string> notices = new List<string>();
            private readonly HashSet<Guid> dissolved = new HashSet<Guid>();
            private readonly Dictionary<Guid, int> depths = new Dictionary<Guid, int>();
            private readonly HashSet<Guid> excluded = new HashSet<Guid>();
            private bool unstable;
            private int rounds;

            internal Run(HierarchySnapshot snapshot)
            {
                this.snapshot = snapshot ?? throw new InvalidOperationException("Missing hierarchy snapshot.");
                if (snapshot.Groups == null || snapshot.LooseNodes == null || snapshot.Edges == null ||
                    snapshot.Inbound == null || snapshot.Outbound == null || snapshot.Options == null)
                    throw new InvalidOperationException("Hierarchy collections and options must be present.");
                if (snapshot.Groups.Any(g => g == null)) throw new InvalidOperationException("Null hierarchy group.");
                foreach (var group in snapshot.Groups.OrderBy(g => g.Id)) Collect(group, Array.Empty<Guid>());
                foreach (var node in snapshot.LooseNodes.OrderBy(n => n == null ? Guid.Empty : n.Id)) AddNode(node);
                this.snapshot = new HierarchySnapshot { Groups = snapshot.Groups, LooseNodes = snapshot.LooseNodes.Where(n => !excluded.Contains(n.Id)).ToArray(),
                    Edges = snapshot.Edges.Where(e => e == null || !excluded.Contains(e.From) && !excluded.Contains(e.To)).ToArray(),
                    Inbound = snapshot.Inbound.Where(e => e == null || !excluded.Contains(e.Node)).ToArray(),
                    Outbound = snapshot.Outbound.Where(e => e == null || !excluded.Contains(e.Node)).ToArray(), Options = snapshot.Options };
                snapshot = this.snapshot;
                foreach (var entry in owners)
                    for (int i = 0; i < entry.Value.Count; i++)
                        for (int j = i + 1; j < entry.Value.Count; j++)
                        {
                            var a = entry.Value[i].Where(id => !LabelGroup(groups[id])).ToArray();
                            var b = entry.Value[j].Where(id => !LabelGroup(groups[id])).ToArray();
                            if (!a.Take(Math.Min(a.Length, b.Length)).SequenceEqual(b.Take(Math.Min(a.Length, b.Length))))
                                throw new HierarchyFailure(FailureKind.Overlap, "Overlapping membership for '" + Label(entry.Key) + "'.");
                        }
                // Validate the original port-level DAG before contraction.
                var flat = new Snapshot { Nodes = nodes.Values.ToArray(), Edges = snapshot.Edges,
                    Inbound = snapshot.Inbound, Outbound = snapshot.Outbound, Options = snapshot.Options };
                if (!Graph.TryCreate(flat, out _, out var diagnostic)) throw new InvalidOperationException(diagnostic);
                foreach (var node in nodes.Values) tops.Add(node.Id, node.CurrentTop);
                foreach (var item in nonNodes.Values)
                {
                    var b = item.Bounds;
                    if (!Graph.Finite(b.X) || !Graph.Finite(b.Y) || !Graph.Finite(b.Width) || !Graph.Finite(b.Height) ||
                        b.Width <= 0 || b.Height <= 0 || !Graph.Finite(item.PivotOffset.X) || !Graph.Finite(item.PivotOffset.Y))
                        throw new InvalidOperationException("Invalid bounds for '" + item.Label + "'.");
                    tops.Add(item.Id, new Vec2(b.X, b.Y));
                }
                foreach (var group in snapshot.Groups) OriginalBox(group);
            }

            private string Label(Guid id) => nodes.TryGetValue(id, out var n) ? n.Label : nonNodes[id].Label;
            private void AddNode(Node n)
            {
                if (n == null) throw new InvalidOperationException("Null hierarchy node.");
                if (Graph.Finite(n.Width) && Graph.Finite(n.Height) && (n.Width <= 0 || n.Height <= 0))
                {
                    if (excluded.Add(n.Id)) log.Add("Skipped node '" + n.Label + "': empty bounds after layout");
                    return;
                }
                if (nonNodes.ContainsKey(n.Id) || groups.ContainsKey(n.Id)) throw new InvalidOperationException("Duplicate object identifier.");
                if (!nodes.ContainsKey(n.Id)) nodes.Add(n.Id, n);
            }
            private HashSet<Guid> Collect(HierarchyGroup group, Guid[] path)
            {
                if (group == null) throw new InvalidOperationException("Null hierarchy group.");
                if (group.Nodes == null || group.NonNodes == null || group.Children == null ||
                    group.Children.Any(c => c == null) || group.NonNodes.Any(n => n == null))
                    throw new InvalidOperationException(group.Label + ": invalid members.");
                if (path.Contains(group.Id)) throw new InvalidOperationException(group.Label + ": group membership cycle.");
                if (groups.ContainsKey(group.Id) || nodes.ContainsKey(group.Id) || nonNodes.ContainsKey(group.Id))
                    throw new InvalidOperationException(group.Label + ": duplicate group identifier.");
                if (group.Locked) throw new HierarchyFailure(FailureKind.Locked, group.Label + ": Selection belongs to locked group.");
                var p = group.Padding;
                if (new[] { p.Left, p.Top, p.Right, p.Bottom }.Any(v => !Graph.Finite(v) || v < 0))
                    throw new InvalidOperationException(group.Label + ": invalid padding.");
                groups.Add(group.Id, group);
                depths.Add(group.Id, path.Length);
                var next = path.Concat(new[] { group.Id }).ToArray();
                var ids = new HashSet<Guid>();
                foreach (var n in group.Nodes.OrderBy(n => n == null ? Guid.Empty : n.Id))
                {
                    AddNode(n);
                    if (!excluded.Contains(n.Id)) { ids.Add(n.Id); Own(n.Id, next); }
                }
                foreach (var item in group.NonNodes)
                {
                    if (nodes.ContainsKey(item.Id) || groups.ContainsKey(item.Id)) throw new InvalidOperationException("Duplicate object identifier.");
                    if (!nonNodes.ContainsKey(item.Id)) nonNodes.Add(item.Id, item);
                    ids.Add(item.Id); Own(item.Id, next);
                }
                foreach (var child in group.Children.OrderBy(g => g.Id)) ids.UnionWith(Collect(child, next));
                members.Add(group.Id, ids);
                return ids;
            }
            private void Own(Guid id, Guid[] path)
            {
                if (!owners.TryGetValue(id, out var list)) owners.Add(id, list = new List<Guid[]>());
                list.Add(path);
            }
            // Ownership: a single-node group never creates a second layout owner.
            private bool LabelGroup(HierarchyGroup group) => members[group.Id].Count(nodes.ContainsKey) == 1;
            // Layout: a label is see-through only while its node's footprint is its whole
            // visible content. Notes give it a footprint of its own, so it then reserves
            // its box like a real group, unless a member also belongs to an unrelated
            // group: that space is not the label's alone, so it follows its node instead.
            private bool SeeThrough(HierarchyGroup group) => LabelGroup(group) &&
                (!members[group.Id].Any(nonNodes.ContainsKey) || members[group.Id].Any(Shared));
            private bool Shared(Guid id)
            {
                var paths = owners[id];
                bool Prefix(Guid[] a, Guid[] b) => a.Length <= b.Length && a.SequenceEqual(b.Take(a.Length));
                for (int i = 0; i < paths.Count; i++)
                    for (int j = i + 1; j < paths.Count; j++)
                        if (!Prefix(paths[i], paths[j]) && !Prefix(paths[j], paths[i])) return true;
                return false;
            }
            private Box ObjectBox(Guid id)
            {
                var t = tops[id];
                return nodes.TryGetValue(id, out var n) ? new Box(t.X, t.Y, n.Width, n.Height) :
                    new Box(t.X, t.Y, nonNodes[id].Bounds.Width, nonNodes[id].Bounds.Height);
            }
            private static Box Envelope(IEnumerable<Box> source)
            {
                var b = source.ToArray();
                if (b.Length == 0) throw new InvalidOperationException("Group has no measurable members.");
                double x = b.Min(r => r.X), y = b.Min(r => r.Y);
                return new Box(x, y, b.Max(r => r.Right) - x, b.Max(r => r.Bottom) - y);
            }
            private static Box Pad(Box b, Padding p) => new Box(b.X - p.Left, b.Y - p.Top,
                b.Width + p.Left + p.Right, b.Height + p.Top + p.Bottom);
            private Box OriginalBox(HierarchyGroup g)
            {
                foreach (var child in MeasurableChildren(g)) OriginalBox(child);
                if (members[g.Id].Count == 0) return default;
                var content = Envelope(PhysicalIds(g).Select(ObjectBox).Concat(MeasurableChildren(g).Select(c => originalBoxes[c.Id])));
                return originalBoxes[g.Id] = Pad(content, g.Padding);
            }
            private IEnumerable<HierarchyGroup> MeasurableChildren(HierarchyGroup g) => g.Children.Where(c => members[c.Id].Count > 0);
            private IEnumerable<HierarchyGroup> LayoutChildren(HierarchyGroup g) => MeasurableChildren(g)
                .SelectMany(c => dissolved.Contains(c.Id) ? LayoutChildren(c) : new[] { c });
            private IEnumerable<HierarchyGroup> RealChildren(HierarchyGroup g) => LayoutChildren(g).Where(c => !SeeThrough(c));
            // Labels affect the visible box (and therefore measured padding), but never
            // compete as layout units. Keep those envelopes separate.
            private IEnumerable<Guid> PhysicalIds(HierarchyGroup g)
            {
                var indirect = new HashSet<Guid>(g.Children.SelectMany(c => members[c.Id]));
                return g.Nodes.Select(n => n.Id).Concat(g.NonNodes.Select(n => n.Id)).Where(id => !excluded.Contains(id) && !indirect.Contains(id)).Distinct();
            }
            private IEnumerable<Guid> LayoutPhysicalIds(HierarchyGroup g) => PhysicalIds(g)
                .Concat(g.Children.Where(c => dissolved.Contains(c.Id)).SelectMany(LayoutPhysicalIds)).Distinct();
            private IEnumerable<Guid> DirectIds(HierarchyGroup g)
            {
                var childIds = new HashSet<Guid>(RealChildren(g).SelectMany(c => members[c.Id]));
                return members[g.Id].Where(id => !childIds.Contains(id)).OrderBy(id => id);
            }

            // Dissolution changes only the layout tree. Visible boxes still follow
            // the original membership tree, including all padding and label boxes.
            private void DissolveCycles()
            {
                while (true)
                {
                    var candidates = new List<(HierarchyGroup Child, HierarchyGroup Parent)>();
                    foreach (var parent in groups.Values.Where(g => !dissolved.Contains(g.Id)))
                    {
                        var children = RealChildren(parent).ToArray();
                        var owner = members[parent.Id].ToDictionary(id => id, id => id);
                        foreach (var child in children) foreach (var id in members[child.Id]) owner[id] = child.Id;
                        var links = snapshot.Edges.Where(e => owner.ContainsKey(e.From) && owner.ContainsKey(e.To))
                            .Select(e => (From: owner[e.From], To: owner[e.To])).Where(e => e.From != e.To).Distinct().ToArray();
                        foreach (var child in children)
                        {
                            var seen = new HashSet<Guid>(); var queue = new Queue<Guid>(links.Where(e => e.From == child.Id).Select(e => e.To));
                            while (queue.Count > 0)
                            {
                                var id = queue.Dequeue();
                                if (id == child.Id) { candidates.Add((child, parent)); break; }
                                if (seen.Add(id)) foreach (var link in links.Where(e => e.From == id)) queue.Enqueue(link.To);
                            }
                        }
                    }
                    if (candidates.Count == 0) return;
                    var next = candidates.OrderBy(c => depths[c.Child.Id]).ThenBy(c => c.Child.Id).ThenBy(c => c.Parent.Id).First();
                    dissolved.Add(next.Child.Id);
                    log.Add(next.Child.Label + " was laid out with " + next.Parent.Label + ": its wires leave and re-enter it");
                    notices.Add(log[log.Count - 1]);
                }
            }

            private Unit[] UniqueUnits(IEnumerable<Unit> source)
            {
                var result = new List<Unit>();
                foreach (var unit in source.OrderBy(u => u.Node.Kind == NodeKind.Group ? 0 : 1).ThenBy(u => u.Node.Id))
                {
                    var previous = result.FirstOrDefault(u => u.Members.Contains(unit.Node.Id));
                    if (previous != null) { previous.Members.UnionWith(unit.Members); continue; }
                    unit.Members = new HashSet<Guid>(unit.Members);
                    result.Add(unit);
                }
                return result.ToArray();
            }

            internal Result Solve()
            {
                DissolveCycles();
                var roots = snapshot.Groups.Where(g => members[g.Id].Count > 0).ToArray();
                var covered = new HashSet<Guid>(snapshot.Groups.SelectMany(g => members[g.Id]));
                var loose = snapshot.LooseNodes.Where(n => !covered.Contains(n.Id)).OrderBy(n => n.Id).ToArray();
                var originalUnits = roots.Select(g => SeeThrough(g) ? ObjectBox(members[g.Id].Single(nodes.ContainsKey)) : originalBoxes[g.Id])
                    .Concat(loose.Select(n => ObjectBox(n.Id))).ToArray();
                foreach (var g in roots.OrderBy(g => g.Id)) SolveGroup(g);
                var units = UniqueUnits(roots.Select(GroupUnit).Concat(loose.Select(NodeUnit)));
                if (units.Length > 1 || snapshot.Groups.Count == 0)
                {
                    var anchor = originalUnits.Length == 0 ? new Vec2(0, 0) :
                        new Vec2(originalUnits.Min(b => b.X), originalUnits.Min(b => b.Y));
                    SolveLevel(units, anchor, "Selection", true);
                }
                RefreshBoxes();
                LogNoteOverlaps();
                var moves = tops.OrderBy(p => p.Key).Where(p => {
                    var old = nodes.TryGetValue(p.Key, out var n) ? n.CurrentTop : new Vec2(nonNodes[p.Key].Bounds.X, nonNodes[p.Key].Bounds.Y);
                    if (n != null) return LayoutSolver.Round(p.Value.X + n.PivotOffset.X) != LayoutSolver.Round(old.X + n.PivotOffset.X) ||
                        LayoutSolver.Round(p.Value.Y + n.PivotOffset.Y) != LayoutSolver.Round(old.Y + n.PivotOffset.Y);
                    return Math.Abs(p.Value.X - old.X) > 1e-8 || Math.Abs(p.Value.Y - old.Y) > 1e-8;
                }).Select(p => {
                    var offset = nodes.TryGetValue(p.Key, out var n) ? n.PivotOffset : nonNodes[p.Key].PivotOffset;
                    var pivot = new Vec2(p.Value.X + offset.X, p.Value.Y + offset.Y);
                    return new Move { Id = p.Key, Pivot = n == null ? pivot : new Vec2(LayoutSolver.Round(pivot.X), LayoutSolver.Round(pivot.Y)) };
                }).ToArray();
                return new Result { Success = true, Tops = new SortedDictionary<Guid, Vec2>(tops), Moves = moves,
                    GroupBoxes = new SortedDictionary<Guid, Box>(boxes), Log = log.ToArray(), Notices = notices.ToArray(),
                    DissolvedCount = dissolved.Count, Unstable = unstable, Rounds = rounds,
                    Anchor = originalUnits.Length == 0 ? new Vec2(0, 0) : new Vec2(originalUnits.Min(b => b.X), originalUnits.Min(b => b.Y)) };
            }
            private void SolveGroup(HierarchyGroup g)
            {
                var direct = DirectIds(g).ToArray();
                var children = RealChildren(g).ToArray();
                // Measure before the children grow, excluding notes and see-through label boxes.
                var oldUnits = direct.Where(nodes.ContainsKey).Select(ObjectBox)
                    .Concat(children.Select(c => originalBoxes[c.Id])).ToArray();
                foreach (var child in RealChildren(g).OrderBy(c => c.Id)) SolveGroup(child);
                if (oldUnits.Length > 0)
                {
                    var before = Envelope(oldUnits);
                    // A see-through label uses only its node's footprint, while all
                    // of its members still translate together at this outer level.
                    var labelIds = new HashSet<Guid>(LayoutChildren(g).Where(SeeThrough).SelectMany(c => members[c.Id]));
                    var units = UniqueUnits(direct.Where(id => nodes.ContainsKey(id) && !labelIds.Contains(id)).Select(id => NodeUnit(nodes[id]))
                        .Concat(LayoutChildren(g).Select(GroupUnit)));
                    SolveLevel(units, new Vec2(before.X, before.Y), g.Label,
                        snapshot.Groups.Count == 1 && snapshot.Groups[0].Id == g.Id &&
                        !snapshot.LooseNodes.Any(n => !members[g.Id].Contains(n.Id)));
                    var after = Envelope(direct.Where(nodes.ContainsKey).Select(ObjectBox)
                        .Concat(children.Select(c => boxes[c.Id])));
                    double growthX = Math.Max(0, Math.Ceiling(after.Right - before.Right - 1e-7)),
                        growthY = Math.Max(0, Math.Ceiling(after.Bottom - before.Bottom - 1e-7));
                    foreach (var id in LayoutPhysicalIds(g).Where(nonNodes.ContainsKey).OrderBy(id => id))
                    {
                        var note = ObjectBox(id);
                        tops[id] = new Vec2(note.X + (note.X >= before.Right ? growthX : 0),
                            note.Y + (note.Y >= before.Bottom ? growthY : 0));
                    }
                }
                RefreshBox(g);
            }
            private void LogNoteOverlaps()
            {
                foreach (var g in groups.Values.OrderBy(g => g.Id))
                {
                    var formattedNodes = members[g.Id].Where(nodes.ContainsKey).Select(ObjectBox).ToArray();
                    foreach (var id in PhysicalIds(g).Where(nonNodes.ContainsKey).OrderBy(id => id))
                    {
                        var note = ObjectBox(id);
                        if (formattedNodes.Any(n => n.Right > note.X + 1e-7 && note.Right > n.X + 1e-7 &&
                            n.Bottom > note.Y + 1e-7 && note.Bottom > n.Y + 1e-7))
                            log.Add("Note '" + nonNodes[id].Label + "' overlaps formatted nodes in " + g.Label);
                    }
                }
            }
            private void RefreshBoxes() { foreach (var g in snapshot.Groups) RefreshBox(g); }
            private Box RefreshBox(HierarchyGroup g)
            {
                foreach (var child in MeasurableChildren(g)) RefreshBox(child);
                if (members[g.Id].Count == 0) return default;
                return boxes[g.Id] = Pad(Envelope(PhysicalIds(g).Select(ObjectBox)
                    .Concat(MeasurableChildren(g).Select(c => boxes[c.Id]))), g.Padding);
            }
            private Unit NodeUnit(Node n) => new Unit { Node = new Node { Id = n.Id, Label = n.Label, Kind = n.Kind,
                Width = n.Width, Height = n.Height, CurrentTop = tops[n.Id], PivotOffset = n.PivotOffset,
                Inputs = n.Inputs, Outputs = n.Outputs, Widget = n.Widget }, Members = new HashSet<Guid> { n.Id } };
            private Unit GroupUnit(HierarchyGroup g)
            {
                if (SeeThrough(g))
                {
                    var labelUnit = NodeUnit(nodes[members[g.Id].Single(nodes.ContainsKey)]);
                    labelUnit.Members = members[g.Id];
                    return labelUnit;
                }
                var b = boxes[g.Id];
                // Refer to a descendant pivot, making every outer translation integral.
                var id = members[g.Id].Where(nodes.ContainsKey).OrderBy(k => k).FirstOrDefault();
                var pivot = id != Guid.Empty ? new Vec2(tops[id].X + nodes[id].PivotOffset.X, tops[id].Y + nodes[id].PivotOffset.Y) :
                    new Vec2(LayoutSolver.Round(b.X), LayoutSolver.Round(b.Y));
                var u = new Unit { Members = members[g.Id], Node = new Node { Id = g.Id, Label = g.Label, Kind = NodeKind.Group,
                    CurrentTop = new Vec2(b.X, b.Y), Width = b.Width, Height = b.Height,
                    PivotOffset = new Vec2(pivot.X - b.X, pivot.Y - b.Y) } };
                if (LabelGroup(g))
                {
                    // A label with notes reserves its whole box but keeps its node's role.
                    var only = nodes[members[g.Id].Single(nodes.ContainsKey)];
                    u.Node.ParamRole = only.Kind == NodeKind.Param;
                    u.Node.AlignRight = tops[only.Id].X + only.Width - b.X;
                }
                // Synthetic port indices follow their visible vertical order. Endpoint
                // identities remain separate, including parallel wires on different ports.
                var inputs = snapshot.Edges.Where(e => u.Members.Contains(e.To) && !u.Members.Contains(e.From)).Select(e => (e.To, e.ToPort))
                    .Concat(snapshot.Inbound.Where(e => u.Members.Contains(e.Node)).Select(e => (e.Node, e.Port)))
                    .Distinct().OrderBy(p => Grip(p.Item1, p.Item2, true, b).Y).ThenBy(p => p.Item1).ThenBy(p => p.Item2).ToArray();
                var outputs = snapshot.Edges.Where(e => u.Members.Contains(e.From) && !u.Members.Contains(e.To)).Select(e => (e.From, e.FromPort))
                    .Concat(snapshot.Outbound.Where(e => u.Members.Contains(e.Node)).Select(e => (e.Node, e.Port)))
                    .Distinct().OrderBy(p => Grip(p.Item1, p.Item2, false, b).Y).ThenBy(p => p.Item1).ThenBy(p => p.Item2).ToArray();
                u.Node.Inputs = inputs.Select((p, i) => { u.Inputs.Add(p, i); return Grip(p.Item1, p.Item2, true, b); }).ToArray();
                u.Node.Outputs = outputs.Select((p, i) => { u.Outputs.Add(p, i); return Grip(p.Item1, p.Item2, false, b); }).ToArray();
                return u;
            }
            private Vec2 Grip(Guid id, int port, bool input, Box relative)
            {
                var g = input ? nodes[id].Inputs[port] : nodes[id].Outputs[port];
                return new Vec2(tops[id].X + g.X - relative.X, tops[id].Y + g.Y - relative.Y);
            }
            private void SolveLevel(Unit[] units, Vec2 anchor, string label, bool fixedFrame)
            {
                if (units.Length == 0) return;
                var owner = new Dictionary<Guid, Unit>();
                foreach (var unit in units)
                    foreach (var id in unit.Members.OrderBy(id => id))
                        if (!owner.TryAdd(id, unit))
                            log.Add("Shared member '" + Label(id) + "': later owner ignored in " + label);
                int Port(Unit u, Guid id, int port, bool input) => u.Node.Kind != NodeKind.Group ? port :
                    (input ? u.Inputs : u.Outputs)[(id, port)];
                var edges = new List<Edge>(); var inbound = new List<ExternalInbound>(); var outbound = new List<ExternalOutbound>();
                foreach (var e in snapshot.Edges)
                {
                    bool from = owner.TryGetValue(e.From, out var a), to = owner.TryGetValue(e.To, out var b);
                    if (from && to && a != b) edges.Add(new Edge { From = a.Node.Id, To = b.Node.Id,
                        FromPort = Port(a, e.From, e.FromPort, false), ToPort = Port(b, e.To, e.ToPort, true) });
                    else if (from && !to) outbound.Add(new ExternalOutbound { Node = a.Node.Id, Port = Port(a, e.From, e.FromPort, false),
                        TargetGripY = tops[e.To].Y + nodes[e.To].Inputs[e.ToPort].Y, TargetObjectId = e.To });
                    else if (!from && to) inbound.Add(new ExternalInbound { Node = b.Node.Id, Port = Port(b, e.To, e.ToPort, true),
                        SourceGripY = tops[e.From].Y + nodes[e.From].Outputs[e.FromPort].Y, SourceObjectId = e.From });
                }
                inbound.AddRange(snapshot.Inbound.Where(e => owner.ContainsKey(e.Node)).Select(e => new ExternalInbound {
                    Node = owner[e.Node].Node.Id, Port = Port(owner[e.Node], e.Node, e.Port, true), SourceGripY = e.SourceGripY,
                    SourceGripX = fixedFrame ? e.SourceGripX : null, SourceObjectId = e.SourceObjectId }));
                outbound.AddRange(snapshot.Outbound.Where(e => owner.ContainsKey(e.Node)).Select(e => new ExternalOutbound {
                    Node = owner[e.Node].Node.Id, Port = Port(owner[e.Node], e.Node, e.Port, false), TargetGripY = e.TargetGripY,
                    TargetGripX = fixedFrame ? e.TargetGripX : null, TargetObjectId = e.TargetObjectId }));
                var result = LayoutSolver.Solve(new Snapshot { Nodes = units.Select(u => u.Node).ToArray(), Edges = edges,
                    Inbound = inbound, Outbound = outbound, Options = snapshot.Options });
                if (!result.Success) throw new InvalidOperationException(label + ": contracted graph: " + result.Diagnostic);
                unstable |= result.Unstable; rounds = Math.Max(rounds, result.Rounds);
                log.AddRange(result.Log.Select(line => label + ": " + line));
                notices.AddRange(result.Notices.Select(line => label + ": " + line));
                double dx = LayoutSolver.Round(anchor.X - result.Tops.Values.Min(t => t.X)),
                    dy = LayoutSolver.Round(anchor.Y - result.Tops.Values.Min(t => t.Y));
                foreach (var u in units)
                {
                    var t = result.Tops[u.Node.Id];
                    double tx = t.X + dx - u.Node.CurrentTop.X, ty = t.Y + dy - u.Node.CurrentTop.Y;
                    foreach (var id in u.Members.Where(id => owner[id] == u)) tops[id] = new Vec2(tops[id].X + tx, tops[id].Y + ty);
                }
                RefreshBoxes();
            }
        }
    }
}
