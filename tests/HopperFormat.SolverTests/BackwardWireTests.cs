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

    private static void BackwardWireTests()
    {
        Check("Column windows: fixed-point recognition cannot translate across a fractional wire threshold", () => {
            var n = Component(1, "Node", 0, 0);
            var s = Make(new[] { n });
            s.Inbound = new[] { new ExternalInbound { Node = n.Id, SourceGripX = .25 } };
            var r = Solve(s); Near(r.Tops[n.Id].X, 1, "fractional source threshold"); Guarantees(s, r);
            s.Inbound = Array.Empty<ExternalInbound>();
            s.Outbound = new[] { new ExternalOutbound { Node = n.Id, TargetGripX = n.Width - .25 } };
            r = Solve(s); Near(r.Tops[n.Id].X, -1, "fractional consumer threshold"); Guarantees(s, r);
        });
        Check("Column windows: slide right of an outside source and left of an outside consumer", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            var r = Solve(s); Near(r.Tops[a.Id].X, 300, "source window"); Guarantees(s, r);
            a.CurrentTop = new Vec2(0, 0); b.CurrentTop = new Vec2(50, 80);
            s.Inbound = Array.Empty<ExternalInbound>();
            s.Outbound = new[] { new ExternalOutbound { Node = b.Id, TargetGripX = 200 } };
            r = Solve(s); Near(r.Tops[b.Id].X + b.Width, 200, "consumer window"); Guarantees(s, r);
        });
        Check("Column windows: both boundaries open one gap; impossible conflict respects 150 cap", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 500, 80);
            var s = Make(new[] { a, b }, Wire(a, b));
            s.Outbound = new[] { new ExternalOutbound { Node = a.Id, TargetGripX = 90 } };
            s.Inbound = new[] { new ExternalInbound { Node = b.Id, SourceGripX = 250 } };
            var r = Solve(s); Near(r.Tops[a.Id].X, 0, "held left"); Near(r.Tops[b.Id].X, 250, "held right");
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
        Check("Column windows: satisfied boundaries preserve today's output exactly, including fractional geometry", () => {
            var s = RandomDag(30011); var legacy = Solve(s);
            foreach (var e in s.Inbound) e.SourceGripX = -10000;
            foreach (var e in s.Outbound) e.TargetGripX = 10000;
            SameResult(legacy, Solve(s));
        });
        Check("Column windows: disconnected islands share the envelope tie-break without sharing constraints", () => {
            var a = Component(1, "A", 500, 0); var b = Component(2, "B", 0, 80);
            var c = Component(3, "C", 0, 200); var d = Component(4, "D", 50, 280);
            var s = Make(new[] { a, b, c, d }, Wire(a, b), Wire(c, d));
            s.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripX = 300 } };
            s.Outbound = new[] { new ExternalOutbound { Node = d.Id, TargetGripX = 200 } };
            var r = Solve(s); Assert(BoundaryCounts(s, r).Created == 0, "independent windows coupled"); Guarantees(s, r);
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
            int legacyCreated = 0, windowsCreated = 0;
            for (int i = 0; i < 700; i++)
            {
                int seed = 30011 + i * 7919;
                try
                {
                    var s = RandomDag(seed); var random = new Random(seed);
                    var legacy = Solve(s);
                    foreach (var e in s.Outbound) { e.TargetGripX = random.Next(-200, 1800) + random.Next(4) * .25; e.TargetObjectId = Id(500); }
                    foreach (var e in s.Inbound) { e.SourceGripX = random.Next(-200, 1800) + random.Next(4) * .25; e.SourceObjectId = Id(501); }
                    var r = Solve(s); Guarantees(s, r);
                    var oldCounts = BoundaryCounts(s, legacy); var newCounts = BoundaryCounts(s, r);
                    Assert(newCounts.Created <= oldCounts.Created, "created more backward wires: " + oldCounts.Created + " -> " + newCounts.Created);
                    legacyCreated += oldCounts.Created; windowsCreated += newCounts.Created;
                    var applied = Applied(s, r);
                    Assert(Graph.TryCreate(applied, out var graph, out var diagnostic), diagnostic);
                    Assert(FixedPoint.TryMatch(graph, new Vec2(applied.Nodes.Min(n => n.CurrentTop.X), applied.Nodes.Min(n => n.CurrentTop.Y)),
                        out var matchedRanks, out var islands, out _), "windows fixed point not recognized");
                    if (s.Edges.Count > 0)
                        foreach (var island in islands)
                        {
                            var columns = Enumerable.Range(0, island.Max(n => matchedRanks[n.Id]) + 1)
                                .Select(c => island.Where(n => matchedRanks[n.Id] == c).ToArray()).ToArray();
                            var geometry = ColumnWindows.Place(graph, columns, applied.Nodes.Min(n => n.CurrentTop.X));
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
            Console.WriteLine("Column windows created backward wires: " + legacyCreated + " -> " + windowsCreated + " across 700 seeded scenes");
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
