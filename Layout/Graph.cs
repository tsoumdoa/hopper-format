using System;
using System.Collections.Generic;
using System.Linq;

namespace HopperFormat.Layout
{
    // Dictionaries and sets are lookup tables only; traversal uses sorted lists.
    internal sealed class Graph
    {
        internal Node[] Nodes;
        internal Edge[] Edges;
        internal Snapshot Snapshot;
        internal Options Options;
        internal Dictionary<Guid, Node> ById;
        internal Dictionary<Guid, List<Edge>> In, Out;
        internal HashSet<Guid> Returns;
        internal List<Node> Topological;

        internal static int CompareNodes(Node a, Node b)
        {
            int c = a.CurrentTop.Y.CompareTo(b.CurrentTop.Y);
            if (c == 0) c = a.CurrentTop.X.CompareTo(b.CurrentTop.X);
            return c == 0 ? a.Id.CompareTo(b.Id) : c;
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Finite(Vec2 value) { return Finite(value.X) && Finite(value.Y); }

        internal static bool TryCreate(Snapshot snapshot, out Graph graph, out string diagnostic)
        {
            graph = null;
            diagnostic = string.Empty;
            if (snapshot == null || snapshot.Nodes == null || snapshot.Edges == null ||
                snapshot.Inbound == null || snapshot.Outbound == null || snapshot.Options == null)
                return Fail("Snapshot collections and options must be present.", out diagnostic);
            if (snapshot.Nodes.Any(n => n == null) || snapshot.Edges.Any(e => e == null) ||
                snapshot.Inbound.Any(e => e == null) || snapshot.Outbound.Any(e => e == null))
                return Fail("Snapshot contains a null entry.", out diagnostic);
            var nodes = snapshot.Nodes.OrderBy(n => n.CurrentTop.Y).ThenBy(n => n.CurrentTop.X).ThenBy(n => n.Id).ToArray();
            if (nodes.Select(n => n.Id).Distinct().Count() != nodes.Length)
                return Fail("Duplicate node identifiers.", out diagnostic);
            var options = snapshot.Options;
            if (!Finite(options.GapX) || !Finite(options.GapY) || !Finite(options.StackGap) ||
                options.GapX < 0 || options.GapY < 0 || options.StackGap < 0)
                return Fail("Gaps must be finite and non-negative.", out diagnostic);
            foreach (var node in nodes)
            {
                if (!Finite(node.Width) || !Finite(node.Height) || node.Width <= 0 || node.Height <= 0)
                    return Fail("Non-positive or non-finite size for '" + node.Label + "'.", out diagnostic);
                if (!Finite(node.CurrentTop) || !Finite(node.PivotOffset) || node.Inputs == null || node.Outputs == null ||
                    node.Inputs.Any(p => !Finite(p)) || node.Outputs.Any(p => !Finite(p)))
                    return Fail("Invalid coordinates or grips for '" + node.Label + "'.", out diagnostic);
            }
            var byId = nodes.ToDictionary(n => n.Id);
            var edges = snapshot.Edges.OrderBy(e => e.From).ThenBy(e => e.FromPort)
                .ThenBy(e => e.To).ThenBy(e => e.ToPort).ToArray();
            foreach (var edge in edges)
            {
                if (!byId.ContainsKey(edge.From) || !byId.ContainsKey(edge.To))
                    return Fail("An internal edge refers to a missing node.", out diagnostic);
                if (edge.FromPort < 0 || edge.FromPort >= byId[edge.From].Outputs.Length ||
                    edge.ToPort < 0 || edge.ToPort >= byId[edge.To].Inputs.Length)
                    return Fail("Bad port index between '" + byId[edge.From].Label + "' and '" + byId[edge.To].Label + "'.", out diagnostic);
            }
            foreach (var external in snapshot.Inbound.OrderBy(e => e.Node).ThenBy(e => e.Port).ThenBy(e => e.SourceGripY))
            {
                if (!byId.ContainsKey(external.Node)) return Fail("An external inbound refers to a missing node.", out diagnostic);
                if (external.Port < 0 || external.Port >= byId[external.Node].Inputs.Length || !Finite(external.SourceGripY))
                    return Fail("Bad external input port or grip for '" + byId[external.Node].Label + "'.", out diagnostic);
            }
            foreach (var external in snapshot.Outbound.OrderBy(e => e.Node).ThenBy(e => e.Port).ThenBy(e => e.TargetGripY))
            {
                if (!byId.ContainsKey(external.Node)) return Fail("An external outbound refers to a missing node.", out diagnostic);
                if (external.Port < 0 || external.Port >= byId[external.Node].Outputs.Length || !Finite(external.TargetGripY))
                    return Fail("Bad external output port or grip for '" + byId[external.Node].Label + "'.", out diagnostic);
            }
            var g = new Graph { Nodes = nodes, Edges = edges, Snapshot = snapshot, Options = options, ById = byId,
                In = nodes.ToDictionary(n => n.Id, n => new List<Edge>()),
                Out = nodes.ToDictionary(n => n.Id, n => new List<Edge>()),
                Returns = new HashSet<Guid>(snapshot.Outbound.Select(e => e.Node)), Topological = new List<Node>() };
            foreach (var edge in edges) { g.In[edge.To].Add(edge); g.Out[edge.From].Add(edge); }
            // Iterative DFS reports an actual back edge, including self cycles.
            var colors = nodes.ToDictionary(n => n.Id, n => 0);
            foreach (var root in nodes)
            {
                if (colors[root.Id] != 0) continue;
                var stack = new Stack<KeyValuePair<Guid, int>>();
                colors[root.Id] = 1;
                stack.Push(new KeyValuePair<Guid, int>(root.Id, 0));
                while (stack.Count > 0)
                {
                    var frame = stack.Pop();
                    if (frame.Value == g.Out[frame.Key].Count) { colors[frame.Key] = 2; continue; }
                    var edge = g.Out[frame.Key][frame.Value];
                    stack.Push(new KeyValuePair<Guid, int>(frame.Key, frame.Value + 1));
                    if (colors[edge.To] == 1)
                        return Fail("Cycle between '" + byId[edge.From].Label + "' and '" + byId[edge.To].Label + "'.", out diagnostic);
                    if (colors[edge.To] == 0)
                    {
                        colors[edge.To] = 1;
                        stack.Push(new KeyValuePair<Guid, int>(edge.To, 0));
                    }
                }
            }
            var remaining = nodes.ToDictionary(n => n.Id, n => g.In[n.Id].Count);
            var indices = nodes.Select((node, index) => new { node.Id, Index = index })
                .ToDictionary(item => item.Id, item => item.Index);
            var ready = new SortedSet<int>(Enumerable.Range(0, nodes.Length).Where(i => remaining[nodes[i].Id] == 0));
            while (ready.Count > 0)
            {
                int index = ready.Min;
                ready.Remove(index);
                var next = nodes[index];
                g.Topological.Add(next);
                foreach (var edge in g.Out[next.Id])
                    if (--remaining[edge.To] == 0) ready.Add(indices[edge.To]);
            }
            graph = g;
            return true;
        }

        private static bool Fail(string message, out string diagnostic) { diagnostic = message; return false; }
    }
}
