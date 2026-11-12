using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal static class BackwardWireCapture
    {
        // Measure before Apply, using final composed pivots for every action. Walking
        // input Sources once counts parallel wires separately without double counting
        // wires between two target objects (including different format-all roots).
        // Target membership is independent of movement, including successful no-ops.
        internal static BackwardWireCounts Count(GH_Document document, IReadOnlyList<Move> moves,
            IReadOnlyDictionary<Guid, IGH_DocumentObject> objects)
        {
            var dx = moves.ToDictionary(m => m.Id, m => m.Pivot.X - objects[m.Id].Attributes.Pivot.X);
            var owners = new Dictionary<IGH_Param, Guid>();
            var inputs = new List<IGH_Param>();
            foreach (var obj in document.Objects)
            {
                if (obj is IGH_Component component)
                {
                    foreach (var port in component.Params.Input.Concat(component.Params.Output)) owners[port] = obj.InstanceGuid;
                    inputs.AddRange(component.Params.Input);
                }
                else if (obj is IGH_Param param && param.Kind == GH_ParamKind.floating)
                { owners[param] = obj.InstanceGuid; inputs.Add(param); }
            }
            var wires = new List<WireX>();
            foreach (var input in inputs)
                foreach (var source in input.Sources)
                {
                    if (!owners.TryGetValue(source, out var from) || !owners.TryGetValue(input, out var to) ||
                        !objects.ContainsKey(from) && !objects.ContainsKey(to)) continue;
                    wires.Add(new WireX(from, source.Attributes?.OutputGrip.X ?? double.NaN,
                        to, input.Attributes?.InputGrip.X ?? double.NaN));
                }
            return BackwardWires.Count(wires, dx);
        }
    }
}
