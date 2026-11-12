using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperFormat;
using HopperFormat.Layout;

internal static class HostAdapterTests
{
    private static int _failures;
    internal static int Run()
    {
        Check("Snapshot includes selected group descendants and reports skipped loose objects", () =>
        {
            var document = new GH_Document();
            var selected = new TestComponent { NickName = "", Name = "Fallback name" };
            var unselected = new TestComponent();
            var group = new GH_Group { NickName = "Selected group" };
            group.ObjectIDs.Add(unselected.InstanceGuid);
            var empty = new TestParam { Size = SizeF.Empty, NickName = "Empty param" };
            var scribble = new TestObject { NickName = "Scribble" };
            var formatter = new HopperFormatComponent();
            document.Objects.AddRange(new IGH_DocumentObject[] { selected, unselected, group, empty, scribble, formatter });
            document.Selection.AddRange(new IGH_DocumentObject[] { selected, group, empty, scribble, formatter });
            var capture = SelectionSnapshot.Capture(document, new Options());
            Assert(capture.Objects.Keys.ToHashSet().SetEquals(new[] { selected.InstanceGuid, unselected.InstanceGuid }), "group descendants missing or excluded objects included");
            Assert(capture.Skipped.Count == 3 && capture.Skipped.Any(line => line.Contains("Empty param") && line.Contains("empty bounds"))
                && capture.Skipped.Any(line => line.Contains("TestObject 'Scribble'"))
                && capture.Skipped.Any(line => line.Contains("HopperFormatComponent")), "some skipped objects were silent");
            Assert(capture.Snapshot.Nodes.Single(n => n.Id == selected.InstanceGuid).Label == "Fallback name" && selected.Attributes.Layouts == 1
                && empty.Attributes.Layouts == 1 && unselected.Attributes.Layouts == 1 && capture.Hierarchy.Groups.Count == 1, "attributes/layout/fallback label incorrect");
        });
        Check("Snapshot measures bounds offsets, real grips, topology and external hints", () =>
        {
            var document = new GH_Document();
            var a = new TestComponent(0, 2) { InitialPivot = new PointF(10, 30) };
            var b = new TestComponent(2, 2) { InitialPivot = new PointF(220, 90) };
            var widget = new TestParam { InitialPivot = new PointF(30, 140) };
            var relay = new TestParam { InitialPivot = new PointF(410, 100) };
            var panel = new GH_Panel { InitialPivot = new PointF(250, 220) };
            var externalSource = new TestComponent(0, 1) { InitialPivot = new PointF(-200, 310) };
            var externalTarget = new TestComponent(2, 0) { InitialPivot = new PointF(600, 450) };
            TestParam.Connect(a.Params.Output[1], b.Params.Input[0]);
            TestParam.Connect(widget, b.Params.Input[1]);
            TestParam.Connect(b.Params.Output[0], relay);
            TestParam.Connect(externalSource.Params.Output[0], b.Params.Input[0]);
            TestParam.Connect(relay, externalTarget.Params.Input[0]);
            TestParam.Connect(b.Params.Output[1], externalTarget.Params.Input[1]);
            document.Objects.AddRange(new IGH_DocumentObject[] { a, b, widget, relay, panel, externalSource, externalTarget });
            document.Selection.AddRange(new IGH_DocumentObject[] { a, b, widget, relay, panel });
            var options = new Options { GapX = 41, GapY = 17, StackGap = 8 };
            var snapshot = SelectionSnapshot.Capture(document, options).Snapshot;
            var node = snapshot.Nodes.Single(n => n.Id == b.InstanceGuid);
            Assert(snapshot.Nodes.Where(n => n.Id == a.InstanceGuid || n.Id == b.InstanceGuid)
                .All(n => n.Kind == NodeKind.Component)
                && snapshot.Nodes.Where(n => n.Id == widget.InstanceGuid || n.Id == relay.InstanceGuid)
                    .All(n => n.Kind == NodeKind.Param)
                && snapshot.Nodes.Single(n => n.Id == panel.InstanceGuid).Kind == NodeKind.Panel,
                "component, floating param or panel kind incorrect");
            Assert(ReferenceEquals(snapshot.Options, options) && node.CurrentTop.X == 213 && node.CurrentTop.Y == 81
                && node.PivotOffset.X == 7 && node.PivotOffset.Y == 9 && node.Width == 80 && node.Height == 40,
                "bounds or pivot offset was derived incorrectly");
            Assert(node.Inputs[1].X == -5 && node.Inputs[1].Y == 25 && node.Outputs[1].X == 86
                && node.Outputs[1].Y == 27, "grips did not use actual measured port positions");
            Assert(snapshot.Nodes.Single(n => n.Id == widget.InstanceGuid).Widget
                && !snapshot.Nodes.Single(n => n.Id == relay.InstanceGuid).Widget
                && snapshot.Nodes.Single(n => n.Id == relay.InstanceGuid).Inputs.Length == 1
                && snapshot.Nodes.Single(n => n.Id == relay.InstanceGuid).Outputs.Length == 1, "floating param/widget model incorrect");
            Assert(snapshot.Edges.Count == 3 && snapshot.Edges.Any(e => e.From == a.InstanceGuid && e.FromPort == 1
                && e.To == b.InstanceGuid && e.ToPort == 0), "internal edges were duplicated or port indices lost");
            Assert(snapshot.Inbound.Count == 1 && snapshot.Inbound[0].Node == b.InstanceGuid
                && snapshot.Inbound[0].SourceGripX == externalSource.Params.Output[0].Attributes.OutputGrip.X
                && snapshot.Inbound[0].SourceObjectId == externalSource.InstanceGuid
                && snapshot.Inbound[0].SourceGripY == externalSource.Params.Output[0].Attributes.OutputGrip.Y
                && snapshot.Outbound.Count == 2 && snapshot.Outbound.Any(e => e.Node == b.InstanceGuid
                    && e.Port == 1 && e.TargetGripY == externalTarget.Params.Input[1].Attributes.InputGrip.Y
                    && e.TargetGripX == externalTarget.Params.Input[1].Attributes.InputGrip.X
                    && e.TargetObjectId == externalTarget.InstanceGuid), "external hints incorrect");
            var result = LayoutSolver.Solve(snapshot);
            Assert(result.Success, result.Diagnostic);
            var capture = SelectionSnapshot.Capture(document, options);
            Assert(FormatApplier.Apply(document, result.Moves, capture.Objects, out _), "snapshot moves failed to apply");
            Assert(LayoutSolver.Solve(SelectionSnapshot.Capture(document, options).Snapshot).Moves.Count == 0,
                "remeasured adapter output was not a fixed point");
        });
        Check("Snapshot keeps an internal wire internal when its source param has no attributes", () =>
        {
            var document = new GH_Document();
            var source = new TestComponent(0, 1);
            var target = new TestComponent(1, 0);
            TestParam.Connect(source.Params.Output[0], target.Params.Input[0]);
            document.Objects.AddRange(new[] { source, target });
            document.Selection.AddRange(new[] { source, target });
            source.CreateAttributes(); target.CreateAttributes();
            var port = (TestParam)source.Params.Output[0];
            // Lose the port attributes after the source node is measured, before wire capture.
            target.Attributes.OnLayout = () => { target.Layout(); port.ClearAttributes(); };
            var snapshot = SelectionSnapshot.Capture(document, new Options()).Snapshot;
            Assert(port.Attributes == null && snapshot.Edges.Count == 1 && snapshot.Inbound.Count == 0
                && snapshot.Outbound.Count == 0 && snapshot.Edges[0].From == source.InstanceGuid
                && snapshot.Edges[0].FromPort == 0 && snapshot.Edges[0].To == target.InstanceGuid,
                "source port gained floating attributes or its internal edge became external");

            var orphan = new TestParam { Kind = GH_ParamKind.output, NickName = "Orphan output" };
            target.Params.Input[0].Sources.Clear();
            TestParam.Connect(orphan, target.Params.Input[0]);
            source.CreateAttributes();
            try
            {
                SelectionSnapshot.Capture(document, new Options());
                throw new Exception("unresolved component port was accepted");
            }
            catch (InvalidOperationException error)
            {
                Assert(error.Message.Contains("Orphan output") && orphan.Attributes == null,
                    "unresolved owner did not name the param or created floating attributes");
            }
        });
        Check("Lock checks follow nested and overlapping membership and tolerate cycles", () =>
        {
            var document = new GH_Document();
            var node = new TestParam();
            var child = new GH_Group { NickName = "Child" };
            var parent = new GH_Group { NickName = "Outer [LoCk]" };
            var overlap = new GH_Group { NickName = "Other" };
            child.ObjectIDs.Add(node.InstanceGuid); parent.ObjectIDs.Add(child.InstanceGuid);
            child.ObjectIDs.Add(parent.InstanceGuid); overlap.ObjectIDs.Add(node.InstanceGuid);
            document.Objects.AddRange(new IGH_DocumentObject[] { node, child, parent, overlap });
            Assert(GroupMembership.Ancestors(document, new[] { node.InstanceGuid }).Count == 3, "nested/cyclic ancestors missing or duplicated");
            Assert(GroupMembership.LockedDiagnostic(document, new[] { node.InstanceGuid }).Contains("Outer [LoCk]"), "locked ancestor was not named");
            Assert(GroupMembership.LockedDiagnostic(document, new[] { Guid.NewGuid() }) == null, "unrelated lock blocked selection");
        });
        Check("Apply snapshots all pivots before writes and records once after group refresh", () =>
        {
            var (document, objects, moves) = ApplyFixture();
            var before = objects.ToDictionary(pair => pair.Key, pair => pair.Value.Attributes.Pivot);
            var child = new GH_Group(); child.ObjectIDs.Add(moves[0].Id);
            var parent = new GH_Group(); parent.ObjectIDs.Add(child.InstanceGuid);
            var unrelated = new GH_Group(); document.Objects.AddRange(new[] { child, parent, unrelated });
            document.UndoUtil.OnRecord = () => Assert(child.CacheExpirations == 1 && parent.CacheExpirations == 1
                && unrelated.CacheExpirations == 0, "undo recorded before affected group caches expired");
            Assert(FormatApplier.Apply(document, moves, objects, out var status) && status == "Formatted selection · 2 moves", "successful apply failed");
            var record = document.UndoUtil.Records.Single();
            Assert(document.UndoUtil.Creates == 1 && record.Name == "Format selection" && record.Before.Count == 2
                && record.Before.All(pair => pair.Value == before[pair.Key.InstanceGuid]), "undo did not snapshot original pivots once");
            Assert(moves.All(move => objects[move.Id].Attributes.Pivot == new PointF((float)move.Pivot.X, (float)move.Pivot.Y)), "emitted pivots were not applied");
        });
        Check("Pivot mismatch restores every original and records no undo event", () =>
        {
            var (document, objects, moves) = ApplyFixture();
            var before = objects.ToDictionary(pair => pair.Key, pair => pair.Value.Attributes.Pivot);
            objects[moves[1].Id].Attributes.TransformPivot = point => point.X > 100 ? new PointF(point.X + 1, point.Y) : point;
            Assert(!FormatApplier.Apply(document, moves, objects, out var status) && status.Contains("pivot mismatch"), "mismatch was accepted");
            Assert(document.UndoUtil.Records.Count == 0 && objects.All(pair => pair.Value.Attributes.Pivot == before[pair.Key])
                && objects.All(pair => pair.Value.Attributes.Layouts >= 2), "rollback was partial or recorded undo");
        });
        Check("Layout exception restores earlier moves and attempts every remaining restore", () =>
        {
            var (document, objects, moves) = ApplyFixture();
            var before = objects.ToDictionary(pair => pair.Key, pair => pair.Value.Attributes.Pivot);
            var second = objects[moves[1].Id];
            second.Attributes.OnLayout = () => { if (second.Attributes.Pivot.X > 100) throw new InvalidOperationException("injected layout failure"); };
            Assert(!FormatApplier.Apply(document, moves, objects, out var status) && status.Contains("injected layout failure"), "layout exception escaped apply");
            Assert(document.UndoUtil.Records.Count == 0 && objects.All(pair => pair.Value.Attributes.Pivot == before[pair.Key]), "exception left objects moved");
        });
        Check("Rejected undo recording rolls pivots back", () =>
        {
            var (document, objects, moves) = ApplyFixture();
            var before = objects.ToDictionary(pair => pair.Key, pair => pair.Value.Attributes.Pivot);
            document.UndoUtil.Reject = true;
            Assert(!FormatApplier.Apply(document, moves, objects, out _) && document.UndoUtil.Records.Count == 0
                && objects.All(pair => pair.Value.Attributes.Pivot == before[pair.Key]), "unrecorded moves survived");
        });
        Check("Zero moves creates no undo record and reports Already formatted", () =>
        {
            var (document, objects, _) = ApplyFixture();
            Assert(FormatApplier.Apply(document, Array.Empty<Move>(), objects, out var status) && status == "Already formatted"
                && document.UndoUtil.Creates == 0 && document.UndoUtil.Records.Count == 0, "no-op generated undo");
        });
        return _failures + GroupHostTests.Run();
    }

    private static (GH_Document, Dictionary<Guid, IGH_DocumentObject>, List<Move>) ApplyFixture()
    {
        var document = new GH_Document();
        var nodes = new[] { new TestComponent(), new TestComponent() };
        foreach (var node in nodes) { node.CreateAttributes(); document.Objects.Add(node); }
        var objects = nodes.ToDictionary(node => node.InstanceGuid, node => (IGH_DocumentObject)node);
        var moves = nodes.Select((node, i) => new Move { Id = node.InstanceGuid, Pivot = new Vec2(200 + i * 100, 400) }).ToList();
        return (document, objects, moves);
    }
    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
