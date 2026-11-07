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

        internal static string LockedDiagnostic(GH_Document document, IEnumerable<Guid> ids)
        {
            var locked = Ancestors(document, ids).FirstOrDefault(group =>
                (group.NickName ?? string.Empty).IndexOf("[lock]", StringComparison.OrdinalIgnoreCase) >= 0);
            return locked == null ? null : $"Selection belongs to locked group '{locked.NickName}'";
        }
    }
}
