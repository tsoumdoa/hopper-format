using System;
using System.Collections.Generic;
using System.Linq;
using HopperFormat.Layout;

internal static class FormatAllTests
{
    private static Guid Id(int i) => new Guid(i, 0, 0, new byte[8]);
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static string Coordinates(Result r) => string.Join("; ", r.GroupBoxes.Select(p => $"{p.Key.ToString().Substring(0, 8)}=({p.Value.X},{p.Value.Y}) {p.Value.Width}x{p.Value.Height}"));
    private static Result Tidy(Box[] boxes, params Box[] obstacles) => GroupTidy.Solve(boxes.Select((b, i) => (b, i)).ToDictionary(p => Id(p.i + 1), p => p.b), obstacles, new Options());
    private static void At(Result r, int id, double x, double y)
    { var b = r.GroupBoxes[Id(id)]; Assert(Math.Abs(b.X - x) < 1e-7 && Math.Abs(b.Y - y) < 1e-7, Coordinates(r)); }
    private static bool Whole(double value) => Math.Abs(value - Math.Round(value)) <= 1e-6;
    private static void Clearance(Result result, IEnumerable<Box> obstacles, string context)
    {
        foreach (var p in result.GroupBoxes) foreach (var other in result.GroupBoxes.Where(q => q.Key != p.Key).Select(q => q.Value).Concat(obstacles)) {
            var b = p.Value;
            Assert(b.Right <= other.X - 50 + 1e-7 || other.Right <= b.X - 50 + 1e-7 || b.Bottom <= other.Y - 40 + 1e-7 || other.Bottom <= b.Y - 40 + 1e-7,
                context + " clearance against (" + other.X + "," + other.Y + "): " + Coordinates(result));
        }
    }
    private static IEnumerable<HierarchyGroup> Groups(HierarchyGroup g) => new[] { g }.Concat(g.Children.SelectMany(Groups));
    private static void WholeMoves(FormatAllSnapshot snapshot, Result result, string context)
    {
        var groups = snapshot.Targets.SelectMany(t => t.Hierarchy.Groups.SelectMany(Groups)).ToArray();
        var pivots = groups.SelectMany(g => g.Nodes).Select(n => (n.Id, Pivot: new Vec2(n.CurrentTop.X + n.PivotOffset.X,n.CurrentTop.Y + n.PivotOffset.Y)))
            .Concat(groups.SelectMany(g => g.NonNodes).Select(n => (n.Id, Pivot: new Vec2(n.Bounds.X + n.PivotOffset.X,n.Bounds.Y + n.PivotOffset.Y))))
            .GroupBy(p => p.Id).ToDictionary(g => g.Key,g => g.First().Pivot);
        foreach (var top in result.Tops) {
            var member = groups.SelectMany(g => g.Nodes).FirstOrDefault(n => n.Id == top.Key);
            var note = member == null ? groups.SelectMany(g => g.NonNodes).First(n => n.Id == top.Key) : null;
            var offset = member == null ? note.PivotOffset : member.PivotOffset;
            Assert(Whole(top.Value.X + offset.X - pivots[top.Key].X) && Whole(top.Value.Y + offset.Y - pivots[top.Key].Y),context + " fractional descendant delta");
        }
        foreach (var move in result.Moves) Assert(Whole(move.Pivot.X - pivots[move.Id].X) && Whole(move.Pivot.Y - pivots[move.Id].Y),
            context + " fractional pivot delta: " + move.Id + " (" + (move.Pivot.X - pivots[move.Id].X) + "," + (move.Pivot.Y - pivots[move.Id].Y) + ")");
    }
    internal static int Run()
    {
        int failures = 0;
        void Check(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
        Check("Format all overlap/edge snap: A=(0,0), B=(250,0), C=(0,160), D=(250,120)", () => {
            var r = Tidy(new[] { new Box(0,0,200,120), new Box(260,10,120,80), new Box(20,180,100,80), new Box(250,70,160,150) });
            At(r,1,0,0); At(r,2,250,0); At(r,3,0,160); At(r,4,250,120); Assert(!r.Unstable, Coordinates(r));
        });
        Check("Format all gap evening: 70 becomes 50, 150 is kept", () => {
            var r = Tidy(new[] { new Box(0,0,100,80), new Box(170,0,100,80) });
            At(r,1,0,0); At(r,2,150,0);
            r = Tidy(new[] { new Box(0,0,100,80), new Box(250,0,100,80) });
            At(r,1,0,0); At(r,2,250,0);
        });
        Check("Format all rows: tops 0/12 snap, tops 0/100 stay separate", () => {
            var r = Tidy(new[] { new Box(0,0,100,80), new Box(250,12,100,80), new Box(500,100,100,80) });
            At(r,1,0,0); At(r,2,250,0); At(r,3,500,100);
        });
        Check("Format all grown group clears fixed component and locked group", () => {
            var obstacles = new[] { new Box(100,0,80,80), new Box(230,0,80,80) };
            var r = Tidy(new[] { new Box(0,0,160,80) }, obstacles);
            At(r,1,360,0); Assert(obstacles[0].X == 100 && obstacles[1].X == 230, "obstacle moved");
        });
        Check("Format all bottom clearance: group=(230,0), component=(100,90), 10-unit gap pushes right", () => {
            var obstacle = new Box(100,90,80,40);
            var r = Tidy(new[] { new Box(0,0,160,80) }, obstacle);
            At(r,1,230,0); Assert(obstacle.X == 100 && obstacle.Y == 90, "component moved");
        });
        Check("Format all subunit row/column spans stay aligned, including seed-5 lefts", () => {
            var boxes = new[] { new Box(4.3886,0,80,80),new Box(4.8125,300,80,80),new Box(4.9797,600,80,80),
                new Box(400,900.3886,80,80),new Box(700,900.8125,80,80),new Box(1000,900.9797,80,80) };
            var r = Tidy(boxes);
            for (int i = 0; i < boxes.Length; i++) At(r,i + 1,boxes[i].X,boxes[i].Y);
            Assert(!r.Unstable && r.Rounds == 1,"already aligned cluster moved");
        });
        Check("Format all unaligned row/column snaps use the smallest Guid representative", () => {
            var r = Tidy(new[] { new Box(60.8,0,80,80),new Box(40.4,300,80,80),
                new Box(400,660.8,80,80),new Box(700,642.4,80,80) });
            At(r,1,60.8,0); At(r,2,60.4,300); At(r,3,400,660.8); At(r,4,700,660.4);
            Assert(!r.Unstable && Coordinates(r) == Coordinates(GroupTidy.Solve(r.GroupBoxes,Array.Empty<Box>(),new Options())),"representative not fixed");
        });
        Check("Format all fractional collision push: group=(232.3,0.4), obstacle clearance=50.9", () => {
            var obstacle = new Box(110.8,90.5,70.6,40.2);
            var r = Tidy(new[] { new Box(.3,.4,100.2,80.3) },obstacle);
            At(r,1,232.3,.4); Clearance(r,new[] { obstacle },"fractional push");
            Assert(Whole(r.GroupBoxes[Id(1)].X - .3) && !r.Unstable,"fractional push translation");
            Assert(Coordinates(r) == Coordinates(GroupTidy.Solve(r.GroupBoxes,new[] { obstacle },new Options())),"fractional push not fixed");
        });
        Check("Format all fractional boxes/padding: A=(10.3,0.4), B=(160.7,0.9), C=(10.7,120.8), whole descendant deltas and fixed point", () => {
            FormatAllTarget Target(int i, double x, double y) {
                double nx = x + 2.2, ny = y + 3.3;
                var node = new Node { Id = Id(100 + i), CurrentTop = new Vec2(nx,ny), Width = 94.8, Height = i == 2 ? 71.5 : 72.5,
                    PivotOffset = new Vec2(Math.Ceiling(nx) - nx,Math.Ceiling(ny) - ny) };
                var child = new HierarchyGroup { Id = Id(200 + i), Nodes = new[] { node } };
                var note = new NonNodeMember { Id = Id(300 + i), Bounds = new Box(nx + .2,ny + .3,10.1,8.2), PivotOffset = new Vec2(.4,.6) };
                var root = new HierarchyGroup { Id = Id(i), Children = new[] { child }, NonNodes = new[] { note }, Padding = new Padding(2.2,3.3,3.1,4.4) };
                return new FormatAllTarget { Id = root.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { root } } };
            }
            var snapshot = new FormatAllSnapshot { Targets = new[] { Target(1,10.3,.4),Target(2,180.7,12.9),Target(3,31.7,150.8) } };
            var r = FormatAllSolver.Solve(snapshot).Layout;
            Assert(r.Success && !r.Unstable, "fractional golden refused");
            At(r,1,10.3,.4); At(r,2,160.7,.9); At(r,3,10.7,120.8);
            Assert(Math.Abs(r.GroupBoxes[Id(1)].Y - r.GroupBoxes[Id(2)].Y) <= .5 + 1e-7 &&
                Math.Abs(r.GroupBoxes[Id(1)].X - r.GroupBoxes[Id(3)].X) <= .5 + 1e-7, "fractional snap: " + Coordinates(r));
            double gapX = r.GroupBoxes[Id(2)].X - r.GroupBoxes[Id(1)].Right, gapY = r.GroupBoxes[Id(3)].Y - r.GroupBoxes[Id(1)].Bottom;
            Assert(gapX >= 50 - 1e-7 && gapX < 51 && gapY >= 40 - 1e-7 && gapY < 41, "fractional gaps: " + gapX + "," + gapY);
            WholeMoves(snapshot,r,"fractional golden");
            foreach (var t in snapshot.Targets) {
                var original = HierarchySolver.Solve(t.Hierarchy);
                double dx = r.GroupBoxes[t.Id].X - original.GroupBoxes[t.Id].X, dy = r.GroupBoxes[t.Id].Y - original.GroupBoxes[t.Id].Y;
                Assert(Whole(dx) && Whole(dy), "fractional group translation");
                var childId = t.Hierarchy.Groups.Single().Children.Single().Id;
                var child = original.GroupBoxes[childId];
                var moved = r.GroupBoxes[childId];
                Assert(Math.Abs(moved.X - child.X - dx) < 1e-7 && Math.Abs(moved.Y - child.Y - dy) < 1e-7, "nested box translation");
            }
            var again = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = snapshot.Targets.Select(t => new FormatAllTarget {
                Id = t.Id, Hierarchy = Applied(t.Hierarchy,r) }).ToArray() }).Layout;
            Assert(again.Success && !again.Unstable && again.Moves.Count == 0, "fractional golden not fixed: " + Coordinates(again));
        });
        Check("Format all skips overlapping membership and inner failures, retaining fixed obstacles", () => {
            var shared = new Node { Id = Id(100), CurrentTop = new Vec2(0,0), Width = 100, Height = 80 };
            FormatAllTarget Target(int id, Node n) => new FormatAllTarget { Id = Id(id), Label = "G" + id,
                Bounds = new Box(n.CurrentTop.X,n.CurrentTop.Y,n.Width,n.Height),
                Hierarchy = new HierarchySnapshot { Groups = new[] { new HierarchyGroup { Id = Id(id), Nodes = new[] { n } } } } };
            var good = Target(3, new Node { Id = Id(101), CurrentTop = new Vec2(0,0), Width = 100, Height = 80 });
            var bad = Target(4, new Node { Id = Id(102), CurrentTop = new Vec2(400,0), Width = 100, Height = 80 });
            bad.Hierarchy.Groups[0].Locked = true;
            var r = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { Target(1,shared),Target(2,shared),good,bad } });
            Assert(r.Layout.Success && r.Skipped == 3 && r.Formatted == 1 && r.Layout.Log.Count(l => l.StartsWith("Skipped")) == 3, "skip accounting");
            Assert(r.Layout.Tops.Count == 1 && !r.Layout.Tops.ContainsKey(shared.Id) && r.Layout.Moves.Single().Id == Id(101), "skipped group moved");
            At(r.Layout,3,150,0);
            var none = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { bad } });
            Assert(!none.Layout.Success && none.Skipped == 1 && none.Layout.Moves.Count == 0, "all-failed run accepted");
        });
        Check("Random format-all tidy properties: 1000 fractional seeds, whole translations, clearance, shuffle and repeat", () => {
            for (int seed = 0; seed < 1000; seed++) {
                var random = new Random(seed);
                Box Make() => new Box(random.Next(-100,1000) + random.NextDouble(), random.Next(-100,800) + random.NextDouble(), random.Next(30,180) + random.Next(100) + random.NextDouble(), random.Next(30,180) + random.Next(100) + random.NextDouble());
                var boxes = Enumerable.Range(1, random.Next(2,16)).ToDictionary(Id, _ => Make());
                var obstacles = Enumerable.Range(0, random.Next(5)).Select(_ => Make()).ToArray();
                var savedObstacles = obstacles.Select(b => (b.X,b.Y,b.Width,b.Height)).ToArray();
                var r = GroupTidy.Solve(boxes, obstacles, new Options());
                var again = GroupTidy.Solve(r.GroupBoxes, obstacles, new Options());
                Assert(!r.Unstable && !again.Unstable, "seed " + seed + " unstable: " + Coordinates(r));
                Assert(Coordinates(r) == Coordinates(again), "seed " + seed + " not idempotent: " + Coordinates(r) + " -> " + Coordinates(again));
                var shuffled = GroupTidy.Solve(boxes.OrderBy(_ => random.Next()).ToDictionary(p => p.Key,p => p.Value), obstacles.Reverse().ToArray(), new Options());
                Assert(Coordinates(r) == Coordinates(shuffled), "seed " + seed + " shuffle");
                Assert(savedObstacles.SequenceEqual(obstacles.Select(b => (b.X,b.Y,b.Width,b.Height))), "obstacle mutated");
                foreach (var p in r.GroupBoxes) {
                    Assert(Whole(p.Value.X - boxes[p.Key].X) && Whole(p.Value.Y - boxes[p.Key].Y), "seed " + seed + " fractional translation");
                    Assert(p.Value.X >= boxes[p.Key].X - 5 * 75 - 1e-7 && p.Value.Y >= boxes[p.Key].Y - 5 * 80 - 1e-7,
                        "seed " + seed + " moved beyond five passes' snap/evening allowance");
                }
                Clearance(r,obstacles,"seed " + seed);
            }
        });
        Check("Random format-all composition: 1000 seeds, fractional hierarchy geometry, whole moves, clearance, shuffle and reapply", () => {
            for (int seed = 0; seed < 1000; seed++) {
                var random = new Random(seed); int id = 100;
                Node Make(double x, double y) { var n = new Node { Id = Id(++id), Label = "N" + id,
                    CurrentTop = new Vec2(x,y), Width = random.Next(30,150) + random.NextDouble(), Height = random.Next(20,90) + random.NextDouble(),
                    PivotOffset = new Vec2(random.NextDouble()*10,random.NextDouble()*10),
                    Inputs = new[] { new Vec2(0,10) }, Outputs = new[] { new Vec2(40,10) } };
                    // Fractional bounds and offsets, with whole host pivots.
                    n.CurrentTop = new Vec2(Math.Round(x) - n.PivotOffset.X,Math.Round(y) - n.PivotOffset.Y); return n; }
                var targets = Enumerable.Range(1,random.Next(2,8)).Select(i => {
                    var a = Make(random.Next(1000),random.Next(800)); var b = Make(a.CurrentTop.X + random.Next(100),a.CurrentTop.Y + random.Next(100));
                    var root = new HierarchyGroup { Id = Id(i), Label = "G" + i, Nodes = new[] { a,b },
                        Padding = new Padding(10 + random.NextDouble(),20 + random.NextDouble(),12 + random.NextDouble(),14 + random.NextDouble()) };
                    return new FormatAllTarget { Id = root.Id, Label = root.Label, Bounds = new Box(a.CurrentTop.X,a.CurrentTop.Y,200,150),
                        Hierarchy = new HierarchySnapshot { Groups = new[] { root }, Edges = new[] { new Edge { From = a.Id,To = b.Id } } } };
                }).ToArray();
                var obstacles = Enumerable.Range(0,random.Next(4)).Select(_ => new Box(random.Next(1000),random.Next(800),random.Next(40,180),random.Next(30,100))).ToArray();
                var snapshot = new FormatAllSnapshot { Targets = targets, Obstacles = obstacles };
                var r = FormatAllSolver.Solve(snapshot);
                Assert(r.Layout.Success && !r.Layout.Unstable && r.Skipped == 0, "seed " + seed + " refused: " + Coordinates(r.Layout) + " " + string.Join(";", r.Layout.Log));
                WholeMoves(snapshot,r.Layout,"seed " + seed);
                Clearance(r.Layout,obstacles,"seed " + seed);
                var shuffled = FormatAllSolver.Solve(new FormatAllSnapshot { Obstacles = obstacles.Reverse().ToArray(),
                    Targets = targets.Reverse().Select(t => new FormatAllTarget { Id = t.Id, Label = t.Label, Bounds = t.Bounds,
                        Hierarchy = new HierarchySnapshot { Groups = t.Hierarchy.Groups.Select(g => new HierarchyGroup { Id = g.Id, Label = g.Label,
                            Padding = g.Padding, Nodes = g.Nodes.Reverse().ToArray() }).ToArray(), Edges = t.Hierarchy.Edges.Reverse().ToArray() } }).ToArray() });
                Assert(Coordinates(r.Layout) == Coordinates(shuffled.Layout) && r.Layout.Moves.Select(m => (m.Id,m.Pivot.X,m.Pivot.Y))
                    .SequenceEqual(shuffled.Layout.Moves.Select(m => (m.Id,m.Pivot.X,m.Pivot.Y))), "seed " + seed + " composition shuffle");
                var next = new FormatAllSnapshot { Obstacles = obstacles, Targets = targets.Select(t => new FormatAllTarget {
                    Id = t.Id, Label = t.Label, Bounds = r.Layout.GroupBoxes[t.Id], Hierarchy = Applied(t.Hierarchy,r.Layout) }).ToArray() };
                var again = FormatAllSolver.Solve(next);
                Assert(again.Layout.Success && !again.Layout.Unstable && again.Layout.Moves.Count == 0,
                    "seed " + seed + " composition not fixed: " + Coordinates(r.Layout) + " -> " + Coordinates(again.Layout));
            }
        });
        return failures;
    }

    private static HierarchySnapshot Applied(HierarchySnapshot s, Result r)
    {
        HierarchyGroup Group(HierarchyGroup g) => new HierarchyGroup { Id = g.Id, Label = g.Label, Padding = g.Padding, Locked = g.Locked,
            Nodes = g.Nodes.Select(n => new Node { Id = n.Id, Label = n.Label, CurrentTop = r.Tops[n.Id], PivotOffset = n.PivotOffset,
                Kind = n.Kind, Widget = n.Widget, Width = n.Width, Height = n.Height, Inputs = n.Inputs, Outputs = n.Outputs }).ToArray(),
            NonNodes = g.NonNodes.Select(n => new NonNodeMember { Id = n.Id, Label = n.Label, PivotOffset = n.PivotOffset,
                Bounds = new Box(r.Tops[n.Id].X,r.Tops[n.Id].Y,n.Bounds.Width,n.Bounds.Height) }).ToArray(), Children = g.Children.Select(Group).ToArray() };
        return new HierarchySnapshot { Groups = s.Groups.Select(Group).ToArray(), Edges = s.Edges, Inbound = s.Inbound, Outbound = s.Outbound, Options = s.Options };
    }
}
