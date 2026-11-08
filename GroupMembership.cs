using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace HopperFormat
{
    internal static class GroupMembership
    {
        internal static GH_Group[] TopLevelGroups(GH_Document document)
        {
            var contained = new HashSet<Guid>(document.Objects.OfType<GH_Group>().SelectMany(g => g.ObjectIDs));
            return document.Objects.OfType<GH_Group>().Where(g => !contained.Contains(g.InstanceGuid)).OrderBy(g => g.InstanceGuid).ToArray();
        }

        internal static HashSet<Guid> Descendants(GH_Document document, GH_Group root)
        {
            var groups = document.Objects.OfType<GH_Group>().ToDictionary(g => g.InstanceGuid);
            var seen = new HashSet<Guid>();
            void Visit(GH_Group group)
            {
                foreach (var id in group.ObjectIDs)
                    if (seen.Add(id) && groups.TryGetValue(id, out var child)) Visit(child);
            }
            Visit(root);
            return seen;
        }

        // Read-only preflight: no attributes, layouts, outputs or selection writes.
        internal static string FormatAllDiagnostic(GH_Document document, GH_Group root)
        {
            var ids = Descendants(document, root); ids.Add(root.InstanceGuid);
            var diagnostic = OwnershipDiagnostic(document, ids) ?? LockedDiagnostic(document, ids);
            if (diagnostic != null) return diagnostic;
            var objects = document.Objects.ToDictionary(o => o.InstanceGuid);
            var nodes = document.Objects.Where(SelectionSnapshot.IsNode).ToArray();
            var outputOwners = new Dictionary<IGH_Param, Guid>();
            foreach (var node in nodes)
                foreach (var port in node is IGH_Component c ? c.Params.Output.ToArray() : new[] { (IGH_Param)node })
                    outputOwners[port] = node.InstanceGuid;
            var edges = new List<(Guid From, Guid To)>();
            foreach (var node in nodes.Where(n => ids.Contains(n.InstanceGuid)))
                foreach (var port in node is IGH_Component c ? c.Params.Input.ToArray() : new[] { (IGH_Param)node })
                    foreach (var source in port.Sources)
                        if (outputOwners.TryGetValue(source, out var from) && ids.Contains(from)) edges.Add((from, node.InstanceGuid));
            bool Cycle(Dictionary<Guid, Guid> owner, bool contracted)
            {
                var units = owner.Values.Distinct().ToArray();
                var links = edges.Where(e => owner.ContainsKey(e.From) && owner.ContainsKey(e.To))
                    .Select(e => (From: owner[e.From], To: owner[e.To])).Where(e => !contracted || e.From != e.To).Distinct().ToArray();
                var incoming = units.ToDictionary(id => id, id => links.Count(e => e.To == id));
                var ready = new Queue<Guid>(units.Where(id => incoming[id] == 0)); int count = 0;
                while (ready.Count > 0)
                {
                    var id = ready.Dequeue(); count++;
                    foreach (var e in links.Where(e => e.From == id)) if (--incoming[e.To] == 0) ready.Enqueue(e.To);
                }
                return count != units.Length;
            }
            if (Cycle(nodes.Where(n => ids.Contains(n.InstanceGuid)).ToDictionary(n => n.InstanceGuid, n => n.InstanceGuid), false))
                return "Dataflow graph contains a cycle";
            foreach (var group in document.Objects.OfType<GH_Group>().Where(g => ids.Contains(g.InstanceGuid)))
            {
                var children = group.ObjectIDs.Where(objects.ContainsKey).Select(id => objects[id]).OfType<GH_Group>().ToArray();
                children = children.Where(c => !children.Any(a => a != c && Ancestors(document, new[] { c.InstanceGuid }).Contains(a))).ToArray();
                var owner = new Dictionary<Guid, Guid>();
                foreach (var child in children)
                {
                    var members = Descendants(document, child).Where(id => objects.ContainsKey(id) && SelectionSnapshot.IsNode(objects[id])).ToArray();
                    foreach (var id in members) owner[id] = members.Length == 1 ? id : child.InstanceGuid;
                }
                foreach (var id in group.ObjectIDs.Where(id => objects.ContainsKey(id) && SelectionSnapshot.IsNode(objects[id])))
                    if (!owner.ContainsKey(id)) owner[id] = id;
                if (Cycle(owner, true)) return "Group '" + SelectionSnapshot.Label(group) + "': contracted graph contains a cycle";
            }
            return null;
        }

        // Reverse reachability handles overlapping groups and cycles without
        // choosing a single parent or expanding the selection.
        internal static IReadOnlyList<GH_Group> Ancestors(GH_Document document, IEnumerable<Guid> ids)
        {
            var parents = new Dictionary<Guid, List<GH_Group>>();
            foreach (var group in document.Objects.OfType<GH_Group>())
                foreach (var id in group.ObjectIDs)
                {
                    if (!parents.TryGetValue(id, out var list)) parents[id] = list = new List<GH_Group>();
                    list.Add(group);
                }
            var seen = new HashSet<Guid>(ids);
            var queue = new Queue<Guid>(seen);
            var result = new Dictionary<Guid, GH_Group>();
            while (queue.Count > 0)
            {
                if (!parents.TryGetValue(queue.Dequeue(), out var groups)) continue;
                foreach (var group in groups)
                {
                    result[group.InstanceGuid] = group;
                    if (seen.Add(group.InstanceGuid)) queue.Enqueue(group.InstanceGuid);
                }
            }
            return result.Values.OrderBy(group => group.InstanceGuid).ToArray();
        }

        internal static string OwnershipDiagnostic(GH_Document document, IEnumerable<Guid> ids)
        {
            var targets = new HashSet<Guid>(ids);
            var relevant = Ancestors(document, targets).Concat(document.Objects.OfType<GH_Group>()
                .Where(g => targets.Contains(g.InstanceGuid))).Distinct().OrderBy(g => g.InstanceGuid).ToArray();
            var ancestors = relevant.ToDictionary(g => g.InstanceGuid,
                g => new HashSet<Guid>(Ancestors(document, new[] { g.InstanceGuid }).Select(a => a.InstanceGuid)));
            foreach (var group in relevant)
                if (ancestors[group.InstanceGuid].Contains(group.InstanceGuid))
                    return $"Group membership cycle at '{SelectionSnapshot.Label(group)}'";
            foreach (var obj in document.Objects.Where(o => targets.Contains(o.InstanceGuid))
                .OrderBy(o => SelectionSnapshot.IsNode(o) ? 0 : o is GH_Group ? 2 : 1).ThenBy(o => o.InstanceGuid))
            {
                var owners = Ancestors(document, new[] { obj.InstanceGuid });
                for (int i = 0; i < owners.Count; i++)
                    for (int j = i + 1; j < owners.Count; j++)
                        if (!ancestors[owners[i].InstanceGuid].Contains(owners[j].InstanceGuid) &&
                            !ancestors[owners[j].InstanceGuid].Contains(owners[i].InstanceGuid))
                            return $"Overlapping membership for '{SelectionSnapshot.Label(obj)}'";
            }
            return null;
        }

        internal static string LockedDiagnostic(GH_Document document, IEnumerable<Guid> ids)
        {
            var selected = new HashSet<Guid>(ids);
            var locked = Ancestors(document, selected).Concat(document.Objects.OfType<GH_Group>()
                .Where(group => selected.Contains(group.InstanceGuid))).FirstOrDefault(group =>
                (group.NickName ?? string.Empty).IndexOf("[lock]", StringComparison.OrdinalIgnoreCase) >= 0);
            return locked == null ? null : $"Selection belongs to locked group '{locked.NickName}'";
        }
    }
}
