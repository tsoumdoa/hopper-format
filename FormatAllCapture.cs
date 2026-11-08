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
        internal readonly Dictionary<Guid, IGH_DocumentObject> Objects = new Dictionary<Guid, IGH_DocumentObject>();
        internal readonly List<string> Log = new List<string>();

        internal static int EligibleCount(GH_Document document) => GroupMembership.TopLevelGroups(document)
            .Count(g => GroupMembership.FormatAllDiagnostic(document, g) == null);

        internal static FormatAllCapture Capture(GH_Document document, Options options)
        {
            var capture = new FormatAllCapture();
            var targets = new List<FormatAllTarget>();
            var obstacles = new List<Box>();
            var roots = GroupMembership.TopLevelGroups(document);
            var grouped = new HashSet<Guid>(document.Objects.OfType<GH_Group>().SelectMany(g => g.ObjectIDs));
            foreach (var obj in document.Objects.Where(o => !(o is GH_Group) && !grouped.Contains(o.InstanceGuid)))
                AddObstacle(obj);
            foreach (var root in roots)
            {
                var reason = GroupMembership.FormatAllDiagnostic(document, root);
                SelectionSnapshot inner = null;
                if (reason == null)
                {
                    try
                    {
                        inner = SelectionSnapshot.Capture(document, options, new[] { root });
                        reason = inner.Diagnostic;
                        // A partially measured group cannot safely be formatted as a smaller group.
                        if (reason == null && inner.Skipped.Count > 0) reason = string.Join("; ", inner.Skipped);
                    }
                    catch (Exception error) { reason = error.Message; }
                }
                Box bounds;
                if (reason == null)
                {
                    bounds = Bounds(root);
                    foreach (var pair in inner.Objects) capture.Objects.Add(pair.Key, pair.Value);
                }
                else bounds = ObstacleGroup(root);
                targets.Add(new FormatAllTarget { Id = root.InstanceGuid, Label = SelectionSnapshot.Label(root),
                    Bounds = bounds, Hierarchy = inner?.Hierarchy, SkipReason = reason });
            }
            // Membership cycles can have no top-level root. Keep their visible objects fixed too.
            var reached = new HashSet<Guid>(roots.SelectMany(g => GroupMembership.Descendants(document, g)).Concat(roots.Select(g => g.InstanceGuid)));
            foreach (var group in document.Objects.OfType<GH_Group>().Where(g => !reached.Contains(g.InstanceGuid)))
            {
                var bounds = ObstacleGroup(group);
                targets.Add(new FormatAllTarget { Id = group.InstanceGuid, Label = SelectionSnapshot.Label(group), Bounds = bounds,
                    SkipReason = GroupMembership.FormatAllDiagnostic(document, group) ?? "No top-level ownership root" });
            }
            capture.Snapshot = new FormatAllSnapshot { Targets = targets, Obstacles = obstacles, Options = options };
            return capture;

            Box Bounds(IGH_DocumentObject obj)
            {
                var b = obj.Attributes.Bounds;
                return new Box(b.X, b.Y, b.Width, b.Height);
            }
            void Measure(IGH_DocumentObject obj)
            {
                if (obj.Attributes == null) obj.CreateAttributes();
                if (obj.Attributes == null) throw new InvalidOperationException("No attributes for '" + SelectionSnapshot.Label(obj) + "'");
                obj.Attributes.ExpireLayout(); obj.Attributes.PerformLayout();
            }
            void AddObstacle(IGH_DocumentObject obj)
            {
                try { Measure(obj); }
                catch (Exception error)
                {
                    capture.Log.Add(SelectionSnapshot.SkippedMessage(obj, "obstacle layout failed: " + error.Message));
                    // A custom attribute can fail after previously supplying valid bounds.
                    if (obj.Attributes == null) return;
                }
                var b = Bounds(obj);
                if (b.Width > 0 && b.Height > 0) obstacles.Add(b);
                else capture.Log.Add(SelectionSnapshot.SkippedMessage(obj, "empty obstacle bounds after layout"));
            }
            Box ObstacleGroup(GH_Group group)
            {
                var ids = GroupMembership.Descendants(document, group);
                var members = document.Objects.Where(o => ids.Contains(o.InstanceGuid) && !(o is GH_Group)).ToArray();
                foreach (var obj in members) AddObstacle(obj);
                // Never invoke recursive host group layout on a membership cycle.
                bool cycle = document.Objects.OfType<GH_Group>().Where(g => ids.Contains(g.InstanceGuid) || g == group)
                    .Any(g => GroupMembership.Ancestors(document, new[] { g.InstanceGuid }).Contains(g));
                if (!cycle)
                {
                    var seen = new HashSet<Guid>();
                    void Layout(GH_Group g)
                    {
                        if (!seen.Add(g.InstanceGuid)) return;
                        foreach (var child in document.Objects.OfType<GH_Group>().Where(c => g.ObjectIDs.Contains(c.InstanceGuid))) Layout(child);
                        try { Measure(g); }
                        catch (Exception error) { capture.Log.Add(SelectionSnapshot.SkippedMessage(g, "obstacle layout failed: " + error.Message)); }
                    }
                    Layout(group);
                }
                if (group.Attributes != null)
                {
                    var b = Bounds(group);
                    if (b.Width > 0 && b.Height > 0) return b;
                }
                // Physical members above remain obstacles even for an unmeasurable cyclic outline.
                return new Box(0, 0, 0, 0);
            }
        }
    }
}
