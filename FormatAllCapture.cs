using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal sealed class FormatAllCapture
    {
        internal FormatAllSnapshot Snapshot { get; private set; }
        internal GroupMembership.Index Membership { get; private set; }
        internal readonly Dictionary<Guid, IGH_DocumentObject> Objects = new Dictionary<Guid, IGH_DocumentObject>();
        internal readonly List<string> Log = new List<string>();
        private readonly Dictionary<Guid, IGH_DocumentObject> looseObjects = new Dictionary<Guid, IGH_DocumentObject>();

        internal void IncludeSatellites(Result result)
        {
            foreach (var id in result.Tops.Keys)
                if (looseObjects.TryGetValue(id, out var obj)) Objects[id] = obj;
        }

        internal static int EligibleCount(GH_Document document)
        {
            var membership = new GroupMembership.Index(document);
            return membership.Roots.Count(g => membership.FormatAllDiagnostic(g) == null);
        }

        internal static FormatAllCapture Capture(GH_Document document, Options options)
        {
            var capture = new FormatAllCapture();
            var targets = new List<FormatAllTarget>();
            var obstacles = new List<Box>();
            var membership = new GroupMembership.Index(document);
            capture.Membership = membership;
            var roots = membership.Roots;
            var grouped = new HashSet<Guid>(membership.Members.Values.SelectMany(ids => ids));
            foreach (var obj in document.Objects.Where(o => !(o is GH_Group) && !grouped.Contains(o.InstanceGuid)))
            {
                AddObstacle(obj);
                if (obj.Attributes != null && FormatAllSolver.Measurable(Bounds(obj))) capture.looseObjects[obj.InstanceGuid] = obj;
            }
            foreach (var root in roots)
            {
                var reason = membership.FormatAllDiagnostic(root);
                bool fixedRoot = reason != null;
                SelectionSnapshot inner = null;
                if (reason == null)
                {
                    try
                    {
                        inner = SelectionSnapshot.Capture(document, options, new[] { root }, membership);
                        reason = inner.Diagnostic;
                        capture.Log.AddRange(inner.Skipped);
                    }
                    catch (Exception error) { reason = error.Message; }
                }
                Box bounds;
                if (reason == null)
                {
                    bounds = Bounds(root);
                    foreach (var pair in inner.Objects) capture.Objects[pair.Key] = pair.Value;
                }
                else bounds = ObstacleGroup(root, fixedRoot);
                // Refresh read-only diagnostics after obstacle measurement so their
                // description matches the target's now-available bounds and member order.
                if (fixedRoot) reason = membership.FormatAllDiagnostic(root);
                var memberIds = membership.Descendants(root);
                var physical = memberIds.Where(membership.Objects.ContainsKey).Select(id => membership.Objects[id]).Where(o => !(o is GH_Group) && o.Attributes != null).ToArray();
                if (!fixedRoot) foreach (var obj in physical) capture.Objects[obj.InstanceGuid] = obj;
                targets.Add(new FormatAllTarget { Id = root.InstanceGuid, Label = SelectionSnapshot.DescribeGroup(document, root, membership),
                    Bounds = bounds, Hierarchy = inner?.Hierarchy, SkipReason = reason, Fixed = fixedRoot,
                    RigidNodeIds = physical.Where(SelectionSnapshot.IsNode).Select(o => o.InstanceGuid).ToArray(),
                    RigidMembers = physical.Select(o => new NonNodeMember { Id = o.InstanceGuid, Label = SelectionSnapshot.Label(o),
                        Bounds = Bounds(o), PivotOffset = new Vec2(o.Attributes.Pivot.X - o.Attributes.Bounds.X, o.Attributes.Pivot.Y - o.Attributes.Bounds.Y) }).ToArray(),
                    RigidGroupBoxes = memberIds.Concat(new[] { root.InstanceGuid }).Where(membership.Groups.ContainsKey).Select(id => membership.Groups[id]).Where(g => g.Attributes != null)
                        .ToDictionary(g => g.InstanceGuid, g => Bounds(g)) });
            }
            // Membership cycles can have no top-level root. Keep their visible objects fixed too.
            var reached = new HashSet<Guid>(roots.SelectMany(g => membership.Descendants(g)).Concat(roots.Select(g => g.InstanceGuid)));
            foreach (var group in document.Objects.OfType<GH_Group>().Where(g => !reached.Contains(g.InstanceGuid)))
            {
                var bounds = ObstacleGroup(group);
                targets.Add(new FormatAllTarget { Id = group.InstanceGuid, Label = SelectionSnapshot.DescribeGroup(document, group, membership), Bounds = bounds,
                    Fixed = true, SkipReason = membership.FormatAllDiagnostic(group) ?? "No top-level ownership root" });
            }
            capture.Snapshot = new FormatAllSnapshot { Targets = targets, Obstacles = obstacles, Options = options,
                Wires = BackwardWireCapture.Capture(document),
                LooseObjects = capture.looseObjects.Values.Select(obj => new NonNodeMember { Id = obj.InstanceGuid, Label = SelectionSnapshot.Label(obj),
                    Bounds = Bounds(obj), PivotOffset = new Vec2(obj.Attributes.Pivot.X - obj.Attributes.Bounds.X, obj.Attributes.Pivot.Y - obj.Attributes.Bounds.Y) }).ToArray() };
            return capture;

            Box Bounds(IGH_DocumentObject obj)
            {
                var b = obj.Attributes.Bounds;
                return new Box(b.X, b.Y, b.Width, b.Height);
            }
            void Measure(IGH_DocumentObject obj)
            {
                if (obj.Attributes == null) obj.CreateAttributes();
                if (obj.Attributes == null) throw new InvalidOperationException(obj is GH_Group
                    ? "no attributes" : "No attributes for '" + SelectionSnapshot.Label(obj) + "'");
                obj.Attributes.ExpireLayout(); obj.Attributes.PerformLayout();
            }
            void AddObstacle(IGH_DocumentObject obj)
            {
                try { Measure(obj); }
                catch (Exception error)
                {
                    capture.Log.Add(SelectionSnapshot.SkippedMessage(document, obj, "obstacle layout failed: " + error.Message, membership));
                    // A custom attribute can fail after previously supplying valid bounds.
                    if (obj.Attributes == null) return;
                }
                var b = Bounds(obj);
                if (FormatAllSolver.Measurable(b)) obstacles.Add(b);
                else capture.Log.Add(SelectionSnapshot.SkippedMessage(document, obj, "empty obstacle bounds after layout", membership));
            }
            Box ObstacleGroup(GH_Group group, bool fixedGroup = true)
            {
                var ids = membership.Descendants(group);
                var members = ids.Where(membership.Objects.ContainsKey).Select(id => membership.Objects[id]).Where(o => !(o is GH_Group)).ToArray();
                foreach (var obj in members)
                    if (fixedGroup) AddObstacle(obj);
                    else try { Measure(obj); }
                        catch (Exception error) { capture.Log.Add(SelectionSnapshot.SkippedMessage(document, obj, "rigid layout failed: " + error.Message, membership)); }
                // Never invoke recursive host group layout on a membership cycle.
                bool cycle = ids.Concat(new[] { group.InstanceGuid }).Where(membership.Groups.ContainsKey)
                    .Any(id => membership.AncestorIds(id).Contains(id));
                if (!cycle)
                {
                    var seen = new HashSet<Guid>();
                    void Layout(GH_Group g)
                    {
                        if (!seen.Add(g.InstanceGuid)) return;
                        foreach (var child in membership.Members[g.InstanceGuid].Where(membership.Groups.ContainsKey).Select(id => membership.Groups[id])) Layout(child);
                        try { Measure(g); }
                        catch (Exception error) { capture.Log.Add(SelectionSnapshot.SkippedMessage(document, g, "obstacle layout failed: " + error.Message, membership)); }
                    }
                    Layout(group);
                }
                if (group.Attributes != null)
                {
                    var b = Bounds(group);
                    if (FormatAllSolver.Measurable(b)) return b;
                }
                // Physical members above remain obstacles even for an unmeasurable cyclic outline.
                return new Box(0, 0, 0, 0);
            }
        }
    }
}
