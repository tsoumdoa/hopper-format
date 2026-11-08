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
            var f = new Fixture(); var other = new GH_Group { NickName = "Other tree" }; other.ObjectIDs.Add(f.B.InstanceGuid); f.Document.Objects.Add(other);
            var c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(c.Diagnostic.Contains(f.B.NickName), "outside ownership accepted");
            f.Document.Objects.Remove(other); f.Outer.NickName = "Protected [LoCk]";
            c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(GroupMembership.LockedDiagnostic(f.Document, c.CandidateIds).Contains("Protected"), "selected lock accepted");
            f.Document.Selection.Clear(); f.Document.Selection.Add(f.B);
            c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(GroupMembership.LockedDiagnostic(f.Document, c.CandidateIds).Contains("Protected"), "node in locked ancestor accepted");
            f.Document.Selection.Clear(); f.Document.Selection.Add(f.Outer); f.Outer.NickName = "Outer"; f.Inner.NickName = "Inner [lock]";
            c = SelectionSnapshot.Capture(f.Document, new Options()); Assert(GroupMembership.LockedDiagnostic(f.Document, c.CandidateIds).Contains("Inner"), "locked descendant accepted");
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
