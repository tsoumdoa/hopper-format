using System;
using System.Collections.Generic;
using System.Linq;
using HopperFormat.Layout;
using Baseline = HopperFormat.Tests.Baseline;

internal static class FormatAllWireTests
{
    private static Guid Id(int i) => new Guid(i, 0, 0, new byte[8]);
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static Node Node(int id, double x, double y, double width = 80) => new Node {
        Id = Id(id), CurrentTop = new Vec2(x, y), Width = width, Height = 40,
        Inputs = new[] { new Vec2(0, 10) }, Outputs = new[] { new Vec2(width, 10) } };
    private static FormatAllTarget Group(int id, params Node[] nodes) => new FormatAllTarget {
        Id = Id(id), Bounds = Envelope(nodes.Select(n => new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height))),
        Hierarchy = new HierarchySnapshot { Groups = new[] { new HierarchyGroup { Id = Id(id), Nodes = nodes } },
            Edges = nodes.Zip(nodes.Skip(1), (a, b) => new Edge { From = a.Id, To = b.Id }).ToArray() } };
    private static Box Envelope(IEnumerable<Box> boxes) {
        var bs = boxes.ToArray(); double x = bs.Min(b => b.X), y = bs.Min(b => b.Y);
        return new Box(x, y, bs.Max(b => b.Right) - x, bs.Max(b => b.Bottom) - y);
    }
    private static Dictionary<Guid, Box> Members(FormatAllSnapshot s) => s.Targets.SelectMany(t => t.Hierarchy.Groups)
        .SelectMany(g => g.Nodes).Select(n => (n.Id, Bounds: new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height)))
        .Concat(s.LooseObjects.Select(n => (n.Id, n.Bounds))).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Bounds);
    private static WireX Wire(FormatAllSnapshot s, Guid from, Guid to) {
        var b = Members(s); return new WireX(from, b[from].Right, to, b[to].X);
    }
    private static Dictionary<Guid, double> Dx(FormatAllSnapshot s, Result r) {
        var before = Members(s); return r.Tops.ToDictionary(p => p.Key, p => p.Value.X - before[p.Key].X);
    }
    private static FormatAllSnapshot Applied(FormatAllSnapshot s, Result r) {
        var before = Members(s); var dx = Dx(s, r);
        Box M(Guid id, Box b) => r.Tops.TryGetValue(id, out var top) ? new Box(top.X, top.Y, b.Width, b.Height) : b;
        var loose = s.LooseObjects.Select(n => new NonNodeMember { Id = n.Id, Bounds = M(n.Id, n.Bounds), PivotOffset = n.PivotOffset }).ToArray();
        var obstacles = s.Obstacles.ToList();
        foreach (var n in s.LooseObjects) obstacles.Remove(n.Bounds);
        obstacles.AddRange(loose.Select(n => n.Bounds));
        return new FormatAllSnapshot { Options = s.Options, Obstacles = obstacles, LooseObjects = loose,
            Wires = s.Wires.Select(w => new WireX(w.From, w.OutputGripX + dx.GetValueOrDefault(w.From), w.To, w.InputGripX + dx.GetValueOrDefault(w.To))).ToArray(),
            Targets = s.Targets.Select(t => new FormatAllTarget { Id = t.Id, Bounds = r.GroupBoxes[t.Id],
                Hierarchy = new HierarchySnapshot { Edges = t.Hierarchy.Edges, Groups = t.Hierarchy.Groups.Select(g => new HierarchyGroup {
                    Id = g.Id, Nodes = g.Nodes.Select(n => new Node { Id = n.Id, CurrentTop = r.Tops[n.Id], Width = n.Width, Height = n.Height,
                        PivotOffset = n.PivotOffset, Inputs = n.Inputs, Outputs = n.Outputs }).ToArray() }).ToArray() } }).ToArray() };
    }
    private static Result Legacy(FormatAllSnapshot s) => Baseline.FormatAllSolver.Solve(new Baseline.FormatAllSnapshot {
        Options = s.Options, Obstacles = s.Obstacles, Targets = s.Targets.Select(t => new Baseline.FormatAllTarget {
            Id = t.Id, Bounds = t.Bounds, Hierarchy = t.Hierarchy }).ToArray() }).Layout;
    private static Result Verify(FormatAllSnapshot s, string label) {
        var r = FormatAllSolver.Solve(s).Layout;
        Assert(r.Success && !r.Unstable, label + ": " + r.Diagnostic + " " + string.Join(";", r.Log));
        var before = Members(s);
        var offsets = s.Targets.SelectMany(t => t.Hierarchy.Groups).SelectMany(g => g.Nodes).ToDictionary(n => n.Id, n => n.PivotOffset);
        foreach (var n in s.LooseObjects) offsets.Add(n.Id, n.PivotOffset);
        foreach (var m in r.Moves) {
            double x = m.Pivot.X - before[m.Id].X - offsets[m.Id].X, y = m.Pivot.Y - before[m.Id].Y - offsets[m.Id].Y;
            Assert(Math.Abs(x - Math.Round(x)) < 1e-6 && Math.Abs(y - Math.Round(y)) < 1e-6, label + " fractional move");
        }
        var again = FormatAllSolver.Solve(Applied(s, r)).Layout;
        Assert(again.Success && !again.Unstable && again.Moves.Count == 0, label + " repeat: " + again.Diagnostic + " " +
            string.Join(";", again.Moves.Select(m => m.Id + " -> " + m.Pivot.X + "," + m.Pivot.Y)) +
            "\nInput: " + Dump(s) + "\nFirst: " + Dump(Applied(s, r)) + "\nSecond: " + (again.Success ? Dump(Applied(Applied(s, r), again)) : "failed"));
        var shuffled = new FormatAllSnapshot { Targets = s.Targets.Reverse().ToArray(), Obstacles = s.Obstacles.Reverse().ToArray(),
            Wires = s.Wires.Reverse().ToArray(), LooseObjects = s.LooseObjects.Reverse().ToArray(), Options = s.Options };
        var other = FormatAllSolver.Solve(shuffled).Layout;
        Assert(other.Success && r.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y)).SequenceEqual(other.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y))), label + " shuffle");
        var all = r.GroupBoxes.Values.Concat(s.LooseObjects.Where(n => r.Tops.ContainsKey(n.Id)).Select(n => {
            var top = r.Tops[n.Id]; return new Box(top.X, top.Y, n.Bounds.Width, n.Bounds.Height); })).ToArray();
        for (int i = 0; i < all.Length; i++) for (int j = i + 1; j < all.Length; j++) {
            var a = all[i]; var b = all[j];
            Assert(a.Right <= b.X || b.Right <= a.X || a.Bottom <= b.Y || b.Bottom <= a.Y, label + " overlap");
        }
        var fixedObstacles = s.Obstacles.ToList();
        foreach (var n in s.LooseObjects.Where(n => r.Tops.ContainsKey(n.Id))) fixedObstacles.Remove(n.Bounds);
        bool Clear(Box a, Box b) => a.Right <= b.X - 50 + 1e-7 || b.Right <= a.X - 50 + 1e-7 || a.Bottom <= b.Y - 40 + 1e-7 || b.Bottom <= a.Y - 40 + 1e-7;
        foreach (var a in all) foreach (var b in fixedObstacles) Assert(Clear(a, b), label + " fixed-obstacle clearance");
        foreach (var a in r.GroupBoxes) foreach (var b in r.GroupBoxes.Where(p => p.Key != a.Key)) Assert(Clear(a.Value, b.Value), label + " group clearance");
        return r;
    }
    private static string Dump(FormatAllSnapshot s) => string.Join(";", Members(s).OrderBy(p => p.Key).Select(p => p.Key.ToString().Substring(0, 8) + "=" + p.Value.X + "," + p.Value.Y)) +
        " Wires:" + string.Join(";", s.Wires.Select(w => w.From.ToString().Substring(0, 8) + "(" + w.OutputGripX + ")->" + w.To.ToString().Substring(0, 8) + "(" + w.InputGripX + ")"));
    internal static int Run() {
        int failures = 0;
        void Check(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
        Check("Horizontal constraints: exact L1 optima and hard-cycle infeasibility", () => {
            Assert(HorizontalConstraints.Solve(new[] { Id(1), Id(2) }, new[] {
                new HorizontalConstraints.Bound(Id(1), Id(2), 1), new HorizontalConstraints.Bound(Id(2), Id(1), 1) }) == null, "hard cycle admitted");
            Assert(HorizontalConstraints.Solve(new[] { Id(1), Id(2) }, new[] {
                new HorizontalConstraints.Bound(Guid.Empty, Id(2), 151), new HorizontalConstraints.Bound(Id(1), Guid.Empty, -156),
                new HorizontalConstraints.Bound(Id(2), Guid.Empty, -285), new HorizontalConstraints.Bound(Id(2), Id(1), 552) }) == null,
                "hard capacity became finite after cancelling an objective cycle");
            for (int seed = 0; seed < 100; seed++) {
                var rng = new Random(seed); var bounds = new List<HorizontalConstraints.Bound>();
                var witness = new[] { 0, rng.Next(-4, 5), rng.Next(-4, 5), rng.Next(-4, 5) };
                for (int i = 0; i < 8; i++) {
                    int from = rng.Next(4), to = rng.Next(4);
                    bounds.Add(new HorizontalConstraints.Bound(Id(from), Id(to), witness[to] - witness[from] - rng.Next(3)));
                }
                var actual = HorizontalConstraints.Solve(new[] { Id(1), Id(2), Id(3) }, bounds);
                Assert(actual != null && bounds.All(b => actual.GetValueOrDefault(b.To) - actual.GetValueOrDefault(b.From) >= Math.Ceiling(b.Gap)), "infeasible optimum");
                double best = double.PositiveInfinity;
                for (int a = -8; a <= 8; a++) for (int b = -8; b <= 8; b++) for (int c = -8; c <= 8; c++) {
                    var xs = new Dictionary<Guid, double> { [Id(1)] = a, [Id(2)] = b, [Id(3)] = c };
                    if (bounds.All(e => xs.GetValueOrDefault(e.To) - xs.GetValueOrDefault(e.From) >= Math.Ceiling(e.Gap))) best = Math.Min(best, Math.Abs(a) + Math.Abs(b) + Math.Abs(c));
                }
                Assert(actual.Values.Sum(Math.Abs) == best, "not least movement at seed " + seed);
            }
        });
        Check("Format all live case: loose source above C2/C3 follows C3", () => {
            var g = Group(1, Node(101, 0, 100), Node(102, 200, 100), Node(103, 400, 100), Node(104, 700, 100));
            var loose = new NonNodeMember { Id = Id(501), Bounds = new Box(560, 0, 80, 40) };
            var s = new FormatAllSnapshot { Targets = new[] { g }, LooseObjects = new[] { loose }, Obstacles = new[] { loose.Bounds } };
            s.Wires = new[] { Wire(s, loose.Id, Id(104)) };
            Assert(BackwardWires.Count(s.Wires, Dx(s, Legacy(s))).Created == 1, "live fixture does not reproduce HEAD");
            var r = Verify(s, "live case");
            Assert(BackwardWires.Count(s.Wires, Dx(s, r)).Created == 0 && r.Tops[loose.Id].X < loose.Bounds.X, "live wire backward");
        });
        Check("Format all without crossing wires retains HEAD coordinates, moves and log exactly", () => {
            var s = new FormatAllSnapshot { Targets = new[] {
                Group(1, Node(101, .31, 100), Node(102, 100.31, 130)),
                Group(2, Node(201, 210.8, 112), Node(202, 450.8, 150)),
                Group(3, Node(301, 15.3, 300), Node(302, 200.3, 360)) },
                Obstacles = new[] { new Box(200, 400, 50, 50) } };
            s.Wires = s.Targets.SelectMany(t => t.Hierarchy.Edges).Select(e => Wire(s, e.From, e.To)).ToArray();
            var r = FormatAllSolver.Solve(s).Layout; var old = Legacy(s);
            Assert(r.Success && r.Tops.All(p => p.Value.X == old.Tops[p.Key].X && p.Value.Y == old.Tops[p.Key].Y)
                && r.GroupBoxes.All(p => p.Value.Equals(old.GroupBoxes[p.Key]))
                && r.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y)).SequenceEqual(old.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y)))
                && r.Log.SequenceEqual(old.Log), "no-crossing legacy output changed");
        });
        Check("Format all satellite consumer follows growth; collision clears with its group", () => {
            var g = Group(1, Node(101, 0, 100), Node(102, 100, 100));
            var loose = new NonNodeMember { Id = Id(501), Bounds = new Box(185, 0, 80, 40) };
            var obstacle = new Box(270, 0, 80, 40);
            var s = new FormatAllSnapshot { Targets = new[] { g }, LooseObjects = new[] { loose }, Obstacles = new[] { loose.Bounds, obstacle } };
            s.Wires = new[] { Wire(s, Id(102), loose.Id) };
            Assert(BackwardWires.Count(s.Wires, Dx(s, Legacy(s))).Created == 1, "consumer fixture does not reproduce HEAD");
            var r = Verify(s, "satellite consumer collision");
            Assert(BackwardWires.Count(s.Wires, Dx(s, r)).Created == 0 && r.Tops[loose.Id].X > loose.Bounds.X, "consumer did not follow");
        });
        Check("Format all multi-group loose object stays fixed; cross-row wires stay forward", () => {
            var groups = new[] { Group(1, Node(101, 0, 100), Node(102, 100, 100)), Group(2, Node(201, 185, 400), Node(202, 285, 400)) };
            var loose = new NonNodeMember { Id = Id(501), Bounds = new Box(420, 0, 80, 40) };
            var s = new FormatAllSnapshot { Targets = groups, LooseObjects = new[] { loose }, Obstacles = new[] { loose.Bounds } };
            s.Wires = new[] { Wire(s, Id(102), loose.Id), Wire(s, Id(202), loose.Id), Wire(s, Id(102), Id(201)) };
            Assert(BackwardWires.Count(s.Wires, Dx(s, Legacy(s))).Created == 1, "cross-row fixture does not reproduce HEAD");
            var r = Verify(s, "fixed multi-group consumer");
            Assert(!r.Tops.ContainsKey(loose.Id) && BackwardWires.Count(s.Wires, Dx(s, r)).Created == 0, "fixed object moved or backward created");
        });
        Check("Format all pre-existing backward group wire never justifies a far shift", () => {
            var s = new FormatAllSnapshot { Targets = new[] { Group(1, Node(101, 10000, 100), Node(102, 10100, 100)), Group(2, Node(201, 0, 400), Node(202, 200, 400)) } };
            s.Wires = new[] { Wire(s, Id(102), Id(201)) };
            var r = Verify(s, "backward group wire"); var old = Legacy(s);
            Assert(r.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y)).SequenceEqual(old.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y))), "backward wire chased");
        });
        Check("Format all loose-to-loose connectivity disqualifies satellites", () => {
            var loose = new[] { new NonNodeMember { Id = Id(501), Bounds = new Box(300, 0, 80, 40) },
                new NonNodeMember { Id = Id(502), Bounds = new Box(500, 0, 80, 40) } };
            var s = new FormatAllSnapshot { Targets = new[] { Group(1, Node(101, 0, 100), Node(102, 100, 100)) },
                LooseObjects = loose, Obstacles = loose.Select(n => n.Bounds).ToArray() };
            s.Wires = new[] { Wire(s, Id(102), Id(501)), Wire(s, Id(501), Id(502)) };
            var r = Verify(s, "loose-to-loose disqualification");
            Assert(loose.All(n => !r.Tops.ContainsKey(n.Id)), "loose-to-loose satellite moved");
        });
        Check("Format all exact fractional grip thresholds keep forward and 20 px bounds", () => {
            foreach (double clearance in new[] { 0.0, Math.BitIncrement(0.0), 5.0, Math.BitDecrement(20.0), 20.0, Math.BitIncrement(20.0) }) {
                var source = Node(102, 100.31, 100, 80.21); source.PivotOffset = new Vec2(.69, 0);
                var first = Node(101, .31, 100); first.PivotOffset = new Vec2(.69, 0);
                double targetX = source.CurrentTop.X + source.Width + clearance;
                var loose = new NonNodeMember { Id = Id(501), Bounds = new Box(targetX, 0, 80, 40), PivotOffset = new Vec2(Math.Ceiling(targetX) - targetX, 0) };
                var s = new FormatAllSnapshot { Targets = new[] { Group(1, first, source), Group(2, Node(201, -1000, 400)) },
                    LooseObjects = new[] { loose }, Obstacles = new[] { loose.Bounds } };
                s.Wires = new[] { Wire(s, source.Id, loose.Id), Wire(s, Id(201), loose.Id) };
                var r = Verify(s, "fractional threshold " + clearance); var dx = Dx(s, r); var w = s.Wires[0];
                double gap = w.InputGripX + dx.GetValueOrDefault(w.To) - (w.OutputGripX + dx.GetValueOrDefault(w.From));
                Assert(gap >= (w.InputGripX - w.OutputGripX >= 20 ? 20 : 0), "strict fractional bound violated");
            }
        });
        Check("Format all satellite eligibility counts unknown wires and resolves label aliases", () => {
            var g = Group(1, Node(101, 0, 100), Node(102, 200, 100));
            var alias = Group(3, g.Hierarchy.Groups[0].Nodes[1]);
            var loose = new NonNodeMember { Id = Id(501), Bounds = new Box(350, 0, 80, 40) };
            var s = new FormatAllSnapshot { Targets = new[] { g, alias }, LooseObjects = new[] { loose }, Obstacles = new[] { loose.Bounds } };
            s.Wires = new[] { Wire(s, Id(102), loose.Id) };
            var r = FormatAllSolver.Solve(s).Layout;
            Assert(r.Success && r.Tops.ContainsKey(loose.Id) && r.Tops[loose.Id].X == 260, "label alias disqualified its owner's satellite");
            s.Targets = new[] { g, Group(2, Node(201, 0, 400)) };
            s.Wires = new[] { Wire(s, Id(102), loose.Id), new WireX(Id(201), double.NaN, loose.Id, double.NaN) };
            r = Verify(s, "unknown second root");
            Assert(!r.Tops.ContainsKey(loose.Id), "unknown grip geometry hid the second group");
        });
        Check("Format all wire to an excluded empty-bounds group member remains a fixed endpoint", () => {
            var empty = Node(103, 500, 100); empty.Width = 0;
            var g = Group(1, Node(101, 0, 100), Node(102, 100, 100));
            g.Hierarchy.Groups[0].Nodes = g.Hierarchy.Groups[0].Nodes.Concat(new[] { empty }).ToArray();
            var loose = new NonNodeMember { Id = Id(501), Bounds = new Box(600, 0, 80, 40) };
            var s = new FormatAllSnapshot { Targets = new[] { g }, LooseObjects = new[] { loose }, Obstacles = new[] { loose.Bounds },
                Wires = new[] { new WireX(empty.Id, 500, loose.Id, 600) } };
            var r = FormatAllSolver.Solve(s).Layout;
            Assert(r.Success && !r.Tops.ContainsKey(empty.Id) && BackwardWires.Count(s.Wires, Dx(s, r)).Created == 0,
                "empty-bounds endpoint failed capture or moved with its root");
        });
        Check("Format all seed 1264 regression: reorder a constrained row without drifting on repeat", () => {
            var loose = new[] { (575.0, 445.0), (537.0, 358.0), (644.0, 684.0), (648.0, -50.0), (810.0, 259.0) }
                .Select((p, i) => new NonNodeMember { Id = Id(10000 + i), Bounds = new Box(p.Item1, p.Item2, 80, 40) }).ToArray();
            var s = new FormatAllSnapshot { Targets = new[] {
                Group(1, Node(100, 388, 244), Node(101, 517, 276), Node(102, 680, 261)),
                Group(2, Node(200, 263, 291), Node(201, 420, 247), Node(202, 657, 303), Node(203, 887, 293)) },
                LooseObjects = loose, Obstacles = loose.Select(n => n.Bounds).ToArray() };
            s.Wires = new[] { Wire(s, Id(10000), Id(202)), Wire(s, Id(102), Id(10001)), Wire(s, Id(10002), Id(203)),
                Wire(s, Id(100), Id(10002)), Wire(s, Id(10003), Id(102)), Wire(s, Id(200), Id(10003)),
                Wire(s, Id(10004), Id(201)), Wire(s, Id(102), Id(200)) };
            var r = Verify(s, "seed 1264 regression");
            Assert(BackwardWires.Count(s.Wires, Dx(s, r)).Created == 0 && r.GroupBoxes[Id(1)].X < r.GroupBoxes[Id(2)].X,
                "constrained row order was not corrected");
        });
        foreach (bool fractional in new[] { false, true }) Check("Format all crossing wires: 1500 " + (fractional ? "fractional" : "integer") + " scenes, whole moves, shuffle, repeat and HEAD comparison", () => {
            int oldCreated = 0, newCreated = 0, fallback = 0, satellitesMoved = 0;
            int oldLost = 0, newLost = 0;
            var fallbackSeeds = new List<int>();
            for (int seed = 0; seed < 1500; seed++) {
                var rng = new Random(seed);
                var groups = Enumerable.Range(1, rng.Next(2, 7)).Select(i => {
                    double x = rng.Next(-100, 600), y = rng.Next(0, 800); int count = rng.Next(2, 6);
                    return Group(i, Enumerable.Range(0, count).Select(j => {
                        var n = Node(i * 100 + j, x + j * rng.Next(90, 250), y + rng.Next(80), fractional ? rng.Next(45, 150) + rng.NextDouble() : 80);
                        if (fractional && seed % 2 != 0) { n.PivotOffset = new Vec2(rng.NextDouble(), rng.NextDouble()); n.CurrentTop = new Vec2(n.CurrentTop.X - n.PivotOffset.X, n.CurrentTop.Y - n.PivotOffset.Y); }
                        return n;
                    }).ToArray());
                }).ToArray();
                var loose = Enumerable.Range(0, rng.Next(2, 6)).Select(i => new NonNodeMember {
                    Id = Id(10000 + i), Bounds = new Box(rng.Next(-100, 1000) - (fractional ? .35 : 0), rng.Next(-100, 800) - (fractional ? .65 : 0), 80, 40), PivotOffset = fractional ? new Vec2(.35, .65) : new Vec2(0, 0) }).ToArray();
                var s = new FormatAllSnapshot { Targets = groups, LooseObjects = loose, Obstacles = loose.Select(n => n.Bounds).ToArray() };
                var wires = groups.SelectMany(g => g.Hierarchy.Edges).Select(e => Wire(s, e.From, e.To)).ToList();
                foreach (var n in loose) {
                    var g = groups[rng.Next(groups.Length)]; var node = g.Hierarchy.Groups[0].Nodes[rng.Next(g.Hierarchy.Groups[0].Nodes.Count)];
                    wires.Add(rng.Next(2) == 0 ? Wire(s, n.Id, node.Id) : Wire(s, node.Id, n.Id));
                    if (rng.Next(3) == 0) {
                        var other = groups.First(t => t.Id != g.Id).Hierarchy.Groups[0].Nodes[0]; wires.Add(Wire(s, other.Id, n.Id));
                    }
                }
                for (int i = 1; i < groups.Length; i++) wires.Add(Wire(s, groups[i - 1].Hierarchy.Groups[0].Nodes.Last().Id, groups[i].Hierarchy.Groups[0].Nodes[0].Id));
                if (seed % 3 == 0) wires.Add(Wire(s, loose[0].Id, loose[1].Id));
                s.Wires = wires;
                var r = Verify(s, "seed " + seed); var baseline = Legacy(s);
                int old = BackwardWires.Count(wires, Dx(s, baseline)).Created, now = BackwardWires.Count(wires, Dx(s, r)).Created;
                oldCreated += old; newCreated += now;
                if (r.Log.Any(l => l.StartsWith("Wire-aware tidy: conflicting"))) {
                    fallback++; fallbackSeeds.Add(seed);
                    Assert(r.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y)).SequenceEqual(baseline.Moves.Select(m => (m.Id, m.Pivot.X, m.Pivot.Y))),
                        "seed " + seed + " fallback differs from HEAD");
                }
                satellitesMoved += r.Moves.Count(m => loose.Any(n => n.Id == m.Id));
                int Lost(Result layout) { var dx = Dx(s, layout); return wires.Count(w => w.InputGripX - w.OutputGripX >= 20 &&
                    w.InputGripX + dx.GetValueOrDefault(w.To) - w.OutputGripX - dx.GetValueOrDefault(w.From) < 20); }
                oldLost += Lost(baseline); newLost += Lost(r);
                Assert(now <= old, "seed " + seed + " created " + now + " vs HEAD " + old);
            }
            Console.WriteLine("Format-all created backward wires: " + oldCreated + " -> " + newCreated + " across 1500 scenes");
            Console.WriteLine("Format-all lost 20 px clearance: " + oldLost + " -> " + newLost + "; rigid fallback scenes: " + fallback + "; satellites moved: " + satellitesMoved);
            if (fallbackSeeds.Count > 0) Console.WriteLine("Format-all rigid fallback seeds: " + string.Join(", ", fallbackSeeds));
        });
        return failures;
    }
}
