using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace HopperFormat
{
    internal static class GroupMembership
    {
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
