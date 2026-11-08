using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace HopperFormat
{
    internal static class GroupMembership
    {
        // One read-only membership graph per capture/preflight. Geometry may change
        // during capture, so descriptions are deliberately not cached here.
        internal sealed class Index
        {
            internal readonly GH_Document Document;
            internal readonly Dictionary<Guid, IGH_DocumentObject> Objects;
            internal readonly Dictionary<Guid, int> ObjectOrder;
            internal readonly Dictionary<Guid, GH_Group> Groups;
            internal readonly Dictionary<Guid, Guid[]> Members;
            internal readonly GH_Group[] Roots;
            private readonly Dictionary<Guid, List<Guid>> parents = new Dictionary<Guid, List<Guid>>();
            private readonly Dictionary<Guid, HashSet<Guid>> descendants = new Dictionary<Guid, HashSet<Guid>>();
            private readonly Dictionary<Guid, HashSet<Guid>> ancestors = new Dictionary<Guid, HashSet<Guid>>();
            private readonly Dictionary<Guid, bool> labels = new Dictionary<Guid, bool>();
            private readonly Dictionary<Guid, bool> conflicts = new Dictionary<Guid, bool>();

            internal Index(GH_Document document)
            {
                Document = document;
                Objects = document.Objects.ToDictionary(o => o.InstanceGuid);
                ObjectOrder = document.Objects.Select((o, i) => (o.InstanceGuid, i)).ToDictionary(p => p.InstanceGuid, p => p.i);
                Groups = document.Objects.OfType<GH_Group>().ToDictionary(g => g.InstanceGuid);
                Members = Groups.Values.ToDictionary(g => g.InstanceGuid, g => g.ObjectIDs.ToArray());
                foreach (var group in Groups.Values)
                    foreach (var id in Members[group.InstanceGuid])
                    {
                        if (!parents.TryGetValue(id, out var list)) parents[id] = list = new List<Guid>();
                        list.Add(group.InstanceGuid);
                    }
                Roots = Groups.Values.Where(g => !parents.ContainsKey(g.InstanceGuid)).OrderBy(g => g.InstanceGuid).ToArray();
            }

            internal HashSet<Guid> Descendants(GH_Group root)
            {
                if (descendants.TryGetValue(root.InstanceGuid, out var cached)) return cached;
                var seen = new HashSet<Guid>();
                var queue = new Queue<Guid>(Members.TryGetValue(root.InstanceGuid, out var direct) ? direct : root.ObjectIDs.ToArray());
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    if (seen.Add(id) && Members.TryGetValue(id, out var children))
                        foreach (var child in children) queue.Enqueue(child);
                }
                return descendants[root.InstanceGuid] = seen;
            }

            internal HashSet<Guid> AncestorIds(Guid id)
            {
                if (ancestors.TryGetValue(id, out var cached)) return cached;
                var seen = new HashSet<Guid>();
                var queue = new Queue<Guid>(); queue.Enqueue(id);
                while (queue.Count > 0)
                    if (parents.TryGetValue(queue.Dequeue(), out var owners))
                        foreach (var owner in owners) if (seen.Add(owner)) queue.Enqueue(owner);
                return ancestors[id] = seen;
            }

            internal IReadOnlyList<GH_Group> Ancestors(IEnumerable<Guid> ids) => ids.SelectMany(AncestorIds)
                .Distinct().OrderBy(id => id).Select(id => Groups[id]).ToArray();

            private bool LabelGroup(Guid id)
            {
                if (!labels.TryGetValue(id, out var label))
                    labels[id] = label = Descendants(Groups[id]).Count(member => Objects.TryGetValue(member, out var obj) && SelectionSnapshot.IsNode(obj)) == 1;
                return label;
            }

            private bool ConflictingOwners(Guid id)
            {
                if (conflicts.TryGetValue(id, out var conflict)) return conflict;
                // Non-label owners must form a single ancestry chain. Checking
                // adjacent owners in depth order avoids comparing every pair.
                var owners = AncestorIds(id).Where(owner => !LabelGroup(owner))
                    .OrderBy(owner => AncestorIds(owner).Count).ThenBy(owner => owner).ToArray();
                for (int i = 1; i < owners.Length; i++)
                    if (!AncestorIds(owners[i]).Contains(owners[i - 1])) return conflicts[id] = true;
                return conflicts[id] = false;
            }

            internal string OwnershipDiagnostic(IEnumerable<Guid> ids)
            {
                var targets = new HashSet<Guid>(ids);
                var relevant = Ancestors(targets).Concat(targets.Where(Groups.ContainsKey).Select(id => Groups[id]))
                    .Distinct().OrderBy(g => g.InstanceGuid);
                foreach (var group in relevant)
                    if (AncestorIds(group.InstanceGuid).Contains(group.InstanceGuid))
                        return $"{SelectionSnapshot.DescribeGroup(Document, group, this)}: group membership cycle";
                foreach (var obj in targets.Where(Objects.ContainsKey).Select(id => Objects[id])
                    .OrderBy(o => SelectionSnapshot.IsNode(o) ? 0 : o is GH_Group ? 2 : 1).ThenBy(o => o.InstanceGuid))
                    if (ConflictingOwners(obj.InstanceGuid))
                        return obj is GH_Group group
                            ? $"{SelectionSnapshot.DescribeGroup(Document, group, this)}: Overlapping membership"
                            : $"Overlapping membership for '{SelectionSnapshot.Label(obj)}'";
                return null;
            }

            internal string LockedDiagnostic(IEnumerable<Guid> ids)
            {
                var selected = new HashSet<Guid>(ids);
                var locked = Ancestors(selected).Concat(selected.Where(Groups.ContainsKey).Select(id => Groups[id]).OrderBy(g => ObjectOrder[g.InstanceGuid]))
                    .FirstOrDefault(group => (group.NickName ?? string.Empty).IndexOf("[lock]", StringComparison.OrdinalIgnoreCase) >= 0);
                return locked == null ? null : $"{SelectionSnapshot.DescribeGroup(Document, locked, this)}: Selection belongs to locked group";
            }

            internal string FormatAllDiagnostic(GH_Group root)
            {
                var ids = Descendants(root).Concat(new[] { root.InstanceGuid }).ToArray();
                return OwnershipDiagnostic(ids) ?? LockedDiagnostic(ids);
            }
        }

        internal static GH_Group[] TopLevelGroups(GH_Document document)
        {
            var groups = document.Objects.OfType<GH_Group>().ToArray();
            var contained = new HashSet<Guid>(groups.SelectMany(g => g.ObjectIDs));
            return groups.Where(g => !contained.Contains(g.InstanceGuid)).OrderBy(g => g.InstanceGuid).ToArray();
        }
        internal static HashSet<Guid> Descendants(GH_Document document, GH_Group root) => new Index(document).Descendants(root);
        internal static IReadOnlyList<GH_Group> Ancestors(GH_Document document, IEnumerable<Guid> ids) => new Index(document).Ancestors(ids);
        internal static string OwnershipDiagnostic(GH_Document document, IEnumerable<Guid> ids) => new Index(document).OwnershipDiagnostic(ids);
        internal static string LockedDiagnostic(GH_Document document, IEnumerable<Guid> ids) => new Index(document).LockedDiagnostic(ids);
        internal static string FormatAllDiagnostic(GH_Document document, GH_Group root) => new Index(document).FormatAllDiagnostic(root);
    }
}
