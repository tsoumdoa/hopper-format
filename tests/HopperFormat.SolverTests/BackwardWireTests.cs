using System;
using System.Collections.Generic;
using System.Linq;
using HopperFormat.Layout;

internal static partial class Program
{
    private static void BackwardWireTests()
    {
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
        Check("Outside X is data only: 400 random DAGs retain legacy output and guarantees", () => {
            for (int i = 0; i < 400; i++)
            {
                int seed = 30011 + i * 7919;
                try
                {
                    var s = RandomDag(seed); var random = new Random(seed);
                    var legacy = Solve(s);
                    foreach (var e in s.Outbound) { e.TargetGripX = random.Next(-200, 1800) + random.Next(4) * .25; e.TargetObjectId = Id(500); }
                    foreach (var e in s.Inbound) { e.SourceGripX = random.Next(-200, 1800) + random.Next(4) * .25; e.SourceObjectId = Id(501); }
                    var r = Solve(s); SameResult(legacy, r); Guarantees(s, r);
                    SameResult(r, Solve(new Snapshot { Nodes = Shuffle(s.Nodes, random), Edges = Shuffle(s.Edges, random),
                        Inbound = Shuffle(s.Inbound, random), Outbound = Shuffle(s.Outbound, random), Options = s.Options }));
                    Assert(r.Moves.All(m => m.Pivot.X == Math.Floor(m.Pivot.X) && m.Pivot.Y == Math.Floor(m.Pivot.Y)), "fractional pivot");
                    Assert(r.Moves.All(m => s.Nodes.Any(n => n.Id == m.Id)), "nonmember moved");
                }
                catch (Exception e) { throw new Exception("seed " + seed + ": " + e.Message); }
            }
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
