using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal sealed class SelectionSnapshot
    {
        internal Snapshot Snapshot { get; private set; }
        internal HierarchySnapshot Hierarchy { get; private set; }
        internal bool ContainsGroups { get; private set; }
        internal string Diagnostic { get; private set; }
        internal readonly Dictionary<Guid, IGH_DocumentObject> Objects = new Dictionary<Guid, IGH_DocumentObject>();
        internal readonly List<Guid> CandidateIds = new List<Guid>();
        internal readonly List<string> Skipped = new List<string>();

        internal static bool IsNode(IGH_DocumentObject obj) => !(obj is HopperFormatComponent)
            && (obj is IGH_Component || obj is IGH_Param param && param.Kind == GH_ParamKind.floating);

        internal static string Label(IGH_DocumentObject obj) =>
            string.IsNullOrWhiteSpace(obj.NickName) ? obj.Name : obj.NickName;

        internal static string SkippedMessage(IGH_DocumentObject obj, string reason) =>
            $"Skipped {obj.GetType().Name} '{Label(obj)}': {reason}";

        private static void LayoutObject(IGH_DocumentObject obj)
        {
            if (obj.Attributes == null) obj.CreateAttributes();
            if (obj.Attributes == null) throw new InvalidOperationException($"No attributes for {Label(obj)}");
            obj.Attributes.ExpireLayout();
            obj.Attributes.PerformLayout();
        }

        private static IGH_Param[] Inputs(IGH_DocumentObject obj) => obj is IGH_Component component
            ? component.Params.Input.ToArray() : new[] { (IGH_Param)obj };
        private static IGH_Param[] Outputs(IGH_DocumentObject obj) => obj is IGH_Component component
            ? component.Params.Output.ToArray() : new[] { (IGH_Param)obj };
        private static Vec2 Relative(PointF point, RectangleF bounds) => new Vec2(point.X - bounds.X, point.Y - bounds.Y);

        internal static SelectionSnapshot Capture(GH_Document document, Options options) => Capture(document, options, document.SelectedObjects());

        internal static SelectionSnapshot Capture(GH_Document document, Options options, IEnumerable<IGH_DocumentObject> selection)
        {
            var capture = new SelectionSnapshot();
            var selected = selection.ToArray();
            var selectedGroups = selected.OfType<GH_Group>().ToArray();
            capture.ContainsGroups = selectedGroups.Length > 0;
            var byId = document.Objects.ToDictionary(o => o.InstanceGuid);
            var descendants = new HashSet<Guid>();
            void Expand(GH_Group group)
            {
                foreach (var id in group.ObjectIDs)
                    if (descendants.Add(id) && byId.TryGetValue(id, out var member) && member is GH_Group child) Expand(child);
            }
            foreach (var g in selectedGroups) Expand(g);
            var targets = selected.Concat(document.Objects.Where(o => descendants.Contains(o.InstanceGuid)))
                .GroupBy(o => o.InstanceGuid).Select(g => g.First()).ToArray();
            capture.CandidateIds.AddRange(targets.Where(o => IsNode(o) || o is GH_Group || descendants.Contains(o.InstanceGuid)).Select(o => o.InstanceGuid));
            capture.Diagnostic = GroupMembership.OwnershipDiagnostic(document, capture.CandidateIds);
            // Refuse group cycles before attempting Grasshopper's recursive group layout.
            if (capture.Diagnostic != null) return capture;
            var nodes = new List<Node>();
            var nonNodes = new Dictionary<Guid, NonNodeMember>();
            foreach (var obj in targets)
            {
                if (obj is GH_Group) continue;
                bool node = IsNode(obj);
                if (!node && !descendants.Contains(obj.InstanceGuid))
                {
                    capture.Skipped.Add(SkippedMessage(obj, "not a formattable node"));
                    continue;
                }
                LayoutObject(obj);
                var bounds = obj.Attributes.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    capture.Skipped.Add(SkippedMessage(obj, "empty bounds after layout"));
                    continue;
                }
                capture.Objects.Add(obj.InstanceGuid, obj);
                if (!node)
                {
                    nonNodes.Add(obj.InstanceGuid, new NonNodeMember { Id = obj.InstanceGuid, Label = Label(obj),
                        Bounds = new Box(bounds.X, bounds.Y, bounds.Width, bounds.Height), PivotOffset = Relative(obj.Attributes.Pivot, bounds) });
                    continue;
                }
                nodes.Add(new Node {
                    Id = obj.InstanceGuid, Label = Label(obj),
                    Kind = obj is IGH_Component ? NodeKind.Component :
                        obj is Grasshopper.Kernel.Special.GH_Panel ? NodeKind.Panel : NodeKind.Param,
                    CurrentTop = new Vec2(bounds.X, bounds.Y), PivotOffset = Relative(obj.Attributes.Pivot, bounds),
                    Width = bounds.Width, Height = bounds.Height,
                    Widget = obj is IGH_Param param && param.SourceCount == 0,
                    Inputs = Inputs(obj).Select(p => Relative(p.Attributes.InputGrip, bounds)).ToArray(),
                    Outputs = Outputs(obj).Select(p => Relative(p.Attributes.OutputGrip, bounds)).ToArray()
                });
            }

            var nodeIds = new HashSet<Guid>(nodes.Select(n => n.Id));
            var edges = new List<Edge>();
            var inbound = new List<ExternalInbound>();
            var outbound = new List<ExternalOutbound>();
            var laidOut = new HashSet<Guid>(capture.Objects.Keys);
            Dictionary<IGH_Param, IGH_Component> paramOwners = null;
            IGH_DocumentObject Owner(IGH_Param param)
            {
                // Connected external objects may not have been painted yet either.
                IGH_DocumentObject owner;
                if (param.Attributes == null && param.Kind != GH_ParamKind.floating)
                {
                    if (paramOwners == null)
                    {
                        paramOwners = new Dictionary<IGH_Param, IGH_Component>();
                        foreach (var component in document.Objects.OfType<IGH_Component>())
                            foreach (var port in component.Params.Input.Concat(component.Params.Output))
                                paramOwners[port] = component;
                    }
                    if (!paramOwners.TryGetValue(param, out var componentOwner))
                        throw new InvalidOperationException($"Cannot resolve owning component of param '{Label(param)}'");
                    owner = componentOwner;
                }
                else
                {
                    if (param.Attributes == null) param.CreateAttributes();
                    owner = param.Attributes.GetTopLevel.DocObject;
                }
                if (laidOut.Add(owner.InstanceGuid)) LayoutObject(owner);
                return owner;
            }
            foreach (var obj in capture.Objects.Values.Where(IsNode))
            {
                var inputs = Inputs(obj);
                for (int q = 0; q < inputs.Length; q++)
                    foreach (var source in inputs[q].Sources)
                    {
                        var owner = Owner(source);
                        if (nodeIds.Contains(owner.InstanceGuid))
                        {
                            int port = Array.IndexOf(Outputs(owner), source);
                            if (port < 0) throw new InvalidOperationException($"Cannot resolve output port of '{Label(owner)}'");
                            edges.Add(new Edge { From = owner.InstanceGuid, FromPort = port, To = obj.InstanceGuid, ToPort = q });
                        }
                        else inbound.Add(new ExternalInbound { Node = obj.InstanceGuid, Port = q, SourceGripY = source.Attributes.OutputGrip.Y });
                    }
                var outputs = Outputs(obj);
                for (int p = 0; p < outputs.Length; p++)
                    foreach (var recipient in outputs[p].Recipients)
                        if (!capture.Objects.ContainsKey(Owner(recipient).InstanceGuid))
                            outbound.Add(new ExternalOutbound { Node = obj.InstanceGuid, Port = p, TargetGripY = recipient.Attributes.InputGrip.Y });
            }
            capture.Snapshot = new Snapshot { Nodes = nodes, Edges = edges, Inbound = inbound, Outbound = outbound, Options = options };
            if (capture.ContainsGroups)
            {
                var selectedIds = new HashSet<Guid>(selectedGroups.Select(g => g.InstanceGuid));
                var roots = selectedGroups.Where(g => !GroupMembership.Ancestors(document, new[] { g.InstanceGuid })
                    .Any(a => selectedIds.Contains(a.InstanceGuid))).OrderBy(g => g.InstanceGuid).ToArray();
                var nodeById = nodes.ToDictionary(n => n.Id);
                var built = new Dictionary<Guid, HierarchyGroup>();
                IEnumerable<Guid> MemberIds(HierarchyGroup g) => g.Nodes.Select(n => n.Id).Concat(g.NonNodes.Select(n => n.Id))
                    .Concat(g.Children.SelectMany(MemberIds)).Distinct();
                Box Bounds(Guid id) => nodeById.TryGetValue(id, out var n) ? new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height) : nonNodes[id].Bounds;
                HierarchyGroup Build(GH_Group group)
                {
                    if (built.TryGetValue(group.InstanceGuid, out var previous)) return previous;
                    var childGroups = group.ObjectIDs.Where(byId.ContainsKey).Select(id => byId[id]).OfType<GH_Group>().ToArray();
                    var children = childGroups.Where(c => !childGroups.Any(a => a != c &&
                        GroupMembership.Ancestors(document, new[] { c.InstanceGuid }).Contains(a))).Select(Build).ToArray();
                    var childIds = new HashSet<Guid>(children.SelectMany(MemberIds));
                    var direct = group.ObjectIDs.Where(id => !childIds.Contains(id)).Distinct().ToArray();
                    var g = new HierarchyGroup { Id = group.InstanceGuid, Label = Label(group), Children = children,
                        Nodes = direct.Where(nodeById.ContainsKey).Select(id => nodeById[id]).ToArray(),
                        NonNodes = direct.Where(nonNodes.ContainsKey).Select(id => nonNodes[id]).ToArray(),
                        Locked = (group.NickName ?? string.Empty).IndexOf("[lock]", StringComparison.OrdinalIgnoreCase) >= 0 };
                    LayoutObject(group);
                    var envelope = g.Nodes.Select(n => Bounds(n.Id)).Concat(g.NonNodes.Select(n => n.Bounds))
                        .Concat(children.Select(c => byId[c.Id].Attributes.Bounds)
                            .Select(b => new Box(b.X, b.Y, b.Width, b.Height))).ToArray();
                    if (envelope.Length == 0) throw new InvalidOperationException($"Group '{g.Label}' has no measurable members");
                    var bounds = group.Attributes.Bounds;
                    g.Padding = new Padding(envelope.Min(b => b.X) - bounds.Left, envelope.Min(b => b.Y) - bounds.Top,
                        bounds.Right - envelope.Max(b => b.Right), bounds.Bottom - envelope.Max(b => b.Bottom));
                    built.Add(g.Id, g);
                    return g;
                }
                HierarchyGroup[] forest;
                try { forest = roots.Select(Build).ToArray(); }
                catch (InvalidOperationException error) { capture.Diagnostic = error.Message; return capture; }
                var covered = new HashSet<Guid>(forest.SelectMany(MemberIds));
                capture.Hierarchy = new HierarchySnapshot { Groups = forest, LooseNodes = nodes.Where(n => !covered.Contains(n.Id)).ToArray(),
                    Edges = edges, Inbound = inbound, Outbound = outbound, Options = options };
            }
            return capture;
        }
    }
}
