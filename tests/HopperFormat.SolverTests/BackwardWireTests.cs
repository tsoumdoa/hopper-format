using System;
using System.Collections.Generic;
using System.Linq;
using HopperFormat.Layout;

internal static partial class Program
{
    private static BackwardWireCounts BoundaryCounts(Snapshot s, Result r)
    {
        var nodes = s.Nodes.ToDictionary(n => n.Id);
        var wires = s.Inbound.Where(e => Graph.Finite(e.SourceGripX)).Select(e => new WireX(e.SourceObjectId,
            e.SourceGripX.Value, e.Node, nodes[e.Node].CurrentTop.X + nodes[e.Node].Inputs[e.Port].X))
            .Concat(s.Outbound.Where(e => Graph.Finite(e.TargetGripX)).Select(e => new WireX(e.Node,
                nodes[e.Node].CurrentTop.X + nodes[e.Node].Outputs[e.Port].X, e.TargetObjectId, e.TargetGripX.Value)));
        return BackwardWires.Count(wires, nodes.Values.ToDictionary(n => n.Id, n => r.Tops[n.Id].X - n.CurrentTop.X));
    }

    // Clearance is a second protection class; backward counts retain their strict definition.
    private static int LostClearance(Snapshot s, Result r)
    {
        var nodes = s.Nodes.ToDictionary(n => n.Id);
        return s.Inbound.Count(e => Graph.Finite(e.SourceGripX) &&
            nodes[e.Node].CurrentTop.X + nodes[e.Node].Inputs[e.Port].X - e.SourceGripX.Value >= 20 &&
            r.Tops[e.Node].X + nodes[e.Node].Inputs[e.Port].X - e.SourceGripX.Value < 20) +
            s.Outbound.Count(e => Graph.Finite(e.TargetGripX) &&
            e.TargetGripX.Value - (nodes[e.Node].CurrentTop.X + nodes[e.Node].Outputs[e.Port].X) >= 20 &&
            e.TargetGripX.Value - (r.Tops[e.Node].X + nodes[e.Node].Outputs[e.Port].X) < 20);
    }

    private static void RecordNoOutside(System.Text.StringBuilder text, Result r)
    {
        text.Append(Moves(r)).Append('|').Append(r.Success).Append('|').Append(r.Diagnostic).Append('|')
            .Append(r.Unstable).Append('|').Append(r.Rounds).Append('|').Append(r.CrossingsBefore).Append('|')
            .Append(r.CrossingsAfter).Append('|').Append(string.Join(";", r.Log));
        foreach (var pair in r.Tops.OrderBy(p => p.Key))
            text.Append('|').Append(pair.Key).Append(':').Append(pair.Value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append(',').Append(pair.Value.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(r.Ranks[pair.Key]);
        foreach (var e in r.LaneEdges) text.Append('|').Append(e.From).Append(':').Append(e.FromPort).Append('>').Append(e.To).Append(':').Append(e.ToPort);
        text.Append("\r\n"); // Fixed record separator for the hashes captured on deebe27.
    }

    private static string NoOutsideHash(System.Text.StringBuilder text) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));

    private static void Recognized(Snapshot s, Result r)
    {
        var applied = Applied(s, r);
        Assert(Graph.TryCreate(applied, out var graph, out var diagnostic), diagnostic);
        Assert(FixedPoint.TryMatch(graph, new Vec2(applied.Nodes.Min(n => n.CurrentTop.X),
            applied.Nodes.Min(n => n.CurrentTop.Y)), out var ranks, out var islands, out _),
            "emitted layout was not recognized");
        if (s.Edges.Count == 0) return;
        foreach (var island in islands)
        {
            var columns = Enumerable.Range(0, island.Max(n => ranks[n.Id]) + 1)
                .Select(c => island.Where(n => ranks[n.Id] == c).ToArray()).ToArray();
            var geometry = ColumnWindows.Place(graph, columns, ColumnWindows.OldLeft(island));
            for (int c = 1; c < columns.Length; c++)
            {
                double stretch = geometry.Left[c] - geometry.Left[c - 1] - geometry.Packed[c] + geometry.Packed[c - 1];
                Assert(stretch >= -1e-8 && stretch <= 150 + 1e-8, "gap stretch " + stretch);
            }
        }
        Assert(r.Moves.All(m => m.Pivot.X == Math.Floor(m.Pivot.X) && m.Pivot.Y == Math.Floor(m.Pivot.Y)),
            "fractional pivot");
    }

    private static void BackwardWireTests()
    {
        Check("Per-island movement: distinct left edges survive without outside X", () => {
            var a = Component(1, "A", 200, 500); var b = Component(2, "B", 450, 700);
            var c = Component(3, "C", -50, -100); var d = Component(4, "D", 10, -50);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            var r = Solve(s);
            Near(r.Tops[a.Id].X, 200, "first island old left"); Near(r.Tops[c.Id].X, -50, "second island old left");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Per-island movement: mixed outside X leaves the other island at its distinct left edge", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 200, 80);
            var c = Component(3, "C", -50, 200); var d = Component(4, "D", 10, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            var baseline = Solve(s);
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 400 } };
            var r = Solve(s);
            Near(r.Tops[a.Id].X, 420, "required slide");
            foreach (var n in new[] { c, d }) Near(r.Tops[n.Id].X, baseline.Tops[n.Id].X, "unconstrained island");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Per-island movement: X cannot break island ownership or ordering ties", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", -100, 0); var d = Component(4, "D", -100, 80);
            var orphan = Component(5, "Orphan", 500, 200);
            var s = Make(new[] { a, b, c, d, orphan }, Wire(a, b), Wire(c, d));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            Assert(Graph.TryCreate(s, out var graph, out _), "invalid scene");
            var islands = Ranking.Assign(graph, out _);
            Assert(islands[0].Any(n => n.Id == a.Id) && islands[0].Any(n => n.Id == orphan.Id), "X chose orphan owner");
            var r = Solve(s); var applied = Applied(s, r);
            Assert(Graph.TryCreate(applied, out graph, out _), "invalid output");
            var after = Ranking.Assign(graph, out _);
            Assert(islands.Select(i => string.Join(",", i.Select(n => n.Id).OrderBy(id => id)))
                .SequenceEqual(after.Select(i => string.Join(",", i.Select(n => n.Id).OrderBy(id => id)))), "island membership changed");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Column windows: recognize a rounded free source whose level long wire is obstructed", () => {
            var root = Component(1, "Root", 0, 0, 1, height: 20);
            var source = Component(2, "Free source", 0, 49, 1, height: 123);
            var middle = Component(3, "Obstruction", 120, 0, 1, height: 120);
            var target = Component(4, "Target", 240, 100, 1, height: 20);
            var orphan = Component(5, "Fractional anchor", 360, -.5, 1, height: 10);
            var last = Component(6, "Last", 360, 25.5, 1, height: 20);
            foreach (var n in new[] { root, source, middle, target }) n.PivotOffset = new Vec2(0, 0);
            orphan.PivotOffset = new Vec2(0, .5);
            last.PivotOffset = new Vec2(0, .5);
            source.Kind = NodeKind.Param;
            middle.Inputs[0] = new Vec2(0, 10); middle.Outputs[0] = new Vec2(90, 110);
            var s = Make(new[] { root, source, middle, target, orphan, last }, Wire(root, middle), Wire(middle, target), Wire(source, target), Wire(target, last));
            s.Inbound = new[] { new ExternalInbound { Node = source.Id, SourceGripX = -100 } };
            Assert(Graph.TryCreate(s, out var graph, out var diagnostic), diagnostic);
            Near(source.CurrentTop.Y - target.CurrentTop.Y, -51, "visible source alignment");
            Near(target.Inputs[0].Y - source.Outputs[0].Y, -51.5, "raw grip alignment");
            Assert(FixedPoint.TryMatch(graph, new Vec2(0, -.5), out var ranks), "rounded free-source contact not recognized");
            Assert(ranks[source.Id] == 0 && ranks[target.Id] == 2, "long source link lost");
            Assert(!FixedPoint.VisibleLanes(graph, graph.Nodes.ToList(), ranks).Any(), "obstructed wire acquired a lane");
            var r = Solve(s); Assert(r.Moves.Count == 0, "rounded contact moved"); Guarantees(s, r);
        });
        Check("Column windows: deliberate far return wire preserves the output with outside X stripped", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 50, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 3000 } };
            var r = Solve(s);
            Near(r.Tops[a.Id].X, 0, "far return wire moved the envelope");
            Assert(BoundaryCounts(s, r).After == 1, "deliberate return wire was chased");
            Guarantees(s, r);
            s.Inbound[0].SourceGripX = null;
            SameResult(Solve(s), r);
        });
        Check("Column windows: required forward-wire slide straightens a return wire at no extra cost", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            var forced = Solve(s);
            s.Inbound = s.Inbound.Concat(new[] { new ExternalInbound { Node = b.Id, SourceGripX = 400 } }).ToArray();
            var r = Solve(s); var counts = BoundaryCounts(s, r);
            SameResult(forced, r);
            Assert(counts.Before == 1 && counts.After == 0 && counts.Created == 0, "free straightening lost");
            Guarantees(s, r);
        });
        Check("Column windows: a held island cannot subsidize a far return wire in another island", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", 0, 200); var d = Component(4, "D", 500, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            s.Outbound = new[] { new ExternalOutbound { Node = c.Id, TargetGripX = 90 } };
            s.Inbound = new[] { new ExternalInbound { Node = d.Id, SourceGripX = 250 } };
            var baseline = Solve(s);
            s.Inbound = s.Inbound.Concat(new[] { new ExternalInbound { Node = a.Id, SourceGripX = 3000 } }).ToArray();
            var r = Solve(s);
            Near(r.Tops[c.Id].X, 0, "held anchor");
            Near(r.Tops[a.Id].X, baseline.Tops[a.Id].X, "far return wire moved its island");
            Near(r.Tops[b.Id].X, baseline.Tops[b.Id].X, "far return wire moved its island's output");
            Assert(BoundaryCounts(s, r).Created == 0, "held island lost its forward wire");
            Guarantees(s, r);
        });
        Check("Column windows: a return wire cannot add stretch to a required slide", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            var forced = Solve(s);
            s.Inbound = s.Inbound.Concat(new[] { new ExternalInbound { Node = b.Id, SourceGripX = 500 } }).ToArray();
            var r = Solve(s); SameResult(forced, r);
            Assert(BoundaryCounts(s, r).After == 1, "return wire added stretch"); Guarantees(s, r);
        });
        Check("Column windows: remaining return wires break equal-distance slide ties", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            // Packed B is at shift+120. Keeping the source needs shift>=100;
            // keeping B's consumer needs shift<=-100. Clearance moves both limits
            // outward by 20; both directions cost one new wire and one lost clearance.
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 100 },
                new ExternalInbound { Node = b.Id, SourceGripX = 150 } };
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 110 } };
            var r = Solve(s);
            Near(r.Tops[a.Id].X, 120, "remaining-wire movement tie-break");
            var counts = BoundaryCounts(s, r);
            Assert(counts.Created == 1 && counts.After == 1, "remaining-wire tie-break lost");
            Guarantees(s, r);
        });
        Check("Column windows: fixed-point recognition cannot translate across a fractional wire threshold", () => {
            var n = Component(1, "Node", 1, 0);
            var anchor = Component(2, "Anchor", 0, 100);
            var s = Make(new[] { n, anchor });
            s.Inbound = new[] { new ExternalInbound { Node = n.Id, SourceGripX = .25 } };
            var r = Solve(s); Near(r.Tops[n.Id].X, 1, "fractional source threshold"); Guarantees(s, r);
            n.CurrentTop = new Vec2(0, 0);
            anchor.CurrentTop = new Vec2(0, 100);
            s = Make(new[] { anchor, n }, Wire(anchor, n));
            s.Outbound = new[] { new ExternalOutbound { Node = n.Id, TargetGripX = n.Width + .25 } };
            r = Solve(s); Near(r.Tops[n.Id].X, 0, "fractional consumer threshold");
            Near(r.Tops[anchor.Id].X, -120, "consumer window slide"); Guarantees(s, r);
        });
        Check("Column windows: slide right of an outside source and left of an outside consumer", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            var r = Solve(s); Near(r.Tops[a.Id].X, 320, "source window"); Guarantees(s, r);
            a.CurrentTop = new Vec2(0, 0); b.CurrentTop = new Vec2(50, 80);
            s.Inbound = Array.Empty<ExternalInbound>();
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 200 } };
            r = Solve(s); Near(r.Tops[b.Id].X + b.Width, 180, "consumer window"); Guarantees(s, r);
        });
        Check("Column windows: both boundaries open one gap; impossible conflict respects 150 cap", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 500, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Outbound = new[] { new ExternalOutbound { Node = a.Id, TargetGripX = 90 } };
            s.Inbound = new[] { new ExternalInbound { Node = b.Id, SourceGripX = 250 } };
            var r = Solve(s); Near(r.Tops[a.Id].X, 0, "held left"); Near(r.Tops[b.Id].X, 270, "held right");
            Assert(BoundaryCounts(s, r).Created == 0, "avoidable backward wire"); Guarantees(s, r);
            s.Inbound[0].SourceGripX = 400;
            r = Solve(s); Assert(BoundaryCounts(s, r).Created == 1, "conflict must sacrifice exactly one wire");
            double gap = r.Tops[b.Id].X - r.Tops[a.Id].X - a.Width;
            Assert(gap >= 30 && gap <= 180, "conflict exceeded gap cap"); Guarantees(s, r);
        });
        Check("Column windows: keep a pre-existing backward wire when fixing it breaks a forward wire", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Outbound = new[] { new ExternalOutbound { Node = a.Id, TargetGripX = 90 } };
            s.Inbound = new[] { new ExternalInbound { Node = b.Id, SourceGripX = 300 } };
            var r = Solve(s); var counts = BoundaryCounts(s, r);
            Assert(counts.Created == 0 && counts.Before == 1 && counts.After == 1, "forward priority lost");
            Near(r.Tops[b.Id].X - r.Tops[a.Id].X, 120, "unnecessary stretch"); Guarantees(s, r);
        });
        Check("Clearance: outside consumer below a chain retains 20 px rather than zero slack", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 30, 80);
            var c = Component(3, "C", 60, 160);
            var s = Make(new[] { a, b, c }, Wire(a, b), Wire(b, c));
            s.Outbound = new[] { new ExternalOutbound { Node = c.Id, TargetGripX = 200, TargetGripY = 500 } };
            var r = Solve(s);
            Near(200 - (r.Tops[c.Id].X + c.Outputs[0].X), 20, "consumer clearance");
            Assert(LostClearance(s, r) == 0, "lost feasible clearance");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Clearance: packed shortcut must protect clearance even when packed stays forward", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 50, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 220 } };
            var r = Solve(s);
            Near(r.Tops[a.Id].X, -10, "packed shortcut lost clearance");
            Near(220 - (r.Tops[b.Id].X + b.Width), 20, "clearance threshold");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Clearance: tier 1 wins when retaining clearance would exceed the stretch cap", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 500, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Outbound = new[] { new ExternalOutbound { Node = a.Id, TargetGripX = 90 } };
            s.Inbound = new[] { new ExternalInbound { Node = b.Id, SourceGripX = 260 } };
            var r = Solve(s);
            Assert(BoundaryCounts(s, r).Created == 0 && LostClearance(s, r) == 1, "tier 1 must win");
            Near(r.Tops[b.Id].X, 260, "stop at forward threshold");
            Near(r.Tops[b.Id].X - r.Tops[a.Id].X, 260, "minimum tier-1 stretch");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Clearance: a wire starting at 5 px does not justify extra movement", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 95 } };
            var r = Solve(s);
            Near(r.Tops[a.Id].X, -115, "unprotected clearance moved the island");
            Near(95 - (r.Tops[b.Id].X + b.Width), 0, "tier-1-only stop");
            Guarantees(s, r); Recognized(s, r);
        });
        Check("Clearance: whole pivots near both thresholds use actual grip arithmetic", () => {
            foreach (bool inbound in new[] { false, true })
                foreach (double margin in new[] { 0.0, 20.0 })
                    foreach (double epsilon in new[] { -1e-10, 0, 1e-10 })
                    {
                        var a = Component(1, "A", inbound ? 500 : 0, 0);
                        var b = Component(2, "B", 0, 80);
                        a.PivotOffset = new Vec2(12.25, 7); b.PivotOffset = new Vec2(12.25, 7);
                        var s = Make(new[] { a, b }, Wire(a, b));
                        double outside = (inbound ? 300.75 : 200.75) + epsilon;
                        if (inbound)
                        {
                            if (margin == 0) a.CurrentTop = new Vec2(outside + 5, 0);
                            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = outside } };
                        }
                        else
                        {
                            if (margin == 0) b.CurrentTop = new Vec2(outside - b.Width - 5, 80);
                            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = outside } };
                        }
                        var r = Solve(s);
                        double slack = inbound ? r.Tops[a.Id].X + a.Inputs[0].X - outside
                            : outside - (r.Tops[b.Id].X + b.Outputs[0].X);
                        Assert(slack >= margin && slack < margin + 1 + 1e-8, "rounded stop missed its tier threshold");
                        Assert(BoundaryCounts(s, r).Created == 0 && LostClearance(s, r) == 0, "rounded threshold violated");
                        Guarantees(s, r); Recognized(s, r);
                    }
        });
        Check("Column windows: satisfied boundaries preserve today's output exactly, including fractional geometry", () => {
            var s = RandomDag(30011); var legacy = Solve(s);
            foreach (var e in s.Inbound) e.SourceGripX = -10000;
            foreach (var e in s.Outbound) e.TargetGripX = 10000;
            SameResult(legacy, Solve(s));
        });
        Check("Column windows: packed return-wire shortcut retains fractional legacy rounding", () => {
            var s = RandomDag(140877); var legacy = Solve(s);
            foreach (var e in s.Inbound) e.SourceGripX = 10000;
            var r = Solve(s); SameResult(legacy, r); Guarantees(s, r);
            Assert(BoundaryCounts(s, r).After == s.Inbound.Count, "deliberate return wire was chased");
            Assert(Graph.TryCreate(Applied(s, r), out var graph, out var diagnostic), diagnostic);
            Assert(FixedPoint.TryMatch(graph, new Vec2(r.Tops.Values.Min(t => t.X), r.Tops.Values.Min(t => t.Y)), out _),
                "packed return-wire fixed point not recognized");
            Assert(graph.WindowTops == null, "return wires bypassed the packed shortcut");
        });
        Check("Per-island movement: a required left slide leaves the other island fixed", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", 0, 200); var d = Component(4, "D", 0, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            // B's current output is 90 <= 200. Packed B would output 210,
            // so A-B must slide left 30 to retain 20 px clearance. C-D has no crossing constraint.
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 200 } };
            var first = Solve(s);
            Near(first.Tops[a.Id].X, -30, "required first slide");
            Near(first.Tops[b.Id].X, 90, "first consumer position");
            Near(first.Tops[c.Id].X, 0, "unconstrained island's zero movement");
            Near(first.Tops[d.Id].X, 120, "first unconstrained output");
            Assert(BoundaryCounts(s, first).Created == 0, "first plan broke the forward wire");
            var applied = Applied(s, first);
            var second = Solve(applied);
            Near(second.Tops[a.Id].X, -30, "required island stays");
            Near(second.Tops[c.Id].X, 0, "other island keeps its own anchor");
            Near(second.Tops[d.Id].X, 120, "second unconstrained output");
            Assert(BoundaryCounts(applied, second).Created == 0, "second plan broke the forward wire");
            Assert(second.Moves.Count == 0, "second solve moved an island");
            Guarantees(s, first); Recognized(s, first);
        });
        Check("Column windows: disconnected islands optimize movement without sharing constraints", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", 0, 200); var d = Component(4, "D", 50, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            s.Outbound = new[] { new ExternalOutbound { Node = d.Id, TargetGripX = 200 } };
            var r = Solve(s); Assert(BoundaryCounts(s, r).Created == 0, "independent windows coupled"); Guarantees(s, r);
        });
        Check("Column windows: a held island cannot pay for another island's extra return-wire slide", () => {
            // Required replacement for the former "free return-wire repair" test.
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", 0, 200); var d = Component(4, "D", 0, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 },
                new ExternalInbound { Node = b.Id, SourceGripX = 450 } };
            s.Outbound = new[] { new ExternalOutbound { Node = d.Id, TargetGripX = 210 } };
            var r = Solve(s);
            Near(r.Tops[a.Id].X, 320, "minimum required island slide"); Near(r.Tops[c.Id].X, -20, "held envelope");
            Near(r.Tops[b.Id].X - r.Tops[a.Id].X, 120, "free repair stretched a gap");
            Assert(BoundaryCounts(s, r).After == 1, "return wire added island movement"); Guarantees(s, r);
        });
        Check("Column windows: every island stops at its own minimum required slide", () => {
            // Required replacement for the former global-envelope repair test.
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", 500, 200); var d = Component(4, "D", 0, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 100 },
                new ExternalInbound { Node = c.Id, SourceGripX = 100 },
                new ExternalInbound { Node = b.Id, SourceGripX = 300 },
                new ExternalInbound { Node = d.Id, SourceGripX = 300 } };
            var r = Solve(s);
            Near(r.Tops.Values.Min(t => t.X), 120, "remaining wires moved the envelope");
            Near(r.Tops[a.Id].X, 120, "first island minimum slide"); Near(r.Tops[c.Id].X, 120, "second island minimum slide");
            var counts = BoundaryCounts(s, r);
            Assert(counts.Created == 0 && counts.After == 2, "return wires added island movement"); Guarantees(s, r);
        });
        Check("Column windows: per-node grip offsets, equality thresholds and unconnected nodes", () => {
            var a = Component(1, "A", 250.25, 0, width: 120.25);
            var b = Component(2, "B", 10.5, 80, width: 70.75);
            a.PivotOffset = new Vec2(12.25, 7); b.PivotOffset = new Vec2(12.5, 7);
            a.Inputs[0] = new Vec2(9.25, 11); b.Outputs[0] = new Vec2(67.5, 22);
            var s = Make(new[] { a, b });
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 100.25 } };
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 200.5 } };
            var r = Solve(s); Assert(BoundaryCounts(s, r).Created == 0, "per-node windows lost"); Guarantees(s, r);
            var applied = Applied(s, r);
            s.Inbound[0].SourceGripX = r.Tops[a.Id].X + a.Inputs[0].X;
            s.Outbound[0].TargetGripX = r.Tops[b.Id].X + b.Outputs[0].X;
            r = Solve(applied); Assert(BoundaryCounts(applied, r).Created == 0, "equality became backward"); Guarantees(applied, r);
        });
        Check("Backward counter: forward, backward, created, both ends moved and unknown", () => {
            void Counts(WireX wire, Dictionary<Guid, double> dx, int before, int after, int created)
            {
                var counts = BackwardWires.Count(new[] { wire }, dx);
                Assert(counts.Before == before && counts.After == after && counts.Created == created, "wrong backward counts");
            }
            var empty = new Dictionary<Guid, double>();
            Counts(new WireX(Id(1), 10, Id(2), 20), empty, 0, 0, 0);
            Counts(new WireX(Id(1), 20, Id(2), 10), empty, 1, 1, 0);
            Counts(new WireX(Id(1), 10, Id(2), 20), new Dictionary<Guid, double> { [Id(1)] = 11 }, 0, 1, 1);
            Counts(new WireX(Id(1), 10, Id(2), 20), new Dictionary<Guid, double> { [Id(1)] = 11, [Id(2)] = 12 }, 0, 0, 0);
            Counts(new WireX(Id(1), 20, Id(2), 10), new Dictionary<Guid, double> { [Id(2)] = 10 }, 1, 0, 0);
            Counts(new WireX(Id(1), 10, Id(2), 10), empty, 0, 0, 0);
            Counts(new WireX(Id(1), double.NaN, Id(2), 10), empty, 0, 0, 0);
            Counts(new WireX(Id(1), 10, Id(2), double.NaN), empty, 0, 0, 0);
            var combined = BackwardWires.Count(new[] { new WireX(Id(1), 10, Id(2), 20),
                new WireX(Id(1), 20, Id(2), 10), new WireX(Id(1), double.NaN, Id(2), 20) },
                new Dictionary<Guid, double> { [Id(1)] = 11 });
            Assert(combined.Before == 1 && combined.After == 2 && combined.Created == 1, "wrong combined counts");
        });
        Check("External X validation accepts unknown and rejects both infinities", () => {
            var n = Component(1, "Node", 0, 0);
            var s = Make(new[] { n });
            var inbound = new ExternalInbound { Node = n.Id };
            var outbound = new ExternalOutbound { Node = n.Id };
            s.Inbound = new[] { inbound }; s.Outbound = new[] { outbound };
            Assert(Graph.TryCreate(s, out _, out _), "unknown X rejected");
            foreach (double bad in new[] { double.PositiveInfinity, double.NegativeInfinity })
            {
                inbound.SourceGripX = bad; Assert(!Graph.TryCreate(s, out _, out _), "infinite inbound accepted");
                inbound.SourceGripX = null;
                outbound.TargetGripX = bad; Assert(!Graph.TryCreate(s, out _, out _), "infinite outbound accepted");
                outbound.TargetGripX = null;
            }
            inbound.SourceGripX = double.NaN; outbound.TargetGripX = double.NaN;
            Assert(Graph.TryCreate(s, out _, out _), "NaN unknown X rejected");
            inbound.SourceGripX = -123; outbound.TargetGripX = 456;
            Assert(Graph.TryCreate(s, out _, out _), "finite X rejected");
        });
        Check("Column windows: 700 random DAGs, fixed points, caps, legacy comparison and determinism", () => {
            int legacyCreated = 0, windowsCreated = 0, packedLost = 0, windowsLost = 0;
            int multipleIslands = 0, multipleOutsideIslands = 0;
            var noOutsideResults = new System.Text.StringBuilder();
            for (int i = 0; i < 700; i++)
            {
                int seed = 30011 + i * 7919;
                try
                {
                    var s = RandomDag(seed); var random = new Random(seed);
                    var legacy = Solve(s); RecordNoOutside(noOutsideResults, legacy);
                    foreach (var e in s.Outbound) { e.TargetGripX = random.Next(-200, 1800) + random.Next(4) * .25; e.TargetObjectId = Id(500); }
                    foreach (var e in s.Inbound) { e.SourceGripX = random.Next(-200, 1800) + random.Next(4) * .25; e.SourceObjectId = Id(501); }
                    Assert(Graph.TryCreate(s, out var inputGraph, out var inputDiagnostic), inputDiagnostic);
                    var inputIslands = Ranking.Assign(inputGraph, out _);
                    if (inputIslands.Count > 1) multipleIslands++;
                    var outsideNodes = new HashSet<Guid>(s.Inbound.Select(e => e.Node).Concat(s.Outbound.Select(e => e.Node)));
                    if (inputIslands.Count(island => island.Any(n => outsideNodes.Contains(n.Id))) > 1) multipleOutsideIslands++;
                    var r = Solve(s); Guarantees(s, r);
                    var oldCounts = BoundaryCounts(s, legacy); var newCounts = BoundaryCounts(s, r);
                    Assert(newCounts.Created <= oldCounts.Created, "created more backward wires: " + oldCounts.Created + " -> " + newCounts.Created);
                    legacyCreated += oldCounts.Created; windowsCreated += newCounts.Created;
                    int oldLost = LostClearance(s, legacy), newLost = LostClearance(s, r);
                    Assert(newCounts.Created < oldCounts.Created || newLost <= oldLost,
                        "per-scene lost-clearance increase: " + oldLost + " -> " + newLost + " created: " + newCounts.Created + "/" + oldCounts.Created);
                    packedLost += oldLost; windowsLost += newLost;
                    var applied = Applied(s, r);
                    Assert(Graph.TryCreate(applied, out var graph, out var diagnostic), diagnostic);
                    Assert(FixedPoint.TryMatch(graph, new Vec2(applied.Nodes.Min(n => n.CurrentTop.X), applied.Nodes.Min(n => n.CurrentTop.Y)),
                        out var matchedRanks, out var islands, out _), "windows fixed point not recognized");
                    if (s.Edges.Count > 0)
                        foreach (var island in islands)
                        {
                            var columns = Enumerable.Range(0, island.Max(n => matchedRanks[n.Id]) + 1)
                                .Select(c => island.Where(n => matchedRanks[n.Id] == c).ToArray()).ToArray();
                            var geometry = ColumnWindows.Place(graph, columns, ColumnWindows.OldLeft(island));
                            for (int c = 1; c < columns.Length; c++)
                            {
                                double stretch = geometry.Left[c] - geometry.Left[c - 1] - geometry.Packed[c] + geometry.Packed[c - 1];
                                Assert(stretch >= -1e-8 && stretch <= 150 + 1e-8, "gap stretch " + stretch);
                            }
                        }
                    foreach (var e in s.Inbound) e.SourceGripX = null;
                    foreach (var e in s.Outbound) e.TargetGripX = null;
                    SameResult(legacy, Solve(s));
                    foreach (var e in s.Inbound) e.SourceGripX = random.Next(-200, 1800) + random.Next(4) * .25;
                    foreach (var e in s.Outbound) e.TargetGripX = random.Next(-200, 1800) + random.Next(4) * .25;
                    r = Solve(s);
                    SameResult(r, Solve(new Snapshot { Nodes = Shuffle(s.Nodes, random), Edges = Shuffle(s.Edges, random),
                        Inbound = Shuffle(s.Inbound, random), Outbound = Shuffle(s.Outbound, random), Options = s.Options }));
                    Assert(r.Moves.All(m => m.Pivot.X == Math.Floor(m.Pivot.X) && m.Pivot.Y == Math.Floor(m.Pivot.Y)), "fractional pivot");
                    Assert(r.Moves.All(m => s.Nodes.Any(n => n.Id == m.Id)), "nonmember moved");
                }
                catch (Exception e) { throw new Exception("seed " + seed + ": " + e.Message); }
            }
            Assert(NoOutsideHash(noOutsideResults) == "35680BE785965B9A9B95AFEF985323D70438863C276FC8B59BC141F9F6A07BE3",
                "no-outside-X output differs from deebe27 (700 random scenes)");
            Assert(multipleOutsideIslands >= 70, "too few scenes exercise outside X in multiple islands");
            Console.WriteLine("Column windows created backward wires: " + legacyCreated + " -> " + windowsCreated + " across 700 seeded scenes");
            Console.WriteLine("Column windows lost clearance: " + packedLost + " -> " + windowsLost);
            Console.WriteLine("Column windows multi-island coverage: " + multipleIslands + " scenes; " + multipleOutsideIslands + " with outside X in multiple islands");
        });
        Check("Column windows: 700 distinct-left multi-island scenes, mixed X, alignment and rounding", () => {
            int packedCreated = 0, windowsCreated = 0, packedLost = 0, windowsLost = 0;
            int noOutside = 0, mixedOutside = 0, allOutside = 0, groupColumns = 0, indentedColumns = 0;
            var noOutsideResults = new System.Text.StringBuilder();
            for (int scene = 0; scene < 700; scene++)
            {
                int seed = 164634 + scene * 7919;
                try
                {
                    var random = new Random(seed);
                    var nodes = new List<Node>(); var edges = new List<Edge>();
                    var inbound = new List<ExternalInbound>(); var outbound = new List<ExternalOutbound>();
                    int count = random.Next(2, 5), id = 0, mode = scene % 3;
                    var members = new List<Node[]>();
                    for (int island = 0; island < count; island++)
                    {
                        double left = -900 + island * 1300 + random.Next(4) * .25;
                        bool groups = (scene + island) % 5 == 0;
                        var ns = Enumerable.Range(0, random.Next(3, 7)).Select(j => {
                            var n = Component(++id, "Island " + island + " node " + j,
                                left + (j == 0 ? 0 : random.Next(500)), random.Next(-200, 800) + random.Next(4) * .25,
                                width: random.Next(40, 180) + random.Next(4) * .25,
                                height: random.Next(20, 100) + random.Next(4) * .25);
                            n.PivotOffset = new Vec2(random.Next(20) + random.Next(4) * .25, random.Next(20) + random.Next(4) * .25);
                            if (groups) n.Kind = NodeKind.Group;
                            else if (j == 0) { n.Kind = NodeKind.Param; n.ParamRole = true; n.AlignRight = n.Width / 2; }
                            return n;
                        }).ToArray();
                        members.Add(ns); nodes.AddRange(ns);
                        edges.Add(Wire(ns[0], ns[2])); edges.Add(Wire(ns[1], ns[2], 1));
                        for (int j = 3; j < ns.Length; j++) edges.Add(Wire(ns[j - 1], ns[j]));
                        if (groups) groupColumns++; else indentedColumns++;
                        if (mode == 0 || mode == 1 && island != 0) continue;
                        // Include forward constraints and far pre-existing return wires.
                        foreach (var n in ns)
                        {
                            if (random.Next(2) == 0) inbound.Add(new ExternalInbound { Node = n.Id,
                                SourceGripX = left + random.Next(-200, 1000) + random.Next(4) * .25 });
                            if (random.Next(2) == 0) outbound.Add(new ExternalOutbound { Node = n.Id,
                                TargetGripX = left + random.Next(100, 1200) + random.Next(4) * .25 });
                        }
                        inbound.Add(new ExternalInbound { Node = ns[0].Id, SourceGripX = left + 3000 });
                    }
                    var s = Make(nodes.ToArray(), edges.ToArray());
                    var packed = Solve(s); RecordNoOutside(noOutsideResults, packed); Guarantees(s, packed); Recognized(s, packed);
                    foreach (var island in members)
                        Near(island.Min(n => packed.Tops[n.Id].X), ColumnWindows.OldLeft(island), "packed island old left", .5);
                    s.Inbound = inbound.ToArray(); s.Outbound = outbound.ToArray();
                    var r = Solve(s); Guarantees(s, r); Recognized(s, r);
                    var baseline = BoundaryCounts(s, packed); var actual = BoundaryCounts(s, r);
                    Assert(actual.Created <= baseline.Created, "per-scene created-wire increase");
                    packedCreated += baseline.Created; windowsCreated += actual.Created;
                    int oldLost = LostClearance(s, packed), newLost = LostClearance(s, r);
                    Assert(actual.Created < baseline.Created || newLost <= oldLost, "per-scene lost-clearance increase: " + oldLost + " -> " + newLost + " created: " + actual.Created + "/" + baseline.Created);
                    packedLost += oldLost; windowsLost += newLost;
                    foreach (var island in members.Where(ns => !inbound.Any(e => ns.Any(n => n.Id == e.Node)) &&
                        !outbound.Any(e => ns.Any(n => n.Id == e.Node))))
                        foreach (var n in island) Near(r.Tops[n.Id].X, packed.Tops[n.Id].X, "outside X in another island changed X");
                    SameResult(r, Solve(new Snapshot { Nodes = Shuffle(s.Nodes, random), Edges = Shuffle(s.Edges, random),
                        Inbound = Shuffle(s.Inbound, random), Outbound = Shuffle(s.Outbound, random), Options = s.Options }));
                    if (mode == 0) noOutside++; else if (mode == 1) mixedOutside++; else allOutside++;
                }
                catch (Exception e) { throw new Exception("seed " + seed + ": " + e.Message); }
            }
            Assert(NoOutsideHash(noOutsideResults) == "52596B0757C95B97C26FBAB9CDE9E77770982FB80336850A9034C7D0075861C3",
                "no-outside-X output differs from deebe27 (700 distinct-left scenes)");
            Assert(groupColumns > 0 && indentedColumns > 0, "missing alignment coverage");
            Console.WriteLine("Distinct-left created backward wires: " + packedCreated + " -> " + windowsCreated + " across 700 seeded scenes");
            Console.WriteLine("Distinct-left lost clearance: " + packedLost + " -> " + windowsLost);
            Console.WriteLine("Distinct-left coverage: " + noOutside + " without outside X; " + mixedOutside +
                " with outside X on one island only; " + allOutside + " with X on every island; " + groupColumns +
                " pure-group islands; " + indentedColumns + " indented islands");
        });
        Check("Return-pin guard blocker: qualifying release cannot be rediscovered after orphan reranking", () => {
            // With this all-component fixture, loose mode differs from releasing
            // just Early's pin only in name: Z is already at its ASAP maximum,
            // and no param input/output exemptions apply. This exercises existing
            // ranking and fixed-point rules without shipping a non-idempotent guard.
            var a = Component(101, "A", 100, 0, width: 250);
            var b = Component(102, "B", 200, 60, width: 80);
            var c = Component(103, "C", 300, 120, width: 80);
            var d = Component(104, "D", 400, 180, width: 80);
            var z = Component(105, "Z", 500, 240, width: 80);
            var feeder = Component(106, "Feeder", 0, 300, width: 40);
            var early = Component(107, "Early", 100, 360, width: 80);
            var orphan = Component(108, "Orphan", 0, 420, width: 200);
            var s = Make(new[] { a, b, c, d, z, feeder, early, orphan },
                Wire(a,b), Wire(b,c), Wire(c,d), Wire(d,z), Wire(a,early), Wire(feeder,early,1));
            s.Outbound = new[] { new ExternalOutbound { Node = early.Id, TargetGripX = 750 } };
            var windows = Solve(s); Guarantees(s, windows);
            Assert(windows.Ranks[early.Id] == 4, "sink pin unexpectedly released");
            s.Outbound[0].TargetGripX = null; // Retain the exact historical guard counterexample with packed placement.
            var pinned = Solve(s);
            s.Options = new Options { ReturnConstraint = false };
            var released = Solve(s);
            var applied = Applied(s, released); applied.Options = new Options();
            var repinned = Solve(applied);
            Near(early.CurrentTop.X + 80, 180, "input output grip X");
            Near(pinned.Tops[early.Id].X + 80, 810, "first pinned output grip X");
            Near(released.Tops[early.Id].X + 80, 360, "released output grip X");
            Near(repinned.Tops[early.Id].X + 80, 690, "next pinned output grip X");
            Assert(pinned.Ranks[early.Id] == 4 && released.Ranks[early.Id] == 1 && repinned.Ranks[early.Id] == 4,
                "counterexample ranks changed");
            Assert(pinned.Ranks[orphan.Id] == 3 && released.Ranks[orphan.Id] == 0 && repinned.Ranks[orphan.Id] == 0,
                "counterexample no longer exposes orphan reranking");
            Assert(repinned.Moves.Count > 0, "counterexample no longer exposes idempotence conflict");
            // Each configuration remains an ordinary stable layout in its own right.
            Guarantees(s, released); Guarantees(applied, repinned);
        });
    }
}
