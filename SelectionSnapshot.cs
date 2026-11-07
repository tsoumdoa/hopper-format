using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal sealed class SelectionSnapshot
    {
        internal Snapshot Snapshot { get; private set; }
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

        internal static SelectionSnapshot Capture(GH_Document document, Options options)
        {
            var capture = new SelectionSnapshot();
            var nodes = new List<Node>();
            foreach (var obj in document.SelectedObjects())
            {
                if (!IsNode(obj))
                {
                    capture.Skipped.Add(SkippedMessage(obj, "not a formattable node"));
                    continue;
                }
                capture.CandidateIds.Add(obj.InstanceGuid);
                LayoutObject(obj);
                var bounds = obj.Attributes.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    capture.Skipped.Add(SkippedMessage(obj, "empty bounds after layout"));
                    continue;
                }
                capture.Objects.Add(obj.InstanceGuid, obj);
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
            foreach (var obj in capture.Objects.Values)
            {
                var inputs = Inputs(obj);
                for (int q = 0; q < inputs.Length; q++)
                    foreach (var source in inputs[q].Sources)
                    {
                        var owner = Owner(source);
                        if (capture.Objects.ContainsKey(owner.InstanceGuid))
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
            return capture;
        }
    }
}
