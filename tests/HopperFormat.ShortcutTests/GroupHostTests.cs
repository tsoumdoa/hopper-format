using System;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperFormat;
using HopperFormat.Layout;

internal static class GroupHostTests
{
    private static int failures;
    private static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Check(string name, Action test)
    { try { test(); Console.WriteLine("PASS " + name); } catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    internal static int Run()
    {
        Check("Group snapshot measures direct units, labels, asymmetric padding and non-node members", () => {
            var f = new Fixture(); var capture = SelectionSnapshot.Capture(f.Document, new Options());
            Assert(capture.Diagnostic == null && capture.ContainsGroups && capture.Hierarchy.Groups.Count == 1, capture.Diagnostic);
            var outer = capture.Hierarchy.Groups.Single(); var inner = outer.Children.Single(c => c.Id == f.Inner.InstanceGuid);
            var label = inner.Children.Single();
            Assert(label.Nodes.Single().Id == f.B.InstanceGuid && inner.Nodes.Single().Id == f.C.InstanceGuid && outer.Nodes.Single().Id == f.A.InstanceGuid,
                "direct members or label group missing");
            Assert(capture.Hierarchy.LooseNodes.Single().Id == f.Loose.InstanceGuid && capture.Objects.Count == 5
                && outer.NonNodes.Single().Id == f.Notes.InstanceGuid, "covered node duplicated, loose node or scribble lost");
            Assert(outer.Padding.Left == 13 && outer.Padding.Top == 27 && outer.Padding.Right == 19 && outer.Padding.Bottom == 11
                && inner.Padding.Left == 10 && inner.Padding.Top == 20, "padding was assumed or measured against flattened labels");
            Assert(outer.NonNodes[0].PivotOffset.X == 7 && outer.NonNodes[0].PivotOffset.Y == 9
                && outer.NonNodes[0].Bounds.Width == 80, "non-node bounds/offset missing");
        });
        Check("Group apply composes pivots once, records Format groups and refreshes every ancestor including labels", () => {
            var f = new Fixture(); var capture = SelectionSnapshot.Capture(f.Document, new Options()); var r = HierarchySolver.Solve(capture.Hierarchy);
            Assert(r.Success && !r.Unstable && r.Moves.Count > 0, r.Diagnostic);
            var before = capture.Objects.ToDictionary(p => p.Key, p => p.Value.Attributes.Pivot);
            f.Document.UndoUtil.OnRecord = () => Assert(new[] { f.Outer, f.Inner, f.Label }.All(g => g.CacheExpirations == 1), "record preceded cache refresh");
            Assert(FormatApplier.Apply(f.Document, r.Moves, capture.Objects, out var status, capture.ContainsGroups), status);
            var record = f.Document.UndoUtil.Records.Single();
            Assert(record.Name == "Format groups" && f.Document.UndoUtil.Creates == 1 && record.Before.Count == r.Moves.Count
                && record.Before.All(p => p.Value == before[p.Key.InstanceGuid]), "undo snapshot incomplete or written too late");
            Assert(r.Moves.All(m => capture.Objects[m.Id].Attributes.Pivot == new PointF((float)m.Pivot.X, (float)m.Pivot.Y)), "pivot not composed");
            var next = SelectionSnapshot.Capture(f.Document, new Options()); var again = HierarchySolver.Solve(next.Hierarchy);
            Assert(again.Success && !again.Unstable && again.Moves.Count == 0, "remeasured hierarchy is not fixed: " + again.Diagnostic);
            foreach (var g in new[] { f.Outer, f.Inner, f.Label }) {
                var predicted = r.GroupBoxes[g.InstanceGuid]; var measured = g.Attributes.Bounds;
                Assert(Math.Abs(predicted.X - measured.X) < .01 && Math.Abs(predicted.Y - measured.Y) < .01
                    && Math.Abs(predicted.Width - measured.Width) < .01 && Math.Abs(predicted.Height - measured.Height) < .01, "predicted group bounds differ from host");
            }
            Assert(f.Unrelated.Attributes == null, "unselected object was measured or moved");
        });
        Check("Group pivot mismatch restores all descendants and scribbles without recording undo", () => {
            var f = new Fixture(); var c = SelectionSnapshot.Capture(f.Document, new Options()); var r = HierarchySolver.Solve(c.Hierarchy);
            var before = c.Objects.ToDictionary(p => p.Key, p => p.Value.Attributes.Pivot);
            var bad = r.Moves.Last(); var old = before[bad.Id];
            c.Objects[bad.Id].Attributes.TransformPivot = p => p == old ? p : new PointF(p.X + 2, p.Y);
            Assert(!FormatApplier.Apply(f.Document, r.Moves, c.Objects, out var status, true) && status.Contains("pivot mismatch"), "mismatch accepted");
            Assert(f.Document.UndoUtil.Records.Count == 0 && c.Objects.All(p => p.Value.Attributes.Pivot == before[p.Key]), "rollback incomplete");
            Assert(new[] { f.Outer, f.Inner, f.Label }.All(g => g.CacheExpirations > 0), "rollback did not refresh labels/ancestors");
        });
        Check("Group snapshot refuses ownership outside selection and locks on selected groups or descendants", () => {
            var f = new Fixture(); var other = new GH_Group { NickName = "Other tree" }; other.ObjectIDs.AddRange(new[] { f.B.InstanceGuid, f.C.InstanceGuid }); f.Document.Objects.Add(other);
            var c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(c.Diagnostic.Contains("Overlapping") && (c.Diagnostic.Contains(f.B.NickName) || c.Diagnostic.Contains(f.C.NickName)), "outside ownership accepted");
            f.Document.Objects.Remove(other); f.Outer.NickName = "Protected [LoCk]";
            c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(GroupMembership.LockedDiagnostic(f.Document, c.CandidateIds).Contains("Protected"), "selected lock accepted");
            f.Document.Selection.Clear(); f.Document.Selection.Add(f.B);
            c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(GroupMembership.LockedDiagnostic(f.Document, c.CandidateIds).Contains("Protected"), "node in locked ancestor accepted");
            f.Document.Selection.Clear(); f.Document.Selection.Add(f.Outer); f.Outer.NickName = "Outer"; f.Inner.NickName = "Inner [lock]";
            c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(GroupMembership.LockedDiagnostic(f.Document, c.CandidateIds).Contains("Inner"), "locked descendant accepted");
        });
        Check("Format-all snapshot includes roots, label targets, loose obstacles, locks and skipped ownership without selection writes", () => {
            var f = new Fixture();
            var other = new GH_Group { NickName = "Shared" }; other.ObjectIDs.AddRange(new[] { f.B.InstanceGuid, f.C.InstanceGuid });
            var goodNode = new TestComponent { InitialPivot = new PointF(1000,1000) };
            var good = new GH_Group { NickName = "Good label" }; good.ObjectIDs.Add(goodNode.InstanceGuid);
            var lockedNode = new TestComponent { InitialPivot = new PointF(1400,1000) };
            var locked = new GH_Group { NickName = "Fixed [lock]" }; locked.ObjectIDs.Add(lockedNode.InstanceGuid);
            var owner = new HopperFormatComponent();
            f.Document.Objects.AddRange(new IGH_DocumentObject[] { other, goodNode, good, lockedNode, locked, owner });
            var selection = f.Document.SelectedObjects().ToArray();
            Assert(FormatAllCapture.EligibleCount(f.Document) == 1 && f.A.Attributes == null && owner.Attributes == null, "preflight measured objects or counted skipped groups");
            var c = FormatAllCapture.Capture(f.Document, new Options());
            Assert(c.Snapshot.Targets.Count == 4 && c.Snapshot.Targets.Count(t => t.SkipReason == null) == 1
                && c.Snapshot.Targets.Single(t => t.Id == good.InstanceGuid).Hierarchy.Groups.Single().Nodes.Single().Id == goodNode.InstanceGuid,
                "top-level/label targets incorrect");
            Assert(c.Snapshot.Targets.Single(t => t.Id == f.Outer.InstanceGuid).SkipReason.Contains("Overlapping")
                && c.Snapshot.Targets.Single(t => t.Id == locked.InstanceGuid).SkipReason.Contains("locked"), "skip reasons absent");
            Assert(c.Objects.Keys.SequenceEqual(new[] { goodNode.InstanceGuid }) && c.Snapshot.Obstacles.Count >= 3
                && f.Document.Selection.SequenceEqual(selection), "obstacles included as moves or selection mutated");
            var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && r.Formatted == 1 && r.Skipped == 3, "partial run refused");
        });
        Check("Format-all apply creates one named record before writes, refreshes nested labels and rolls back mismatches", () => {
            var f = new Fixture(); f.Document.SelectedObjects();
            var c = FormatAllCapture.Capture(f.Document, new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && r.Layout.Moves.Count > 0, r.Layout.Diagnostic);
            var originals = c.Objects.ToDictionary(p => p.Key,p => p.Value.Attributes.Pivot);
            Assert(FormatApplier.Apply(f.Document,r.Layout.Moves,c.Objects,out var status,true,true),status);
            var record = f.Document.UndoUtil.Records.Single();
            Assert(record.Name == "Format all groups" && f.Document.UndoUtil.Creates == 1
                && record.Before.All(p => p.Value == originals[p.Key.InstanceGuid])
                && new[] { f.Outer,f.Inner,f.Label }.All(g => g.CacheExpirations == 1), "record/cache contract");
            var next = FormatAllCapture.Capture(f.Document,new Options()); var again = FormatAllSolver.Solve(next.Snapshot);
            Assert(again.Layout.Success && again.Layout.Moves.Count == 0 && !again.Layout.Unstable, "format all not fixed after host remeasurement");
            var fail = new Fixture(); fail.Document.SelectedObjects();
            c = FormatAllCapture.Capture(fail.Document,new Options()); r = FormatAllSolver.Solve(c.Snapshot);
            originals = c.Objects.ToDictionary(p => p.Key,p => p.Value.Attributes.Pivot);
            var last = r.Layout.Moves.Last(); var old = originals[last.Id];
            c.Objects[last.Id].Attributes.TransformPivot = p => p == old ? p : new PointF(p.X + 2,p.Y);
            Assert(!FormatApplier.Apply(fail.Document,r.Layout.Moves,c.Objects,out status,true,true)
                && status.Contains("pivot mismatch") && fail.Document.UndoUtil.Records.Count == 0
                && c.Objects.All(p => p.Value.Attributes.Pivot == originals[p.Key]), "format-all rollback failed");
        });
        Check("Format-all preflight admits contracted cycles without layout and keeps membership cycles fixed", () => {
            var f = new Fixture();
            // A -> B -> C -> Loose, but B/Loose inside Inner contracts Inner -> C -> Inner.
            f.Inner.ObjectIDs.Remove(f.C.InstanceGuid); f.Inner.ObjectIDs.Add(f.Loose.InstanceGuid); f.Outer.ObjectIDs.Add(f.C.InstanceGuid);
            Assert(FormatAllCapture.EligibleCount(f.Document) == 1 && f.A.Attributes == null, "contracted cycle preview performed layout or counted target");
            f.Document.SelectedObjects();
            var c = FormatAllCapture.Capture(f.Document,new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && r.Skipped == 0 && r.Layout.Log.Any(l => l.Contains("its wires leave and re-enter it")), "contracted child was not dissolved");
            var a = new GH_Group { NickName = "Cycle A" }; var b = new GH_Group { NickName = "Cycle B" };
            a.ObjectIDs.Add(b.InstanceGuid); b.ObjectIDs.Add(a.InstanceGuid);
            var doc = new GH_Document(); doc.Objects.AddRange(new[] { a,b });
            c = FormatAllCapture.Capture(doc,new Options()); r = FormatAllSolver.Solve(c.Snapshot);
            Assert(!r.Layout.Success && r.Skipped == 2 && a.Attributes == null && b.Attributes == null, "membership cycle recursed into host layout");
        });
        Check("Format-all host fractional bounds preserve whole pivot deltas with snapping attributes and reapply", () => {
            for (int seed = 0; seed < 100; seed++) {
                var random = new Random(seed); float Fraction() => (float)random.NextDouble();
                var f = new Fixture();
                f.Outer.PadLeft = 13 + Fraction(); f.Outer.PadTop = 27 + Fraction();
                f.A.Offset = new PointF(7 + Fraction(),9 + Fraction()); f.B.Offset = new PointF(7 + Fraction(),9 + Fraction());
                f.Unrelated.InitialPivot = new PointF(400 + Fraction(),0); f.Unrelated.Size = new SizeF(80 + Fraction(),1000);
                var n = new TestComponent { InitialPivot = new PointF(600,250), Offset = new PointF(7 + Fraction(),9 + Fraction()) };
                var label = new GH_Group { NickName = "Other label", PadTop = 20 + Fraction() }; label.ObjectIDs.Add(n.InstanceGuid);
                f.Document.Objects.AddRange(new IGH_DocumentObject[] { n,label }); f.Document.SelectedObjects();
                var c = FormatAllCapture.Capture(f.Document,new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
                foreach (var obj in c.Objects.Values)
                    obj.Attributes.TransformPivot = p => new PointF((float)Math.Round(p.X),(float)Math.Round(p.Y));
                foreach (var move in r.Layout.Moves) {
                    var before = c.Objects[move.Id].Attributes.Pivot;
                    double dx = move.Pivot.X - before.X, dy = move.Pivot.Y - before.Y;
                    Assert(Math.Abs(dx - Math.Round(dx)) < 1e-6 && Math.Abs(dy - Math.Round(dy)) < 1e-6,
                        "fractional host seed " + seed + " pivot delta=" + dx + "," + dy);
                }
                Assert(r.Layout.Success && FormatApplier.Apply(f.Document,r.Layout.Moves,c.Objects,out _,true,true), "first fractional run failed");
                c = FormatAllCapture.Capture(f.Document,new Options()); r = FormatAllSolver.Solve(c.Snapshot);
                Assert(r.Layout.Success && r.Layout.Moves.Count == 0, "fractional host seed " + seed + " second run moved: " + string.Join(";",r.Layout.Moves.Select(m => m.Id + " delta=" + (m.Pivot.X-c.Objects[m.Id].Attributes.Pivot.X) + "," + (m.Pivot.Y-c.Objects[m.Id].Attributes.Pivot.Y))));
            }
        });
        Check("Format-all capture keeps a failing inner layout movable as a rigid box", () => {
            var doc = new GH_Document();
            var badNode = new TestComponent { NickName = "Bad attributes", InitialPivot = new PointF(0,0) };
            var goodNode = new TestComponent { NickName = "Good", InitialPivot = new PointF(0,0) };
            var bad = new GH_Group { NickName = "Failed", Document = doc }; bad.ObjectIDs.Add(badNode.InstanceGuid);
            var good = new GH_Group { NickName = "Good", Document = doc }; good.ObjectIDs.Add(goodNode.InstanceGuid);
            doc.Objects.AddRange(new IGH_DocumentObject[] { badNode,goodNode,bad,good });
            badNode.CreateAttributes(); badNode.Attributes.PerformLayout(); bad.CreateAttributes(); bad.Attributes.PerformLayout();
            var original = badNode.Attributes.Pivot;
            badNode.Attributes.OnLayout = () => throw new InvalidOperationException("injected inner layout failure");
            var c = FormatAllCapture.Capture(doc,new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && r.Formatted == 1 && r.Skipped == 1
                && r.Layout.Log.Any(l => l.Contains("Skipped group 'Failed'") && l.Contains("injected inner layout failure")), "inner failure stopped other groups");
            var target = c.Snapshot.Targets.Single(t => t.Id == bad.InstanceGuid);
            var rigid = r.Layout.GroupBoxes[bad.InstanceGuid];
            Assert(!target.Fixed && r.Layout.Tops.ContainsKey(badNode.InstanceGuid) && c.Objects.ContainsKey(badNode.InstanceGuid)
                && Math.Abs(r.Layout.Tops[badNode.InstanceGuid].X - badNode.Attributes.Bounds.X - rigid.X + target.Bounds.X) < .001
                && Math.Abs(r.Layout.Tops[badNode.InstanceGuid].Y - badNode.Attributes.Bounds.Y - rigid.Y + target.Bounds.Y) < .001
                && badNode.Attributes.Pivot == original, "failed group did not take part as a rigid box");
        });
        Check("Format-all capture recovers label overlap, empty nested groups and empty-bounds nodes", () => {
            var f = new Fixture();
            var label = new GH_Group { NickName = "Non-nested B label" }; label.ObjectIDs.Add(f.B.InstanceGuid);
            var empty = new GH_Group { NickName = "Empty" }; var wrapper = new GH_Group { NickName = "Groups only" }; wrapper.ObjectIDs.Add(empty.InstanceGuid);
            var bad = new TestComponent { NickName = "Empty bounds", Size = new SizeF(0,0), InitialPivot = new PointF(100,100) };
            f.Inner.ObjectIDs.AddRange(new[] { wrapper.InstanceGuid,bad.InstanceGuid });
            f.Document.Objects.AddRange(new IGH_DocumentObject[] { label,empty,wrapper,bad }); f.Document.SelectedObjects();
            Assert(FormatAllCapture.EligibleCount(f.Document) == 2,"recoverable targets excluded by preflight");
            var selected = SelectionSnapshot.Capture(f.Document,new Options(),new[] { f.Outer });
            Assert(selected.Diagnostic == null && selected.Skipped.Any(l => l.Contains("Empty bounds")),"FF capture refused recoverable members");
            var c = FormatAllCapture.Capture(f.Document,new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(c.Snapshot.Targets.All(t => t.SkipReason == null) && c.Log.Any(l => l.Contains("empty bounds"))
                && r.Layout.Success && !r.Layout.Unstable && r.Skipped == 0 && r.Formatted == 2 && !r.Layout.Tops.ContainsKey(bad.InstanceGuid),"capture skipped a recoverable root");
            Assert(FormatApplier.Apply(f.Document,r.Layout.Moves,c.Objects,out var status,true,true),status);
            c = FormatAllCapture.Capture(f.Document,new Options()); r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && !r.Layout.Unstable && r.Layout.Moves.Count == 0,"recovered capture not fixed after apply: " + r.Layout.Diagnostic + " " + string.Join(";",r.Layout.Log) + " moves=" + string.Join(";",r.Layout.Moves.Select(m => c.Objects[m.Id].NickName + "=" + (m.Pivot.X-c.Objects[m.Id].Attributes.Pivot.X) + "," + (m.Pivot.Y-c.Objects[m.Id].Attributes.Pivot.Y))));
        });
        Check("Format-all preflight counts an unformattable dataflow cycle as a movable rigid root", () => {
            var doc = new GH_Document();
            var a = new TestComponent { InitialPivot = new PointF(20,30) }; var b = new TestComponent { InitialPivot = new PointF(35,45) };
            var root = new GH_Group { NickName = "Dataflow cycle", Document = doc }; root.ObjectIDs.AddRange(new[] { a.InstanceGuid,b.InstanceGuid });
            TestParam.Connect(a.Params.Output[0],b.Params.Input[0]); TestParam.Connect(b.Params.Output[0],a.Params.Input[0]);
            doc.Objects.AddRange(new IGH_DocumentObject[] { a,b,root });
            Assert(FormatAllCapture.EligibleCount(doc) == 1 && a.Attributes == null,"preflight excluded movable failure or performed layout");
            var c = FormatAllCapture.Capture(doc,new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && r.Skipped == 1 && !c.Snapshot.Targets.Single().Fixed && r.Layout.Tops.Count == 2
                && r.Layout.Log.Any(l => l.Contains("Cycle") && l.Contains("moves rigidly")),"failed root was fixed");
        });
        Check("Format-all skipped root translates every member rigidly and leaves fixed objects untouched", () => {
            var doc = new GH_Document();
            var a = new TestComponent { InitialPivot = new PointF(20,30) }; var b = new TestComponent { InitialPivot = new PointF(35,45) };
            var root = new GH_Group { NickName = "Rigid failure", Document = doc }; root.ObjectIDs.AddRange(new[] { a.InstanceGuid,b.InstanceGuid });
            var fixedNode = new TestComponent { InitialPivot = new PointF(20,110) };
            doc.Objects.AddRange(new IGH_DocumentObject[] { a,b,root,fixedNode });
            foreach (var obj in doc.Objects) { obj.CreateAttributes(); obj.Attributes.PerformLayout(); }
            var beforeA = a.Attributes.Pivot; var beforeB = b.Attributes.Pivot; var beforeFixed = fixedNode.Attributes.Pivot;
            a.Attributes.OnLayout = () => throw new InvalidOperationException("injected failure");
            var c = FormatAllCapture.Capture(doc,new Options()); var r = FormatAllSolver.Solve(c.Snapshot);
            Assert(r.Layout.Success && r.Skipped == 1 && !c.Snapshot.Targets.Single().Fixed && r.Layout.Moves.Count == 2,"skipped root did not move rigidly");
            a.Attributes.OnLayout = a.Layout;
            Assert(FormatApplier.Apply(doc,r.Layout.Moves,c.Objects,out var status,true,true),status);
            Assert(a.Attributes.Pivot.X == beforeA.X && b.Attributes.Pivot.X == beforeB.X
                && a.Attributes.Pivot.Y > beforeA.Y && a.Attributes.Pivot.Y - beforeA.Y == b.Attributes.Pivot.Y - beforeB.Y
                && fixedNode.Attributes.Pivot == beforeFixed,"rigid or fixed-object delta changed");
        });
        return failures;
    }
    private sealed class Fixture
    {
        internal readonly GH_Document Document = new GH_Document();
        internal readonly TestComponent A = new TestComponent(0, 1) { NickName = "A", InitialPivot = new PointF(80, 250) };
        internal readonly TestComponent B = new TestComponent(1, 1) { NickName = "B", InitialPivot = new PointF(300, 50) };
        internal readonly TestComponent C = new TestComponent(1, 1) { NickName = "C", InitialPivot = new PointF(200, 200) };
        internal readonly TestComponent Loose = new TestComponent(1, 0) { NickName = "Loose", InitialPivot = new PointF(20, 500) };
        internal readonly TestObject Notes = new TestObject { NickName = "Notes", InitialPivot = new PointF(0, 0) };
        internal readonly TestObject Unrelated = new TestObject { NickName = "Untouched" };
        internal readonly GH_Group Label = new GH_Group { NickName = "B label" };
        internal readonly GH_Group Inner = new GH_Group { NickName = "Inner" };
        internal readonly GH_Group Outer = new GH_Group { NickName = "Outer", PadLeft = 13, PadTop = 27, PadRight = 19, PadBottom = 11 };
        internal Fixture()
        {
            Label.ObjectIDs.Add(B.InstanceGuid); Inner.ObjectIDs.AddRange(new[] { Label.InstanceGuid, B.InstanceGuid, C.InstanceGuid });
            Outer.ObjectIDs.AddRange(new[] { A.InstanceGuid, Inner.InstanceGuid, B.InstanceGuid, Notes.InstanceGuid });
            TestParam.Connect(A.Params.Output[0], B.Params.Input[0]); TestParam.Connect(B.Params.Output[0], C.Params.Input[0]); TestParam.Connect(C.Params.Output[0], Loose.Params.Input[0]);
            Document.Objects.AddRange(new IGH_DocumentObject[] { A, B, C, Loose, Notes, Label, Inner, Outer, Unrelated });
            // Explicit descendants are covered by the selected ancestor.
            Document.Selection.AddRange(new IGH_DocumentObject[] { Outer, Inner, B, Loose });
        }
    }
}
