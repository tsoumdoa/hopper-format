using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    internal static class Ranking
    {
        internal static List<List<Node>> Assign(Graph graph, out Dictionary<Guid, int> ranks, bool recoverColumns = false)
        {
            var assigned = new Dictionary<Guid, int>();
            ranks = assigned;
            var islands = new List<List<Node>>();
            var seen = new HashSet<Guid>();
            var orphans = new List<Node>();
            var externalInputs = new HashSet<Guid>(graph.Snapshot.Inbound.Select(e => e.Node));
            foreach (var node in graph.Nodes)
            {
                if (graph.In[node.Id].Count + graph.Out[node.Id].Count == 0)
                {
                    orphans.Add(node);
                    continue;
                }
                if (!seen.Add(node.Id)) continue;
                var members = new List<Node>();
                var queue = new Queue<Node>();
                queue.Enqueue(node);
                while (queue.Count > 0)
                {
                    var next = queue.Dequeue();
                    members.Add(next);
                    foreach (var edge in graph.In[next.Id])
                        if (seen.Add(edge.From)) queue.Enqueue(graph.ById[edge.From]);
                    foreach (var edge in graph.Out[next.Id])
                        if (seen.Add(edge.To)) queue.Enqueue(graph.ById[edge.To]);
                }
                members.Sort(Graph.CompareNodes);
                islands.Add(members);
                var ids = new HashSet<Guid>(members.Select(n => n.Id));
                var order = graph.Topological.Where(n => ids.Contains(n.Id)).ToArray();
                foreach (var next in order)
                {
                    int rank = 0;
                    foreach (var edge in graph.In[next.Id]) rank = Math.Max(rank, assigned[edge.From] + 1);
                    assigned[next.Id] = rank;
                }
                int maximum = members.Max(n => assigned[n.Id]);
                var pinned = new HashSet<Guid>();
                foreach (var next in order)
                    if (graph.Options.ReturnConstraint && graph.Out[next.Id].Count == 0 &&
                        (graph.Returns.Contains(next.Id) || next.Kind == NodeKind.Param && graph.In[next.Id].Count > 0))
                    {
                        assigned[next.Id] = maximum;
                        pinned.Add(next.Id);
                    }
                for (int i = order.Length - 1; i >= 0; i--)
                {
                    var next = order[i];
                    bool inputParam = graph.Options.ReturnConstraint && next.Kind == NodeKind.Param &&
                        graph.In[next.Id].Count == 0 && externalInputs.Contains(next.Id);
                    if (graph.Out[next.Id].Count == 0 || graph.Returns.Contains(next.Id) ||
                        pinned.Contains(next.Id) || inputParam) continue;
                    assigned[next.Id] = graph.Out[next.Id].Min(e => assigned[e.To]) - 1;
                }
            }
            if (islands.Count == 0)
            {
                foreach (var node in orphans) assigned[node.Id] = 0;
                if (orphans.Count > 0) islands.Add(orphans);
                return islands;
            }
            islands.Sort((a, b) => {
                int c = b.Count.CompareTo(a.Count);
                return c != 0 ? c : Graph.CompareNodes(a[0], b[0]);
            });
            var main = islands[0];
            foreach (var orphan in orphans)
            {
                var nearest = main.OrderBy(n => Math.Abs(n.CurrentTop.X - orphan.CurrentTop.X))
                    .ThenBy(n => n.CurrentTop.Y).ThenBy(n => n.CurrentTop.X).ThenBy(n => n.Id).First();
                if (recoverColumns)
                {
                    // A wide orphan can be farther from its column's real nodes on
                    // the left after right alignment. Recover a formatted column
                    // by its common right edge; the caller must verify the complete
                    // rounded geometry before accepting these recovered ranks.
                    var aligned = main.Where(n => Math.Abs(n.CurrentTop.X + n.Width - orphan.CurrentTop.X - orphan.Width) < 1)
                        .OrderBy(n => Math.Abs(n.CurrentTop.X + n.Width - orphan.CurrentTop.X - orphan.Width))
                        .ThenBy(n => n.CurrentTop.Y).ThenBy(n => n.CurrentTop.X).ThenBy(n => n.Id).FirstOrDefault();
                    if (aligned != null) nearest = aligned;
                }
                assigned[orphan.Id] = assigned[nearest.Id];
            }
            // Orphans do not influence the main-island choice or one another's ranks.
            main.AddRange(orphans);
            main.Sort(Graph.CompareNodes);
            var rest = islands.Skip(1).ToList();
            rest.Sort((a, b) => Graph.CompareNodes(a[0], b[0]));
            return new[] { main }.Concat(rest).ToList();
        }
    }
}
