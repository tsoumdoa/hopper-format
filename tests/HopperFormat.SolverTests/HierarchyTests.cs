using System;
using System.Collections.Generic;
using System.Linq;
using HopperFormat.Layout;

internal static class HierarchyTests
{
    private static int failures;
    private static Guid Id(int i) => new Guid(i, 0, 0, new byte[8]);
    private static Node N(int i, double x = 0, double y = 0, double width = 80, double height = 40) => new Node {
        Id = Id(i), Label = "N" + i, CurrentTop = new Vec2(x, y), Width = width, Height = height,
        PivotOffset = new Vec2(7, 9), Inputs = new[] { new Vec2(0, 12) }, Outputs = new[] { new Vec2(width, height - 10) } };
    private static Edge E(Node a, Node b, int input = 0, int output = 0) => new Edge {
        From = a.Id, To = b.Id, FromPort = output, ToPort = input };
    private static HierarchyGroup G(int id, Node[] nodes, params HierarchyGroup[] children) => new HierarchyGroup {
        Id = Id(id), Label = "'G" + id + "'", Nodes = nodes, Children = children, Padding = new Padding(10, 20, 12, 14) };
    private static IEnumerable<Node> Nodes(HierarchyGroup g) => g.Nodes.Concat(g.Children.SelectMany(Nodes)).GroupBy(n => n.Id).Select(a => a.First());
    private static IEnumerable<HierarchyGroup> Groups(HierarchyGroup g) => new[] { g }.Concat(g.Children.SelectMany(Groups));
    // Fixtures never share a labelled node with an unrelated group, so notes alone decide.
    private static bool SeeThrough(HierarchyGroup g) => Nodes(g).Count() == 1 && !Groups(g).SelectMany(c => c.NonNodes).Any();
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Near(double a, double b, string message) => Assert(Math.Abs(a - b) < 1e-7, message + ": " + a + " != " + b);
    private static void Check(string name, Action test)
    { try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    private static Result Solve(HierarchySnapshot s)
    { var r = HierarchySolver.Solve(s); Assert(r.Success, r.Diagnostic); Assert(!r.Unstable, string.Join(" ", r.Log)); return r; }
    private static Node AppliedNode(Node n, Result r) => new Node { Id = n.Id, Label = n.Label, Kind = n.Kind, Widget = n.Widget,
        CurrentTop = r.Tops[n.Id], PivotOffset = n.PivotOffset, Width = n.Width, Height = n.Height, Inputs = n.Inputs, Outputs = n.Outputs };
    private static HierarchySnapshot Applied(HierarchySnapshot s, Result r, Random shuffle = null)
    {
        T[] Order<T>(IEnumerable<T> items) => shuffle == null ? items.ToArray() : items.OrderBy(_ => shuffle.Next()).ToArray();
        HierarchyGroup Copy(HierarchyGroup g) => new HierarchyGroup { Id = g.Id, Label = g.Label, Padding = g.Padding, Locked = g.Locked,
            Nodes = Order(g.Nodes.Select(n => AppliedNode(n, r))), Children = Order(g.Children.Select(Copy)),
            NonNodes = Order(g.NonNodes.Select(n => new NonNodeMember { Id = n.Id, Label = n.Label, PivotOffset = n.PivotOffset,
                Bounds = new Box(r.Tops[n.Id].X, r.Tops[n.Id].Y, n.Bounds.Width, n.Bounds.Height) })) };
        return new HierarchySnapshot { Groups = Order(s.Groups.Select(Copy)), LooseNodes = Order(s.LooseNodes.Select(n => AppliedNode(n, r))),
            Edges = Order(s.Edges), Inbound = Order(s.Inbound), Outbound = Order(s.Outbound), Options = s.Options };
    }
    private static void Golden(Result r, params (int Id, double X, double Y)[] expected)
    {
        foreach (var p in expected) { Near(r.Tops[Id(p.Id)].X, p.X, "golden X " + p.Id); Near(r.Tops[Id(p.Id)].Y, p.Y, "golden Y " + p.Id); }
    }
    private static string Fingerprint(Result r) => string.Join(";", r.Tops.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value.X + "," + p.Value.Y));
    private static void Stable(HierarchySnapshot s, Result r)
    { var again = Solve(Applied(s, r)); Assert(again.Moves.Count == 0, "second FF moved " + string.Join(",", again.Moves.Select(m => m.Id + "=" + m.Pivot.X + "," + m.Pivot.Y + " (top delta " + (again.Tops[m.Id].X - r.Tops[m.Id].X) + "," + (again.Tops[m.Id].Y - r.Tops[m.Id].Y) + ")"))); }
    private static void Coordinates(string label, HierarchySnapshot s, Result r) => Console.WriteLine("PASS " + label + ": " +
        string.Join("; ", s.Groups.SelectMany(Nodes).Concat(s.LooseNodes).OrderBy(n => n.Id).Select(n => n.Label + "=(" + r.Tops[n.Id].X + "," + r.Tops[n.Id].Y + ")")));

    internal static int Run()
    {
        Check("Outside X preserves layouts at the sole root, nested child and moving sibling levels", () => {
            var chain = Enumerable.Range(1, 4).Select(i => N(i, (i - 1) * 100, i * 60)).ToArray();
            var early = N(5, 100, 0);
            var child = G(101, chain.Concat(new[] { early }).ToArray());
            var e = new ExternalOutbound { Node = early.Id, TargetGripY = 0 };
            var s = new HierarchySnapshot { Groups = new[] { child }, Outbound = new[] { e },
                Edges = new[] { E(chain[0], chain[1]), E(chain[1], chain[2]), E(chain[2], chain[3]), E(chain[0], early) } };
            var legacy = Solve(s);
            e.TargetGripX = 230;
            var withX = Solve(s);
            Assert(Fingerprint(withX) == Fingerprint(legacy), "outside X changed sole-root output");
            Stable(s, withX);
            s.Groups = new[] { G(100, Array.Empty<Node>(), child) };
            var nested = Solve(s); e.TargetGripX = null;
            Assert(Fingerprint(nested) == Fingerprint(Solve(s)), "outside X changed nested-child output");
            // A wire crossing only a child boundary receives a Y hint, even
            // though the real sibling's input X is available in the flat graph.
            var sibling = N(6, 230, 100);
            s.Groups = new[] { G(100, new[] { sibling }, child) };
            s.Outbound = Array.Empty<ExternalOutbound>();
            s.Edges = s.Edges.Concat(new[] { E(early, sibling) }).ToArray();
            var r = Solve(s); Stable(s, r);
            Near(r.Tops[early.Id].X, r.Tops[chain[3].Id].X, "child's terminal pin must remain");
        });
        Check("Outside X preserves the top Selection layout and moving root layouts", () => {
            var chain = Enumerable.Range(1, 4).Select(i => N(i, (i - 1) * 100, i * 60)).ToArray();
            var early = N(5, 100, 0);
            var s = new HierarchySnapshot { Groups = new[] { G(100, new[] { chain[0] }) },
                LooseNodes = chain.Skip(1).Concat(new[] { early }).ToArray(),
                Edges = new[] { E(chain[0], chain[1]), E(chain[1], chain[2]), E(chain[2], chain[3]), E(chain[0], early) },
                Outbound = new[] { new ExternalOutbound { Node = early.Id, TargetGripX = 230 } } };
            var r = Solve(s);
            s.Outbound[0].TargetGripX = null;
            Assert(Fingerprint(r) == Fingerprint(Solve(s)), "outside X changed Selection output"); Stable(s, r);
            s.Outbound[0].TargetGripX = 230;
            s.Groups = new[] { G(100, chain.Concat(new[] { early }).ToArray()) };
            s.LooseNodes = new[] { N(6, 500, 0) };
            r = Solve(s);
            Near(r.Tops[early.Id].X, r.Tops[chain[3].Id].X, "moving root ran inner guard"); Stable(s, r);
        });
        Check("Hierarchy nested group solves bottom-up and composes each pivot once", () => {
            var n = Enumerable.Range(1, 5).Select(i => N(i, i * 130, 300 - i * 40)).ToArray();
            var child = G(101, n.Skip(1).Take(3).ToArray()); var outer = G(100, new[] { n[0], n[4] }, child);
            var s = new HierarchySnapshot { Groups = new[] { outer }, Edges = Enumerable.Range(0, 4).Select(i => E(n[i], n[i + 1])).ToArray() };
            var r = Solve(s); Stable(s, r); Geometry(s, r);
            Assert(r.Moves.Select(m => m.Id).Distinct().Count() == r.Moves.Count && r.Moves.All(m => n.Any(a => a.Id == m.Id)), "uncomposed move");
            Golden(r, (1, 130, 102), (2, 270, 120), (3, 380, 138), (4, 490, 156), (5, 632, 174));
            Coordinates("Nested group coordinates", s, r);
        });
        Check("Hierarchy label groups preserve every phase-one interface coordinate", () => {
            var s = Interface(false); var flat = new Snapshot { Nodes = s.Groups.SelectMany(Nodes).OrderBy(n => n.Id).ToArray(),
                Edges = s.Edges, Inbound = s.Inbound, Outbound = s.Outbound };
            var expected = LayoutSolver.Solve(flat); var r = Solve(s);
            foreach (var n in flat.Nodes) { Near(r.Tops[n.Id].X, expected.Tops[n.Id].X, n.Label + " x"); Near(r.Tops[n.Id].Y, expected.Tops[n.Id].Y, n.Label + " y"); }
            Stable(s, r); Coordinates("Label-only interface coordinates", s, r);
            s = Interface(true); r = Solve(s); Stable(s, r); Geometry(s, r);
            Golden(r, (1, 70, 192), (2, 602, 192), (3, 200, 20), (4, 320, 58), (5, 602, 120), (6, 482, 79), (7, 602, 84), (8, 602, 156), (9, 0, 228));
            Coordinates("Grouped interface coordinates", s, r);
        });
        Check("Hierarchy two selected groups have equal tops and exact GroupGapX", () => {
            var a = new[] { N(1, 30, 60), N(2, 180, 100) }; var b = new[] { N(3, 450, 30), N(4, 600, 90) };
            var ga = G(100, a); var gb = G(101, b);
            var s = new HierarchySnapshot { Groups = new[] { ga, gb }, Edges = new[] { E(a[0], a[1]), E(a[1], b[0]), E(b[0], b[1]) } };
            var r = Solve(s); var x = r.GroupBoxes[ga.Id]; var y = r.GroupBoxes[gb.Id];
            Near(x.Y, y.Y, "group tops"); Near(y.X - x.Right, 50, "group gap"); Near(x.X, 20, "selection anchor x"); Near(x.Y, 10, "selection anchor y");
            Stable(s, r); Geometry(s, r); Golden(r, (1, 30, 30), (2, 140, 48), (3, 292, 30), (4, 402, 48));
            Coordinates("Two selected groups coordinates", s, r);
        });
        Check("Hierarchy group-to-loose link aligns measured member grips", () => {
            var a = N(1, 30, 60); var b = N(2, 180, 100); var c = N(3, 450, 30);
            var s = new HierarchySnapshot { Groups = new[] { G(100, new[] { a, b }) }, LooseNodes = new[] { c }, Edges = new[] { E(a, b), E(b, c) } };
            var r = Solve(s); Near(r.Tops[b.Id].Y + b.Outputs[0].Y, r.Tops[c.Id].Y + c.Inputs[0].Y, "mixed grip");
            Stable(s, r); Geometry(s, r);
        });
        Check("Hierarchy scribbles keep their anchor offset and translate with outer groups", () => {
            var a = N(1, 30, 60); var b = N(2, 180, 100); var c = N(3, 450, 30);
            var g = G(100, new[] { a, b }); var scribble = new NonNodeMember { Id = Id(300), Label = "Notes", Bounds = new Box(0, 0, 40, 25), PivotOffset = new Vec2(3.25, 8.5) };
            g.NonNodes = new[] { scribble };
            var single = new HierarchySnapshot { Groups = new[] { g }, Edges = new[] { E(a, b) } };
            var alone = Solve(single); Near(alone.Tops[scribble.Id].X, 0, "inner scribble x"); Near(alone.Tops[scribble.Id].Y, 0, "inner scribble y"); Stable(single, alone);
            Near(new[] { a, b }.Min(n => alone.Tops[n.Id].X), 30, "units anchor x");
            Near(new[] { a, b }.Min(n => alone.Tops[n.Id].Y), 60, "units anchor y");
            Assert(new[] { a, b }.All(n => !Overlap(Placed(n, alone), scribble.Bounds)), "node covers title scribble");
            var s = new HierarchySnapshot { Groups = new[] { g }, LooseNodes = new[] { c }, Edges = new[] { E(c, a), E(a, b) } };
            var r = Solve(s); Near(r.Tops[scribble.Id].X - alone.Tops[scribble.Id].X, r.Tops[a.Id].X - alone.Tops[a.Id].X, "scribble translation x");
            Near(r.Tops[scribble.Id].Y - alone.Tops[scribble.Id].Y, r.Tops[a.Id].Y - alone.Tops[a.Id].Y, "scribble translation y"); Stable(s, r);
        });
        Check("Hierarchy note below components follows downward growth and is idempotent", () => {
            var a = N(1, 30, 60); var b = N(2, 30, 70);
            var g = G(100, new[] { a, b });
            var note = new NonNodeMember { Id = Id(300), Label = "Below", Bounds = new Box(30, 110, 60, 25) };
            g.NonNodes = new[] { note };
            var s = new HierarchySnapshot { Groups = new[] { g } }; var r = Solve(s);
            double growth = new[] { a, b }.Max(n => Placed(n, r).Bottom) - 110;
            Assert(growth > 0, "fixture did not grow downward");
            Near(r.Tops[note.Id].X, 30, "below note x"); Near(r.Tops[note.Id].Y, 110 + growth, "below note y");
            Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy fractional growth of 37.4 moves the note below by exactly 38 whole units", () => {
            var a = N(1, 29.75, 59.75); var b = N(2, 29.35, 70.15, 80, 21);
            a.PivotOffset = new Vec2(7.25, 9.25); b.PivotOffset = new Vec2(7.65, 9.85);
            var g = G(100, new[] { a, b });
            var note = new NonNodeMember { Id = Id(300), Label = "Below fractional growth",
                Bounds = new Box(29.35, 99.75, 60, 25), PivotOffset = new Vec2(3.25, 8.5) };
            g.NonNodes = new[] { note };
            var s = new HierarchySnapshot { Groups = new[] { g } }; var r = Solve(s);
            Near(new[] { a, b }.Max(n => Placed(n, r).Bottom) - 99.75, 37.4, "fractional growth");
            Golden(r, (1, 29.75, 59.75), (2, 29.35, 116.15), (300, 29.35, 137.75));
            var move = r.Moves.Single(m => m.Id == note.Id);
            Near(move.Pivot.X - (note.Bounds.X + note.PivotOffset.X), 0, "note pivot delta x");
            Near(move.Pivot.Y - (note.Bounds.Y + note.PivotOffset.Y), 38, "note pivot delta y");
            WholeUnitMoves(s, r); Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy note right of components follows rightward growth and is idempotent", () => {
            var a = N(1, 30, 60); var b = N(2, 40, 70);
            var g = G(100, new[] { a, b });
            var note = new NonNodeMember { Id = Id(300), Label = "Right", Bounds = new Box(120, 60, 40, 60) };
            g.NonNodes = new[] { note };
            var s = new HierarchySnapshot { Groups = new[] { g }, Edges = new[] { E(a, b) } }; var r = Solve(s);
            double growth = new[] { a, b }.Max(n => Placed(n, r).Right) - 120;
            Assert(growth > 0, "fixture did not grow rightward");
            Near(r.Tops[note.Id].X, 120 + growth, "right note x"); Near(r.Tops[note.Id].Y, 60, "right note y");
            Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy note below and right follows growth on both axes and is idempotent", () => {
            var a = N(1, 30, 60); var b = N(2, 40, 70);
            var g = G(100, new[] { a, b });
            var note = new NonNodeMember { Id = Id(300), Label = "Corner", Bounds = new Box(120, 110, 40, 25) };
            g.NonNodes = new[] { note };
            var s = new HierarchySnapshot { Groups = new[] { g }, Edges = new[] { E(a, b) } }; var r = Solve(s);
            double right = new[] { a, b }.Max(n => Placed(n, r).Right), bottom = new[] { a, b }.Max(n => Placed(n, r).Bottom);
            Assert(right > 120 && bottom > 110, "fixture did not grow on both axes");
            Near(r.Tops[note.Id].X, right, "corner note x"); Near(r.Tops[note.Id].Y, bottom, "corner note y");
            Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy note inside old units envelope stays put and logs overlap idempotently", () => {
            var a = N(1, 30, 60); var b = N(2, 180, 160);
            var g = G(100, new[] { a, b });
            var note = new NonNodeMember { Id = Id(300), Label = "Inside", Bounds = new Box(150, 90, 20, 20) };
            g.NonNodes = new[] { note };
            var s = new HierarchySnapshot { Groups = new[] { g }, Edges = new[] { E(a, b) } }; var r = Solve(s);
            Near(r.Tops[note.Id].X, 150, "inside note x"); Near(r.Tops[note.Id].Y, 90, "inside note y");
            Assert(Overlap(Placed(b, r), note.Bounds), "fixture did not overlap formatted node");
            string line = "Note 'Inside' overlaps formatted nodes in 'G100'";
            Assert(r.Log.Contains(line), "missing note overlap log");
            Assert(Solve(Applied(s, r)).Log.Contains(line), "second run lost overlap log");
            Stable(s, r); Geometry(s, r);
        });
        Check("Hierarchy label notes translate with their node and report overlaps", () => {
            var a = N(1, 30, 60); var b = N(2, 180, 160);
            var label = G(101, new[] { b });
            var note = new NonNodeMember { Id = Id(300), Label = "Label note", Bounds = new Box(180, 210, 40, 25) };
            var inside = new NonNodeMember { Id = Id(301), Label = "Inside label", Bounds = new Box(190, 170, 20, 20) };
            label.NonNodes = new[] { note, inside };
            var s = new HierarchySnapshot { Groups = new[] { G(100, new[] { a }, label) }, Edges = new[] { E(a, b) } };
            var r = Solve(s);
            Near(r.Tops[note.Id].X - note.Bounds.X, r.Tops[b.Id].X - b.CurrentTop.X, "label note translation x");
            Near(r.Tops[note.Id].Y - note.Bounds.Y, r.Tops[b.Id].Y - b.CurrentTop.Y, "label note translation y");
            Assert(r.Log.Contains("Note 'Inside label' overlaps formatted nodes in 'G101'"), "missing label note overlap log");
            Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy labels reserve their scribble size instead of only their node", () => {
            // One source feeds two labelled params stacked in one column. Each label
            // carries a large scribble under its param, much taller than the row gap.
            var src = N(1, 0, 0); var p = N(2, 200, 0, 70, 20); var q = N(3, 200, 30, 70, 20);
            var big1 = new NonNodeMember { Id = Id(300), Label = "Big 1", Bounds = new Box(200, 25, 260, 90) };
            var big2 = new NonNodeMember { Id = Id(301), Label = "Big 2", Bounds = new Box(200, 55, 260, 90) };
            var l1 = G(101, new[] { p }); l1.NonNodes = new[] { big1 };
            var l2 = G(102, new[] { q }); l2.NonNodes = new[] { big2 };
            var s = new HierarchySnapshot { Groups = new[] { G(100, new[] { src }, l1, l2) }, Edges = new[] { E(src, p), E(src, q) } };
            var r = Solve(s);
            Box Note(NonNodeMember n) => new Box(r.Tops[n.Id].X, r.Tops[n.Id].Y, n.Bounds.Width, n.Bounds.Height);
            Assert(!Overlap(r.GroupBoxes[l1.Id], r.GroupBoxes[l2.Id]), "label boxes overlap");
            Assert(!Overlap(Note(big1), Placed(q, r)) && !Overlap(Note(big2), Placed(p, r)), "scribble overlaps the other label's param");
            Assert(!Overlap(Note(big1), Note(big2)), "scribbles overlap each other");
            Near(r.Tops[big1.Id].X - big1.Bounds.X, r.Tops[p.Id].X - p.CurrentTop.X, "scribble follows its param x");
            Near(r.Tops[big1.Id].Y - big1.Bounds.Y, r.Tops[p.Id].Y - p.CurrentTop.Y, "scribble follows its param y");
            Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy labelled params with scribbles keep their output column and alignment", () => {
            // Live Rhino fixture: one source feeds three output params. Two labels carry
            // wide scribbles; the third has none. All three must share the last column,
            // aligned on the params themselves, not pushed past the scribbles' right edge.
            Node Param(int i, double x, double y) { var n = N(i, x, y, 70, 20); n.Kind = NodeKind.Param; n.PivotOffset = new Vec2(12, 7);
                n.Inputs = new[] { new Vec2(0, 10) }; n.Outputs = new[] { new Vec2(70, 10) }; return n; }
            var src = N(1, 0, 120, 140, 20); src.Kind = NodeKind.Param; src.Widget = true; src.Inputs = new[] { new Vec2(0, 10) }; src.Outputs = new[] { new Vec2(140, 10) };
            var p1 = Param(2, 200, 0); var p2 = Param(3, 200, 150); var p3 = Param(4, 200, 300);
            var n1 = new NonNodeMember { Id = Id(300), Label = "First large note", Bounds = new Box(220, 25, 630, 75) };
            var n2 = new NonNodeMember { Id = Id(301), Label = "Second large note", Bounds = new Box(220, 175, 630, 75) };
            var l1 = G(101, new[] { p1 }); l1.NonNodes = new[] { n1 };
            var l2 = G(102, new[] { p2 }); l2.NonNodes = new[] { n2 };
            var l3 = G(103, new[] { p3 });
            var s = new HierarchySnapshot { Groups = new[] { G(100, new[] { src }, l1, l2, l3) }, Edges = new[] { E(src, p1), E(src, p2), E(src, p3) } };
            var r = Solve(s);
            double Right(Node n) => r.Tops[n.Id].X + n.Width;
            Near(Right(p1), Right(p3), "labelled and plain params share a right edge");
            Near(Right(p2), Right(p3), "labelled params share a right edge");
            Assert(r.Tops[p1.Id].X > r.Tops[src.Id].X + src.Width, "params left of their source");
            Assert(Right(p3) < n1.Bounds.Right - 300, "plain param pushed past the scribbles");
            Box Note(NonNodeMember n) => new Box(r.Tops[n.Id].X, r.Tops[n.Id].Y, n.Bounds.Width, n.Bounds.Height);
            foreach (var note in new[] { n1, n2 }) foreach (var p in new[] { p1, p2, p3 })
                Assert(!Overlap(Note(note), Placed(p, r)), "scribble overlaps " + p.Label);
            Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy parent notes follow child-group growth before outer translation", () => {
            var a = N(1, 30, 60); var b = N(2, 30, 70); var loose = N(3, 400, 20);
            var child = G(101, new[] { a, b }); var parent = G(100, Array.Empty<Node>(), child);
            var old = InitialBox(child);
            var note = new NonNodeMember { Id = Id(300), Label = "Below child", Bounds = new Box(old.X, old.Bottom, 40, 25) };
            parent.NonNodes = new[] { note };
            var single = new HierarchySnapshot { Groups = new[] { parent } }; var alone = Solve(single);
            Assert(alone.GroupBoxes[child.Id].Bottom > old.Bottom, "child did not grow");
            Near(alone.Tops[note.Id].Y, alone.GroupBoxes[child.Id].Bottom, "note follows child growth");
            var s = new HierarchySnapshot { Groups = single.Groups, LooseNodes = new[] { loose }, Edges = new[] { E(loose, a) } };
            var r = Solve(s);
            Near(r.Tops[note.Id].X - alone.Tops[note.Id].X, r.Tops[a.Id].X - alone.Tops[a.Id].X, "parent note outer x");
            Near(r.Tops[note.Id].Y - alone.Tops[note.Id].Y, r.Tops[a.Id].Y - alone.Tops[a.Id].Y, "parent note outer y");
            Stable(single, alone); Stable(s, r); Geometry(s, r); NotesClear(s, r);
        });
        Check("Hierarchy refuses overlapping ownership, locked groups and contracted cycles atomically", () => {
            var a = N(1); var b = N(2); var c = N(3);
            void Refused(HierarchySnapshot s, string text) { var r = HierarchySolver.Solve(s); Assert(!r.Success && r.Moves.Count == 0 && r.Tops.Count == 0 && r.Diagnostic.Contains(text), r.Diagnostic); }
            Refused(new HierarchySnapshot { Groups = new[] { G(100, new[] { a, b }), G(101, new[] { a, c }) } }, a.Label);
            var locked = G(100, new[] { a, b }); locked.Locked = true;
            Refused(new HierarchySnapshot { Groups = new[] { locked } }, "locked");
            Refused(new HierarchySnapshot { Groups = new[] { G(100, new[] { a, c }) }, LooseNodes = new[] { b }, Edges = new[] { E(a, b), E(b, c) } }, "Cycle");
        });
        Check("Hierarchy contracted nested child dissolves, labels overlap and empty children are ignored", () => {
            var a = N(1, 30, 60); var b = N(2, 100, 90); var c = N(3, 180, 70);
            var child = G(101, new[] { a,c }, G(103, Array.Empty<Node>()));
            var root = G(100, new[] { b }, child, G(104, Array.Empty<Node>()));
            var label = G(102, new[] { a });
            var s = new HierarchySnapshot { Groups = new[] { root,label }, Edges = new[] { E(a,b),E(b,c) } };
            var r = Solve(s);
            Assert(r.Log.Contains("'G101' was laid out with 'G100': its wires leave and re-enter it"), "missing dissolve line");
            Assert(!r.GroupBoxes.ContainsKey(Id(103)) && !r.GroupBoxes.ContainsKey(Id(104)), "empty boxes became layout units");
            Assert(r.Tops[a.Id].X < r.Tops[b.Id].X && r.Tops[b.Id].X < r.Tops[c.Id].X, "parent was not laid out directly");
            Assert(r.GroupBoxes[label.Id].X == r.Tops[a.Id].X - label.Padding.Left, "overlapping label did not follow node");
            Stable(s,r); WholeUnitMoves(s,r);
        });
        Check("Hierarchy rechecks after each dissolution in depth and id order", () => {
            var n = Enumerable.Range(1,5).Select(i => N(i,20*i,30*i)).ToArray();
            var root = G(100,Array.Empty<Node>(),G(102,new[] { n[1],n[3] }),G(101,new[] { n[0],n[2],n[4] }));
            var s = new HierarchySnapshot { Groups = new[] { root }, Edges = Enumerable.Range(0,4).Select(i => E(n[i],n[i+1])).ToArray() };
            var r = Solve(s);
            Assert(r.Log.Where(l => l.Contains("its wires leave and re-enter it")).SequenceEqual(new[] {
                "'G101' was laid out with 'G100': its wires leave and re-enter it",
                "'G102' was laid out with 'G100': its wires leave and re-enter it" }),"did not recheck or used unstable dissolve order");
            Stable(s,r); WholeUnitMoves(s,r);
        });
        Check("Hierarchy empty-bounds nodes are excluded and reported without refusing their group", () => {
            var a = N(1, 30, 60); var bad = N(2, 100, 90, 0); var b = N(3, 180, 70);
            var root = G(100, new[] { a,b }, G(101, new[] { bad }));
            var r = Solve(new HierarchySnapshot { Groups = new[] { root }, Edges = new[] { E(a,bad),E(bad,b) } });
            Assert(!r.Tops.ContainsKey(bad.Id) && r.Tops.Count == 2 && r.Log.Any(l => l.Contains("empty bounds")), "excluded node skipped its root");
            var malformed = N(4); malformed.Width = double.NaN;
            Assert(!HierarchySolver.Solve(new HierarchySnapshot { Groups = new[] { G(102,new[] { malformed }) } }).Success, "malformed geometry accepted");
        });
        Check("Random hierarchy recovery: 300 seeds, contracted children, label overlaps and empty descendants", () => {
            for (int seed = 0; seed < 300; seed++) {
                var random = new Random(seed);
                var a = N(1,random.Next(200),random.Next(200)); var b = N(2,random.Next(200),random.Next(200)); var c = N(3,random.Next(200),random.Next(200));
                var root = G(100,new[] { b },G(101,new[] { a,c },G(103,Array.Empty<Node>())),G(104,new[] { a }));
                var s = new HierarchySnapshot { Groups = new[] { root }, Edges = new[] { E(a,b),E(b,c) } };
                var r = Solve(s); Stable(s,r); WholeUnitMoves(s,r);
                Assert(r.Log.Count(l => l.Contains("its wires leave and re-enter it")) == 1,"seed " + seed + " dissolve count");
                var shuffled = Applied(s,new Result { Tops = new[] { a,b,c }.ToDictionary(n => n.Id,n => n.CurrentTop) },random);
                var other = Solve(shuffled);
                Assert(Fingerprint(r) == Fingerprint(other) && r.Log.SequenceEqual(other.Log),"seed " + seed + " nondeterministic recovery");
                var nodes = new[] { a,b,c }.Select(n => Placed(n,r)).ToArray();
                for (int i = 0; i < nodes.Length; i++) for (int j = i + 1; j < nodes.Length; j++) Assert(!Overlap(nodes[i],nodes[j]),"recovery overlapped nodes");
            }
        });
        Check("Rigid groups left-align group-only columns, use group row gaps and reserve no long lanes", () => {
            var a = N(1, 0, 0, 110); var b = N(2, 100, 200, 70); var c = N(3, 200, 60); var d = N(4, 400, 0);
            foreach (var n in new[] { a, b, c, d }) n.Kind = NodeKind.Group;
            var s = new Snapshot { Nodes = new[] { a, b, c, d }, Edges = new[] { E(a, c), E(b, c), E(c, d), E(a, d) } };
            var r = LayoutSolver.Solve(s); Assert(r.Success && !r.Unstable && r.LaneEdges.Count == 0, r.Diagnostic);
            Near(r.Tops[a.Id].X, r.Tops[b.Id].X, "group column left"); Near(r.Tops[b.Id].Y - r.Tops[a.Id].Y - a.Height, 40, "group row gap");
            var applied = s.Nodes.Select(n => AppliedNode(n, r)).ToArray(); Assert(LayoutSolver.Solve(new Snapshot { Nodes = applied, Edges = s.Edges }).Moves.Count == 0, "rigid fixed point");
        });
        Check("Hierarchy rejects malformed snapshots and supports annotation-only groups", () => {
            Assert(!HierarchySolver.Solve(null).Success && !HierarchySolver.Solve(new HierarchySnapshot { Groups = null }).Success, "invalid snapshot accepted");
            var g = G(100, Array.Empty<Node>()); g.NonNodes = new[] { new NonNodeMember { Id = Id(300), Bounds = new Box(0, 0, 50, 20) } };
            var n = N(1, 200, 50);
            var s = new HierarchySnapshot { Groups = new[] { g }, LooseNodes = new[] { n } }; var r = Solve(s); Stable(s, r); Geometry(s, r);
            var cyclic = G(101, new[] { n }); cyclic.Children = new[] { cyclic };
            Assert(HierarchySolver.Solve(new HierarchySnapshot { Groups = new[] { cyclic } }).Diagnostic.Contains("cycle"), "group cycle accepted");
        });
        Check("Hierarchy flat input preserves phase-one tops and moves exactly", () => {
            var n = new[] { N(1, 80.25, 90.75), N(2, 20.5, -12.25), N(3, 500.5, 32.25) };
            n[0].PivotOffset = new Vec2(7.25, 9.5);
            var e = new[] { E(n[0], n[1]), E(n[1], n[2]) };
            var flat = LayoutSolver.Solve(new Snapshot { Nodes = n, Edges = e });
            var r = Solve(new HierarchySnapshot { LooseNodes = n, Edges = e });
            Assert(Fingerprint(flat) == Fingerprint(r) && flat.Moves.Select(m => m.Id).SequenceEqual(r.Moves.Select(m => m.Id)), "flat hierarchy changed phase one");
            foreach (var m in flat.Moves) { var actual = r.Moves.Single(a => a.Id == m.Id); Near(actual.Pivot.X, m.Pivot.X, "flat pivot x"); Near(actual.Pivot.Y, m.Pivot.Y, "flat pivot y"); }
        });
        Check("Random hierarchy properties: 1000 seeds, depth 0-3, labels, scribbles, DAGs, stable and deterministic", () => {
            for (int seed = 0; seed < 1000; seed++)
            {
                try {
                    var s = RandomTree(seed);
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
                    var before = System.Text.Json.JsonSerializer.Serialize(s, jsonOptions);
                    var r = Solve(s);
                    Assert(before == System.Text.Json.JsonSerializer.Serialize(s, jsonOptions), "solver mutated its input");
                    Assert(r.Moves.Select(m => m.Id).SequenceEqual(r.Moves.Select(m => m.Id).Distinct().OrderBy(id => id)), "move order or duplicate");
                    Stable(s, r); Geometry(s, r); NotesClear(s, r);
                    Assert(Fingerprint(r) == Fingerprint(Solve(s)), "repeat differs");
                    var initial = new Result { Tops = s.Groups.SelectMany(Nodes).Concat(s.LooseNodes).GroupBy(n => n.Id).Select(g => g.First()).ToDictionary(n => n.Id, n => n.CurrentTop) };
                    var allTops = initial.Tops.ToDictionary(p => p.Key, p => p.Value);
                    foreach (var m in s.Groups.SelectMany(Groups).SelectMany(g => g.NonNodes)) allTops[m.Id] = new Vec2(m.Bounds.X, m.Bounds.Y);
                    initial.Tops = allTops;
                    Assert(Fingerprint(r) == Fingerprint(Solve(Applied(s, initial, new Random(seed + 9999)))), "shuffle differs");
                    Assert(r.Moves.All(m => allTops.ContainsKey(m.Id)) && r.Tops.Count == allTops.Count, "non-target moved");
                    // Preserve the original fractional-pivot fixtures above. Whole-unit
                    // deltas assume node pivots start on the canvas's whole-unit grid.
                    var integralTops = new Dictionary<Guid, Vec2>(allTops);
                    foreach (var n in s.Groups.SelectMany(Nodes).Concat(s.LooseNodes))
                        integralTops[n.Id] = new Vec2(LayoutSolver.Round(n.CurrentTop.X + n.PivotOffset.X) - n.PivotOffset.X,
                            LayoutSolver.Round(n.CurrentTop.Y + n.PivotOffset.Y) - n.PivotOffset.Y);
                    var integral = Applied(s, new Result { Tops = integralTops });
                    var integralResult = Solve(integral);
                    WholeUnitMoves(integral, integralResult);
                    Stable(integral, integralResult); Geometry(integral, integralResult); NotesClear(integral, integralResult);
                } catch (Exception e) { throw new Exception("seed " + seed + ": " + e.Message); }
            }
        });
        return failures;
    }

    private static Box Placed(Node n, Result r) => new Box(r.Tops[n.Id].X, r.Tops[n.Id].Y, n.Width, n.Height);
    private static void WholeUnitMoves(HierarchySnapshot s, Result r)
    {
        var pivots = s.Groups.SelectMany(Nodes).Concat(s.LooseNodes).GroupBy(n => n.Id).Select(g => g.First()).ToDictionary(n => n.Id,
            n => new Vec2(n.CurrentTop.X + n.PivotOffset.X, n.CurrentTop.Y + n.PivotOffset.Y));
        foreach (var n in s.Groups.SelectMany(Groups).SelectMany(g => g.NonNodes))
            pivots[n.Id] = new Vec2(n.Bounds.X + n.PivotOffset.X, n.Bounds.Y + n.PivotOffset.Y);
        foreach (var move in r.Moves)
        {
            double dx = move.Pivot.X - pivots[move.Id].X, dy = move.Pivot.Y - pivots[move.Id].Y;
            Assert(Math.Abs(dx - Math.Round(dx)) <= 1e-6 && Math.Abs(dy - Math.Round(dy)) <= 1e-6,
                "fractional pivot delta for " + move.Id + ": " + dx + "," + dy);
        }
    }
    private static bool Overlap(Box a, Box b) => a.Right > b.X + 1e-7 && b.Right > a.X + 1e-7 && a.Bottom > b.Y + 1e-7 && b.Bottom > a.Y + 1e-7;
    private static Box Envelope(IEnumerable<Box> source)
    {
        var boxes = source.ToArray(); double x = boxes.Min(b => b.X), y = boxes.Min(b => b.Y);
        return new Box(x, y, boxes.Max(b => b.Right) - x, boxes.Max(b => b.Bottom) - y);
    }
    private static Box InitialBox(HierarchyGroup g)
    {
        var b = Envelope(g.Nodes.Select(n => new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height))
            .Concat(g.NonNodes.Select(n => n.Bounds)).Concat(g.Children.Select(InitialBox)));
        return new Box(b.X - g.Padding.Left, b.Y - g.Padding.Top,
            b.Width + g.Padding.Left + g.Padding.Right, b.Height + g.Padding.Top + g.Padding.Bottom);
    }
    private static Box InitialUnits(HierarchyGroup g)
    {
        var indirect = new HashSet<Guid>(g.Children.SelectMany(Nodes).Select(n => n.Id));
        return Envelope(g.Nodes.Where(n => !indirect.Contains(n.Id)).Concat(g.Children.Where(c => SeeThrough(c)).SelectMany(Nodes))
            .Select(n => new Box(n.CurrentTop.X, n.CurrentTop.Y, n.Width, n.Height))
            .Concat(g.Children.Where(c => !SeeThrough(c)).Select(InitialBox)));
    }
    private static void NotesClear(HierarchySnapshot s, Result r)
    {
        foreach (var g in s.Groups.SelectMany(Groups).Where(g => Nodes(g).Any()))
        {
            var nodes = Nodes(g).ToArray();
            var old = InitialUnits(g);
            foreach (var note in g.NonNodes.Where(n => n.Bounds.Right <= old.X || n.Bounds.Bottom <= old.Y ||
                n.Bounds.Y >= old.Bottom || n.Bounds.X >= old.Right))
            {
                var top = r.Tops[note.Id]; var bounds = new Box(top.X, top.Y, note.Bounds.Width, note.Bounds.Height);
                Assert(nodes.All(n => !Overlap(Placed(n, r), bounds)), "node overlaps outside note " + note.Id + " in " + g.Label +
                    " (nodes " + string.Join(",", Nodes(g).Select(n => n.Label)) + "; old units " + old.X + "," + old.Y + "," + old.Right + "," + old.Bottom +
                    "; old note " + note.Bounds.X + "," + note.Bounds.Y + "; new note " + top.X + "," + top.Y +
                    "; overlaps " + string.Join(",", nodes.Where(n => Overlap(Placed(n, r), bounds)).Select(n => n.Label)) + ")");
            }
        }
    }
    private static void Geometry(HierarchySnapshot s, Result r)
    {
        Box B(Node n) => new Box(r.Tops[n.Id].X, r.Tops[n.Id].Y, n.Width, n.Height);
        void Apart(Box[] boxes) { for (int i = 0; i < boxes.Length; i++) for (int j = i + 1; j < boxes.Length; j++) Assert(!Overlap(boxes[i], boxes[j]), "overlapping sibling boxes"); }
        var nodes = s.Groups.SelectMany(Nodes).Concat(s.LooseNodes).GroupBy(n => n.Id).Select(a => a.First()).ToArray();
        Apart(nodes.Select(B).ToArray());
        foreach (var g in s.Groups.SelectMany(Groups))
        {
            var box = r.GroupBoxes[g.Id];
            foreach (var n in Nodes(g)) { var b = B(n); Assert(b.X >= box.X - 1e-7 && b.Y >= box.Y - 1e-7 && b.Right <= box.Right + 1e-7 && b.Bottom <= box.Bottom + 1e-7, "node outside group"); }
            foreach (var child in g.Children) { var b = r.GroupBoxes[child.Id]; Assert(b.X >= box.X - 1e-7 && b.Y >= box.Y - 1e-7 && b.Right <= box.Right + 1e-7 && b.Bottom <= box.Bottom + 1e-7, "child box outside parent"); }
            foreach (var n in g.NonNodes) { var t = r.Tops[n.Id]; Assert(t.X >= box.X && t.Y >= box.Y && t.X + n.Bounds.Width <= box.Right && t.Y + n.Bounds.Height <= box.Bottom, "scribble outside group"); }
            var real = g.Children.Where(c => !SeeThrough(c)).ToArray();
            var indirect = new HashSet<Guid>(g.Children.SelectMany(Nodes).Select(n => n.Id));
            Apart(real.Select(c => r.GroupBoxes[c.Id]).Concat(g.Nodes.Where(n => !indirect.Contains(n.Id)).Select(B))
                .Concat(g.Children.Where(c => SeeThrough(c)).SelectMany(Nodes).Select(B)).ToArray());
        }
        Apart(s.Groups.Select(g => SeeThrough(g) ? B(Nodes(g).Single()) : r.GroupBoxes[g.Id]).Concat(s.LooseNodes.Select(B)).ToArray());
    }
    private static HierarchySnapshot RandomTree(int seed)
    {
        var random = new Random(seed); int id = 0, gid = 10000, mid = 20000;
        var all = new List<Node>();
        Node Make() {
            var n = N(++id, random.Next(-100, 700) + random.NextDouble(), random.Next(-100, 500) + random.NextDouble(),
                random.Next(30, 160) + random.NextDouble(), random.Next(20, 90) + random.NextDouble());
            n.PivotOffset = new Vec2(random.NextDouble() * 12, random.NextDouble() * 10);
            n.Inputs = Enumerable.Range(0, random.Next(1, 5)).Select(p => new Vec2(-3.25, (p + 1) * n.Height / 5)).ToArray();
            n.Outputs = Enumerable.Range(0, random.Next(1, 4)).Select(p => new Vec2(n.Width + 4.5, (p + 1) * n.Height / 4)).ToArray();
            if (random.Next(4) == 0) n.Kind = NodeKind.Param;
            all.Add(n); return n;
        }
        HierarchyGroup Build(int depth)
        {
            var children = new List<HierarchyGroup>(); var direct = new List<Node>();
            int count = random.Next(1, 5);
            for (int i = 0; i < count; i++) { if (depth > 0 && random.Next(3) == 0) children.Add(Build(depth - 1)); else direct.Add(Make()); }
            var g = G(++gid, direct.ToArray(), children.ToArray());
            g.Padding = new Padding(8 + random.NextDouble(), 18 + random.NextDouble(), 12 + random.NextDouble(), 14 + random.NextDouble());
            if (random.Next(4) == 0)
            {
                var units = InitialUnits(g);
                int position = random.Next(4);
                double x = position == 0 ? units.X - 60 : position == 2 ? units.Right + 10 : units.X + 5;
                double y = position == 0 ? units.Y - 40 : position == 1 ? units.Bottom + 10 : units.Y + 5;
                g.NonNodes = new[] { new NonNodeMember { Id = Id(++mid), Label = "Scribble", Bounds = new Box(x, y, 50, 30), PivotOffset = new Vec2(0, 0) } };
            }
            return g;
        }
        int depth = seed % 4;
        var roots = Enumerable.Range(0, random.Next(1, 4)).Select(_ => Build(depth)).ToArray();
        var loose = Enumerable.Range(0, random.Next(3)).Select(_ => Make()).ToArray();
        var edges = new List<Edge>();
        for (int i = 0; i < all.Count; i++) for (int j = i + 1; j < all.Count; j++) if (j == i + 1 && seed % 3 == 0 || random.Next(8) == 0)
                edges.Add(E(all[i], all[j], random.Next(all[j].Inputs.Length), random.Next(all[i].Outputs.Length)));
        return new HierarchySnapshot { Groups = roots, LooseNodes = loose, Edges = edges,
            Inbound = all.Where(_ => random.Next(5) == 0).Select(n => new ExternalInbound { Node = n.Id, Port = random.Next(n.Inputs.Length), SourceGripY = random.Next(-200, 800) }).ToArray(),
            Outbound = all.Where(_ => random.Next(5) == 0).Select(n => new ExternalOutbound { Node = n.Id, Port = random.Next(n.Outputs.Length), TargetGripY = random.Next(-200, 800) }).ToArray() };
    }
    private static HierarchySnapshot Interface(bool realGroup)
    {
        Node Param(int i, string label, double x, double y) { var n = N(i, x, y, 70, 20); n.Label = label; n.Kind = NodeKind.Param; n.PivotOffset = new Vec2(12, 7); n.Inputs = new[] { new Vec2(0, 10) }; n.Outputs = new[] { new Vec2(70, 10) }; return n; }
        Node Component(int i, string label, double x, double y, int inputs, double width, double height, int outputs)
        { var n = N(i, x, y, width, height); n.Label = label; n.PivotOffset = new Vec2(12, 7); n.Inputs = Enumerable.Range(0, inputs).Select(p => new Vec2(0, (p + .5) * height / inputs)).ToArray(); n.Outputs = Enumerable.Range(0, outputs).Select(p => new Vec2(width, (p + .5) * height / outputs)).ToArray(); return n; }
        var rec = Param(1, "Rec", 0, 0); rec.Widget = true;
        var pln = Param(2, "Pln", 120, 0);
        var explode = Component(3, "Explode", 120, 50, 2, 90, 80, 2);
        var item = Component(4, "Item", 250, 60, 3, 100, 110, 4);
        item.Inputs = new[] { new Vec2(0, 22), new Vec2(0, 55), new Vec2(0, 88) }; item.Outputs = new[] { new Vec2(100, 22), new Vec2(100, 44), new Vec2(100, 66), new Vec2(100, 88) };
        var bottom = Param(5, "Pt BottomLeft", 400, 60); var decon = Component(6, "pDecon", 400, 150, 1, 90, 90, 3);
        var num = Param(7, "Num", 550, 150); var top = Param(8, "Pt TopLeft", 400, 250);
        var panel = Component(9, "Panel", 0, 200, 1, 140, 130, 1); panel.Kind = NodeKind.Panel;
        var labels = new[] { rec, bottom, num, top }.Select(n => G(100 + Array.IndexOf(new[] { rec, bottom, num, top }, n), new[] { n })).ToList();
        var direct = new List<Node> { pln, explode, item, decon, panel };
        if (realGroup) { labels.Add(G(105, new[] { explode, item })); direct.Remove(explode); direct.Remove(item); }
        return new HierarchySnapshot { Groups = new[] { G(200, direct.ToArray(), labels.ToArray()) },
            Edges = new[] { E(rec, pln), E(rec, explode), E(explode, item, 0, 1), E(item, bottom), E(item, decon, 0, 2), E(decon, num), E(item, top, 0, 3) },
            Inbound = new[] { new ExternalInbound { Node = panel.Id, SourceGripY = 220 } }, Outbound = new[] { new ExternalOutbound { Node = rec.Id, TargetGripY = 10 } } };
    }
}
