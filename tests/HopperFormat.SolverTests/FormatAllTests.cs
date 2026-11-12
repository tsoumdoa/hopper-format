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
        Check("Format all inner solves retain distinct island left edges while stripping outside X", () => {
            var a = TestNode(1, 50, 0); var b = TestNode(2, 200, 80);
            var c = TestNode(3, 550, 200); var d = TestNode(4, 700, 280);
            var root = new HierarchyGroup { Id = Id(100), Nodes = new[] { a, b, c, d } };
            var external = new ExternalOutbound { Node = d.Id, TargetGripX = 0 };
            var h = new HierarchySnapshot { Groups = new[] { root }, Outbound = new[] { external },
                Edges = new[] { new Edge { From = a.Id, To = b.Id }, new Edge { From = c.Id, To = d.Id } } };
            var s = new FormatAllSnapshot { Targets = new[] { new FormatAllTarget { Id = root.Id,
                Bounds = new Box(50, 0, 730, 320), Hierarchy = h } } };
            var r = FormatAllSolver.Solve(s).Layout;
            Assert(r.Success && !r.Unstable && Math.Abs(r.Tops[c.Id].X - r.Tops[a.Id].X - 500) < 1e-7,
                "inner islands shared a left edge");
            Assert(external.TargetGripX == 0, "captured outside X mutated");
            StableAll(s, r, "distinct island left edges"); WholeMoves(s, r, "distinct island left edges");
        });
        Check("Format all ignores outside X during inner solves without mutating captured geometry", () => {
            var chain = Enumerable.Range(1, 4).Select(i => TestNode(i, (i - 1) * 100, i * 60)).ToArray();
            var early = TestNode(5, 100, 0);
            Edge E(Node a, Node b) => new Edge { From = a.Id, To = b.Id };
            var root = new HierarchyGroup { Id = Id(100), Nodes = chain.Concat(new[] { early }).ToArray() };
            var external = new ExternalOutbound { Node = early.Id };
            var h = new HierarchySnapshot { Groups = new[] { root }, Outbound = new[] { external },
                Edges = new[] { E(chain[0], chain[1]), E(chain[1], chain[2]), E(chain[2], chain[3]), E(chain[0], early) } };
            var s = new FormatAllSnapshot { Targets = new[] { new FormatAllTarget { Id = root.Id,
                Bounds = new Box(0, 0, 380, 280), Hierarchy = h } } };
            var legacy = FormatAllSolver.Solve(s).Layout;
            external.TargetGripX = 230;
            var r = FormatAllSolver.Solve(s).Layout;
            Assert(r.Success && legacy.Success && r.Log.SequenceEqual(legacy.Log) && r.Tops.All(p =>
                p.Value.X == legacy.Tops[p.Key].X && p.Value.Y == legacy.Tops[p.Key].Y), "outside X changed format-all output");
            Assert(external.TargetGripX == 230, "format all mutated outside X");
        });
        Check("Format-all skipped diagnostics consume complete group descriptions and include root context once", () => {
            const string label = "group [A, B, C, …] at (120, 340)";
            var root = new HierarchyGroup { Id = Id(1), Label = label, Nodes = new[] { TestNode(100, 130, 360) }, Padding = new Padding(-1, 0, 0, 0) };
            var target = new FormatAllTarget { Id = root.Id, Label = label, Bounds = new Box(120, 340, 100, 80),
                Hierarchy = new HierarchySnapshot { Groups = new[] { root } } };
            var result = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { target } });
            Assert(result.Layout.Log.Contains("Skipped " + label + ": invalid padding. (moves rigidly)"), "root context repeated or requoted");
            target.Label = root.Label = "'Name'";
            result = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { target } });
            Assert(result.Layout.Log.Contains("Skipped 'Name': invalid padding. (moves rigidly)"), "custom description was requoted");
            root.Padding = default; root.Nodes = Array.Empty<Node>(); target.Label = root.Label = "empty group at (120, 340)";
            result = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { target } });
            Assert(result.Layout.Log.Contains("Skipped empty group at (120, 340): no measurable members (moves rigidly)"), "empty group phrasing repeated");
        });
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
            At(r,1,0,120); Assert(obstacles[0].X == 100 && obstacles[1].X == 230, "obstacle moved");
        });
        Check("Format all bottom clearance: group=(0,170), component=(100,90), row pushes down", () => {
            var obstacle = new Box(100,90,80,40);
            var r = Tidy(new[] { new Box(0,0,160,80) }, obstacle);
            At(r,1,0,170); Assert(obstacle.X == 100 && obstacle.Y == 90, "component moved");
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
        Check("Format all fractional collision push: group=(0.3,171.4), downward clearance=40.7", () => {
            var obstacle = new Box(110.8,90.5,70.6,40.2);
            var r = Tidy(new[] { new Box(.3,.4,100.2,80.3) },obstacle);
            At(r,1,.3,171.4); Clearance(r,new[] { obstacle },"fractional push");
            Assert(Whole(r.GroupBoxes[Id(1)].Y - .4) && !r.Unstable,"fractional push translation");
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
            var first = Target(1,shared); var second = Target(2,shared);
            first.Hierarchy.Groups[0].Nodes = new[] { shared, new Node { Id = Id(103), Width = 20, Height = 20 } };
            second.Hierarchy.Groups[0].Nodes = new[] { shared, new Node { Id = Id(104), Width = 20, Height = 20 } };
            var r = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { first,second,good,bad } });
            Assert(r.Layout.Success && r.Skipped == 3 && r.Formatted == 1 && r.Layout.Log.Count(l => l.StartsWith("Skipped")) == 3, "skip accounting");
            Assert(r.Layout.Tops.Count == 1 && !r.Layout.Tops.ContainsKey(shared.Id) && r.Layout.Moves.Single().Id == Id(101), "skipped group moved");
            At(r.Layout,3,0,120);
            var none = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { bad } });
            Assert(!none.Layout.Success && none.Skipped == 1 && none.Layout.Moves.Count == 0, "all-failed run accepted");
        });
        Check("Owner stack: Cut, Plate and Bolt keep their left edge; growth pushes Bolt down", () => {
            FormatAllTarget Stack(int id, string name, double y, bool connected) {
                var a = TestNode(100 + id * 10,10,y + 20); var b = TestNode(101 + id * 10,20,y + 30);
                var label = new HierarchyGroup { Id = Id(300 + id), Nodes = new[] { a } };
                var child = new HierarchyGroup { Id = Id(200 + id), Nodes = new[] { b }, Children = new[] { label } };
                var root = new HierarchyGroup { Id = Id(id), Label = name, Children = new[] { child }, Padding = new Padding(10,20,10,20) };
                return new FormatAllTarget { Id = root.Id, Label = name, Bounds = new Box(0,y,110,90),
                    Hierarchy = new HierarchySnapshot { Groups = new[] { root }, Edges = connected ? new[] { new Edge { From = a.Id,To = b.Id } } : Array.Empty<Edge>() } };
            }
            var targets = new[] { Stack(1,"Cut",0,true),Stack(2,"Plate",130,false),Stack(3,"Bolt",260,true),
                new FormatAllTarget { Id = Id(4), Label = "Right of Cut", Bounds = new Box(160,0,100,80), Hierarchy = new HierarchySnapshot {
                    Groups = new[] { new HierarchyGroup { Id = Id(4), Nodes = new[] { TestNode(150,170,20) }, Padding = new Padding(10,20,10,20) } } } } };
            var s = new FormatAllSnapshot { Targets = targets };
            var outcome = FormatAllSolver.Solve(s); var r = outcome.Layout;
            Assert(r.Success && !r.Unstable && outcome.Formatted == 4 && outcome.Skipped == 0,"stack skipped a root");
            At(r,1,0,0); At(r,2,0,120); At(r,3,0,296); At(r,4,260,0);
            Assert(r.GroupBoxes[Id(2)].Height > targets[1].Bounds.Height && r.GroupBoxes[Id(3)].Y > targets[2].Bounds.Y,"Plate did not grow into Bolt");
            var roots = new Result { GroupBoxes = targets.ToDictionary(t => t.Id,t => r.GroupBoxes[t.Id]) };
            Clearance(roots,Array.Empty<Box>(),"owner stack"); WholeMoves(s,r,"owner stack");
            StableAll(s,r,"owner stack");
            Console.WriteLine("PASS Owner-stack coordinates: Cut=(0,0,210,80); Plate=(0,120,100,136); Bolt=(0,296,210,80); Right=(260,0,100,80)");
        });
        Check("No far jump: inner growth into an ungrouped component pushes the row down", () => {
            var root = new HierarchyGroup { Id = Id(1), Nodes = new[] { TestNode(100,10,20),TestNode(101,20,30) }, Padding = new Padding(10,20,10,20) };
            var obstacle = new Box(20,110,80,50);
            var s = new FormatAllSnapshot { Targets = new[] { new FormatAllTarget { Id = root.Id, Bounds = new Box(0,0,110,90),
                Hierarchy = new HierarchySnapshot { Groups = new[] { root } } } }, Obstacles = new[] { obstacle } };
            var r = FormatAllSolver.Solve(s).Layout;
            At(r,1,0,200); Assert(r.GroupBoxes[Id(1)].Height == 136,"fixture did not grow");
            Clearance(r,s.Obstacles,"no far jump"); StableAll(s,r,"no far jump");
        });
        Check("Format all label overlap follows its node without a second owner or pivot", () => {
            var a = TestNode(100,10,20); var b = TestNode(101,20,30);
            var root = new HierarchyGroup { Id = Id(1), Nodes = new[] { a,b }, Padding = new Padding(10,20,10,20) };
            var label = new HierarchyGroup { Id = Id(2), Nodes = new[] { b }, Padding = new Padding(3,4,5,6) };
            var s = new FormatAllSnapshot { Targets = new[] { new FormatAllTarget { Id = root.Id, Hierarchy = new HierarchySnapshot {
                Groups = new[] { root }, Edges = new[] { new Edge { From = a.Id,To = b.Id } } } },
                new FormatAllTarget { Id = label.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { label } } } } };
            var r = FormatAllSolver.Solve(s);
            Assert(r.Layout.Success && r.Formatted == 2 && r.Skipped == 0 && r.Layout.Tops.Count == 2,"label overlap refused or duplicated a node");
            Assert(r.Layout.GroupBoxes[label.Id].X == r.Layout.Tops[b.Id].X - 3,"label did not follow node");
            StableAll(s,r.Layout,"label overlap");
        });
        Check("Skipped inner solve moves rigidly with untouched relative contents and whole deltas", () => {
            var a = TestNode(100,10,20); var b = TestNode(101,20,30);
            var root = new HierarchyGroup { Id = Id(1), Nodes = new[] { a,b }, Padding = new Padding(10,20,10,20) };
            var s = new FormatAllSnapshot { Targets = new[] { new FormatAllTarget { Id = root.Id, Label = "Rigid", Bounds = new Box(0,0,110,90),
                Hierarchy = new HierarchySnapshot { Groups = new[] { root }, Edges = new[] { new Edge { From = a.Id,To = b.Id,ToPort = 99 } } } } }, Obstacles = new[] { new Box(0,100,80,40) } };
            var r = FormatAllSolver.Solve(s);
            Assert(r.Layout.Success && r.Formatted == 0 && r.Skipped == 1 && r.Layout.Log.Any(l => l.Contains("moves rigidly")),"skipped root was fixed or refused");
            At(r.Layout,1,0,180);
            Assert(r.Layout.Tops[b.Id].X - r.Layout.Tops[a.Id].X == 10 && r.Layout.Tops[b.Id].Y - r.Layout.Tops[a.Id].Y == 10,"rigid contents rearranged");
            WholeMoves(s,r.Layout,"rigid"); Clearance(r.Layout,s.Obstacles,"rigid");
            StableAll(s,r.Layout,"rigid root");
        });
        Check("Malformed root geometry refuses its inner solve without stopping healthy roots", () => {
            var bad = TestNode(100,0,0); bad.Width = double.NaN;
            var good = TestNode(101,0,0);
            var s = new FormatAllSnapshot { Targets = new[] {
                new FormatAllTarget { Id = Id(1), Bounds = new Box(0,0,double.NaN,80), Hierarchy = new HierarchySnapshot {
                    Groups = new[] { new HierarchyGroup { Id = Id(1),Nodes = new[] { bad } } } } },
                new FormatAllTarget { Id = Id(2), Hierarchy = new HierarchySnapshot {
                    Groups = new[] { new HierarchyGroup { Id = Id(2),Nodes = new[] { good } } } } } } };
            var r = FormatAllSolver.Solve(s);
            Assert(r.Layout.Success && !r.Layout.Unstable && r.Skipped == 1 && r.Formatted == 1 && !r.Layout.Tops.ContainsKey(bad.Id),"malformed root stopped healthy root");
        });
        Check("Random format-all recovery: 300 seeds, nested cycles, empty groups, label overlaps and fixed obstacles", () => {
            for (int seed = 0; seed < 300; seed++) {
                var random = new Random(seed);
                var targets = Enumerable.Range(1,3).Select(i => {
                    var a = TestNode(100 + i*10,random.Next(500),random.Next(500));
                    var b = TestNode(101 + i*10,random.Next(500),random.Next(500));
                    var c = TestNode(102 + i*10,random.Next(500),random.Next(500));
                    var root = new HierarchyGroup { Id = Id(i), Label = "Root" + i, Nodes = new[] { b },
                        Children = new[] { new HierarchyGroup { Id = Id(200+i), Label = "Child" + i, Nodes = new[] { a,c } },
                            new HierarchyGroup { Id = Id(300+i), Nodes = new[] { a } }, new HierarchyGroup { Id = Id(400+i) } } };
                    return new FormatAllTarget { Id = root.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { root },
                        Edges = new[] { new Edge { From = a.Id,To = b.Id },new Edge { From = b.Id,To = c.Id } } } };
                }).ToArray();
                var obstacles = new[] { new Box(random.Next(500),random.Next(500),100,80) };
                var s = new FormatAllSnapshot { Targets = targets, Obstacles = obstacles };
                var r = FormatAllSolver.Solve(s);
                Assert(r.Layout.Success && !r.Layout.Unstable && r.Skipped == 0 && r.Layout.Log.Count(l => l.Contains("its wires leave and re-enter it")) == 3,"seed " + seed + " recovery failed");
                WholeMoves(s,r.Layout,"recovery " + seed);
                Clearance(new Result { GroupBoxes = targets.ToDictionary(t => t.Id,t => r.Layout.GroupBoxes[t.Id]) },obstacles,"recovery " + seed);
                var again = FormatAllSolver.Solve(Reapply(s,r.Layout));
                Assert(again.Layout.Success && !again.Layout.Unstable && again.Layout.Moves.Count == 0,"seed " + seed + " recovery not fixed");
                var shuffle = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = targets.Reverse().ToArray(), Obstacles = obstacles });
                Assert(Coordinates(r.Layout) == Coordinates(shuffle.Layout) && r.Layout.Log.SequenceEqual(shuffle.Layout.Log),"seed " + seed + " recovery shuffle");
            }
        });
        Check("Random vertical stacks: 300 seeds, only same-row neighbours move right and whole rows clear obstacles downward", () => {
            for (int seed = 0; seed < 300; seed++) {
                var random = new Random(seed); int count = random.Next(3,10);
                var boxes = Enumerable.Range(1,count).ToDictionary(Id,i => new Box(i == 1 ? .3 : random.Next(20) + .3,(i-1)*120 + .4,
                    random.Next(80,220) + .2,random.Next(80,240) + .3));
                boxes.Add(Id(100),new Box(180.3,.9,90.2,80.3));
                var obstacles = new[] { new Box(0,random.Next(50,500),random.Next(80,250),random.Next(30,100)) };
                var saved = obstacles.Select(b => (b.X,b.Y,b.Width,b.Height)).ToArray();
                var r = GroupTidy.Solve(boxes,obstacles,new Options());
                Assert(r.Success && !r.Unstable,"stack seed " + seed + " unstable");
                foreach (var id in boxes.Keys.Where(id => id != Id(100)))
                    Assert(Math.Abs(r.GroupBoxes[id].X - .3) < 1e-7,"stack seed " + seed + " row jumped right");
                Assert(Math.Abs(r.GroupBoxes[Id(1)].Y - r.GroupBoxes[Id(100)].Y) < 1,"whole row separated");
                foreach (var pair in r.GroupBoxes) Assert(Whole(pair.Value.X-boxes[pair.Key].X) && Whole(pair.Value.Y-boxes[pair.Key].Y),"stack fractional delta");
                Assert(saved.SequenceEqual(obstacles.Select(b => (b.X,b.Y,b.Width,b.Height))),"stack moved fixed obstacle");
                Clearance(r,obstacles,"stack " + seed);
                var again = GroupTidy.Solve(r.GroupBoxes,obstacles,new Options());
                var shuffle = GroupTidy.Solve(boxes.Reverse().ToDictionary(p => p.Key,p => p.Value),obstacles,new Options());
                Assert(!again.Unstable && Coordinates(r) == Coordinates(again) && Coordinates(r) == Coordinates(shuffle),"stack not fixed or deterministic");
            }
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
        Check("Shared note between a label and a big target has one deterministic owner", () => {
            foreach (bool alias in new[] { false, true }) {
                var a = TestNode(100,10,20); var b = TestNode(101,20,30); var c = TestNode(102,300,50);
                var note = new NonNodeMember { Id = Id(500), Label = "Shared note", Bounds = new Box(50,90,20,15) };
                var big = new HierarchyGroup { Id = Id(1), Nodes = new[] { a,b }, NonNodes = new[] { note } };
                var label = new HierarchyGroup { Id = Id(2), Nodes = new[] { alias ? b : c }, NonNodes = new[] { note } };
                var targets = new[] { new FormatAllTarget { Id = big.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { big } } },
                    new FormatAllTarget { Id = label.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { label } } } };
                var snapshot = new FormatAllSnapshot { Targets = targets };
                var r = FormatAllSolver.Solve(snapshot);
                Assert(r.Layout.Success && r.Formatted == 2 && r.Skipped == 0 && r.Layout.Tops.Count == (alias ? 3 : 4),"shared note refused or duplicated");
                Assert(r.Layout.Log.Any(l => l.Contains("Shared note") && l.Contains("later owner ignored")),"duplicate owner not logged");
                Assert(r.Layout.Moves.Select(m => m.Id).Distinct().Count() == r.Layout.Moves.Count,"note moved twice");
                var reversed = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = targets.Reverse().ToArray() });
                Assert(Coordinates(r.Layout) == Coordinates(reversed.Layout) && r.Layout.Log.SequenceEqual(reversed.Layout.Log),"owner depends on enumeration");
                WholeMoves(snapshot,r.Layout,"shared note");
                if (alias) StableAll(snapshot,r.Layout,"shared note alias");
            }
        });
        Check("Two label children sharing a note translate it with the first owning unit", () => {
            var a = TestNode(100,10,20); var b = TestNode(101,20,30);
            var note = new NonNodeMember { Id = Id(500), Label = "Shared note", Bounds = new Box(50,90,20,15) };
            var children = new[] { new HierarchyGroup { Id = Id(2), Nodes = new[] { a }, NonNodes = new[] { note } },
                new HierarchyGroup { Id = Id(3), Nodes = new[] { b }, NonNodes = new[] { note } } };
            var root = new HierarchyGroup { Id = Id(1), Children = children };
            var snapshot = new FormatAllSnapshot { Targets = new[] { new FormatAllTarget { Id = root.Id,
                Hierarchy = new HierarchySnapshot { Groups = new[] { root } } } } };
            var r = FormatAllSolver.Solve(snapshot).Layout;
            Assert(r.Success && r.Tops.Count == 3 && r.Log.Any(l => l.Contains("later owner ignored")),"shared child note failed or unlogged");
            Assert(r.Tops[note.Id].X - r.Tops[a.Id].X == 40 && r.Tops[note.Id].Y - r.Tops[a.Id].Y == 70,"note translated by both children");
            root.Children = children.Reverse().ToArray();
            var reversed = FormatAllSolver.Solve(snapshot).Layout;
            Assert(Coordinates(r) == Coordinates(reversed) && r.Log.SequenceEqual(reversed.Log),"child owner depends on enumeration");
            StableAll(snapshot,r,"shared child note");
        });
        Check("Unexpected target exception skips rigidly while healthy targets format", () => {
            var bad = new FormatAllTarget { Id = Id(1), Label = "Throws", Bounds = new Box(0,0,80,40),
                Hierarchy = new HierarchySnapshot { Groups = new[] { new HierarchyGroup { Id = Id(1), Nodes = new ThrowingList<Node>() } } },
                RigidMembers = new[] { new NonNodeMember { Id = Id(100), Bounds = new Box(0,0,80,40) } } };
            var good = new FormatAllTarget { Id = Id(2), Hierarchy = new HierarchySnapshot {
                Groups = new[] { new HierarchyGroup { Id = Id(2), Nodes = new[] { TestNode(101,10,100),TestNode(102,20,110) } } } } };
            var r = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { bad,good } });
            Assert(r.Layout.Success && r.Formatted == 1 && r.Skipped == 1 && r.Layout.Tops.Count == 3
                && r.Layout.GroupBoxes.ContainsKey(bad.Id) && r.Layout.Log.Count(l => l.StartsWith("Skipped")) == 1
                && r.Layout.Log.Any(l => l.Contains("injected enumeration failure")),"one target exception aborted the run");
            var inner = HierarchySolver.Solve(bad.Hierarchy);
            Assert(!inner.Success && inner.FailureKind == FailureKind.Other && inner.Diagnostic == "injected enumeration failure","unexpected hierarchy exception escaped");
        });
        Check("Typed hierarchy overlap and lock failures keep format-all targets fixed", () => {
            var shared = TestNode(100,10,20);
            foreach (bool locked in new[] { false,true }) {
                var left = new HierarchyGroup { Id = Id(3), Label = "'child'", Nodes = new[] { shared,TestNode(101,20,30) }, Locked = locked };
                var right = new HierarchyGroup { Id = Id(4), Nodes = new[] { shared,TestNode(102,20,30) } };
                var root = new HierarchyGroup { Id = Id(1), Label = "'root'", Children = locked ? new[] { left } : new[] { left,right } };
                var hierarchy = new HierarchySnapshot { Groups = new[] { root } };
                var inner = HierarchySolver.Solve(hierarchy);
                Assert(inner.FailureKind == (locked ? FailureKind.Locked : FailureKind.Overlap),"missing typed reason");
                var bad = new FormatAllTarget { Id = root.Id, Label = root.Label, Bounds = new Box(0,0,80,40), Hierarchy = hierarchy };
                var good = new FormatAllTarget { Id = Id(2), Hierarchy = new HierarchySnapshot {
                    Groups = new[] { new HierarchyGroup { Id = Id(2), Nodes = new[] { TestNode(103,0,100) } } } } };
                var r = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { bad,good } });
                Assert(r.Layout.Success && r.Skipped == 1 && !r.Layout.GroupBoxes.ContainsKey(bad.Id)
                    && r.Layout.Log.Any(l => l.EndsWith("(fixed)")),"typed refusal moved rigidly");
            }
        });
        Check("Status words in group labels do not create warnings or dissolved counts", () => {
            var root = new HierarchyGroup { Id = Id(1), Label = "'Unstable: its wires leave and re-enter it'",
                Nodes = new[] { TestNode(100,10,20),TestNode(101,20,30) } };
            var r = FormatAllSolver.Solve(new FormatAllSnapshot { Targets = new[] { new FormatAllTarget {
                Id = root.Id, Label = root.Label, Hierarchy = new HierarchySnapshot { Groups = new[] { root } } } } }).Layout;
            Assert(r.Success && !r.Unstable && r.DissolvedCount == 0 && r.Notices.Count == 1
                && r.Notices.Single() == "Format all: 1 formatted, 0 skipped, 0 dissolved"
                && r.Log.Any(l => l.StartsWith(root.Label + ": ")),"message text changed typed log routing or counts");
        });
        Check("Fractional alias pivots give notes whole translations and repeat without moves", () => {
            var a = TestNode(100,10.3,20.4); var b = TestNode(101,20.3,30.4);
            var note = new NonNodeMember { Id = Id(500), Bounds = new Box(150.2,100.6,20,15), PivotOffset = new Vec2(.7,.8) };
            var big = new HierarchyGroup { Id = Id(1), Nodes = new[] { a,b } };
            var label = new HierarchyGroup { Id = Id(2), Nodes = new[] { b }, NonNodes = new[] { note } };
            var snapshot = new FormatAllSnapshot { Targets = new[] {
                new FormatAllTarget { Id = big.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { big } } },
                new FormatAllTarget { Id = label.Id, Hierarchy = new HierarchySnapshot { Groups = new[] { label } } } } };
            var r = FormatAllSolver.Solve(snapshot).Layout;
            Assert(r.Success && Whole(r.Tops[note.Id].X - note.Bounds.X) && Whole(r.Tops[note.Id].Y - note.Bounds.Y),"alias note took fractional translation");
            StableAll(snapshot,r,"fractional alias");
            var next = Reapply(snapshot,r);
            var noOp = FormatAllSolver.Solve(next).Layout;
            Assert(!noOp.Moves.Any(m => m.Id == note.Id),"alias note recorded a no-op");
        });
        return failures;
    }

    private sealed class ThrowingList<T> : System.Collections.Generic.IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index] => throw new ArgumentException("injected enumeration failure");
        public System.Collections.Generic.IEnumerator<T> GetEnumerator() => throw new ArgumentException("injected enumeration failure");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void StableAll(FormatAllSnapshot s, Result r, string context)
    {
        var again = FormatAllSolver.Solve(Reapply(s,r)).Layout;
        Assert(again.Success && !again.Unstable && again.Moves.Count == 0,context + " not fixed: " + again.Diagnostic + " " + Coordinates(again));
    }
    private static Node TestNode(int id, double x, double y) => new Node { Id = Id(id), CurrentTop = new Vec2(x,y), Width = 80,Height = 40,
        Inputs = new[] { new Vec2(0,10) }, Outputs = new[] { new Vec2(80,10) } };
    private static FormatAllSnapshot Reapply(FormatAllSnapshot s, Result r) => new FormatAllSnapshot { Options = s.Options, Obstacles = s.Obstacles,
        Targets = s.Targets.Select(t => new FormatAllTarget { Id = t.Id, Label = t.Label, Bounds = r.GroupBoxes[t.Id], Fixed = t.Fixed,
            SkipReason = t.SkipReason, Hierarchy = Applied(t.Hierarchy,r) }).ToArray() };

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
