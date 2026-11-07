using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HopperFormat.Layout;

internal static class Program
{
    private static int failures;

    private static void Check(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Near(double actual, double expected, string message, double tolerance = 1e-8)
    {
        Assert(Math.Abs(actual - expected) <= tolerance, message + ": expected " + expected + ", got " + actual);
    }

    private static Guid Id(int i) { return new Guid(i, 0, 0, new byte[8]); }

    private static Node Component(int id, string label, double x, double y, int inputs = 2,
        double width = 90, double height = 44, int outputs = 1)
    {
        return new Node { Id = Id(id), Label = label, Width = width, Height = height,
            CurrentTop = new Vec2(x, y), PivotOffset = new Vec2(12, 7),
            Inputs = Enumerable.Range(0, inputs).Select(p => new Vec2(0, (p + 0.5) * height / inputs)).ToArray(),
            Outputs = Enumerable.Range(0, outputs).Select(p => new Vec2(width, (p + 0.5) * height / outputs)).ToArray() };
    }

    private static Node Slider(int id, string label, double x, double y)
    {
        var node = Component(id, label, x, y, 0, 160, 20);
        node.Widget = true;
        node.Kind = NodeKind.Param;
        return node;
    }

    private static Node Param(int id, string label, double x, double y)
    {
        var node = Component(id, label, x, y, 1, 70, 20);
        node.Kind = NodeKind.Param;
        return node;
    }

    private static Edge Wire(Node from, Node to, int input = 0, int output = 0)
    {
        return new Edge { From = from.Id, To = to.Id, FromPort = output, ToPort = input };
    }

    private static Snapshot Make(Node[] nodes, params Edge[] edges) { return new Snapshot { Nodes = nodes, Edges = edges }; }

    private static Snapshot Applied(Snapshot snapshot, Result result)
    {
        return new Snapshot { Nodes = snapshot.Nodes.Select(n => new Node { Id = n.Id, Label = n.Label,
            CurrentTop = result.Tops[n.Id], PivotOffset = n.PivotOffset, Width = n.Width, Height = n.Height,
            Kind = n.Kind, Widget = n.Widget, Inputs = n.Inputs, Outputs = n.Outputs }).ToArray(), Edges = snapshot.Edges,
            Inbound = snapshot.Inbound, Outbound = snapshot.Outbound, Options = snapshot.Options };
    }

    private static Result Solve(Snapshot snapshot)
    {
        var result = LayoutSolver.Solve(snapshot);
        Assert(result.Success, result.Diagnostic);
        Assert(!result.Unstable, string.Join(" ", result.Log));
        Assert(result.Rounds >= 1 && result.Rounds <= 3, "round count out of range");
        return result;
    }

    private static void Horizontal(Result result, Node from, Node to, int input = 0, int output = 0)
    {
        Near(result.Tops[from.Id].Y + from.Outputs[output].Y,
            result.Tops[to.Id].Y + to.Inputs[input].Y, from.Label + " -> " + to.Label + " horizontal");
    }

    private static void NoOverlap(Snapshot snapshot, Result result)
    {
        for (int i = 0; i < snapshot.Nodes.Count; i++)
            for (int j = i + 1; j < snapshot.Nodes.Count; j++)
            {
                var a = snapshot.Nodes[i]; var b = snapshot.Nodes[j];
                var at = result.Tops[a.Id]; var bt = result.Tops[b.Id];
                Assert(at.X + a.Width <= bt.X + 1e-8 || bt.X + b.Width <= at.X + 1e-8 ||
                    at.Y + a.Height <= bt.Y + 1e-8 || bt.Y + b.Height <= at.Y + 1e-8,
                    "overlap: " + a.Label + " and " + b.Label);
            }
    }

    private static void Dataflow(Snapshot snapshot, Result result)
    {
        var nodes = snapshot.Nodes.ToDictionary(n => n.Id);
        foreach (var edge in snapshot.Edges)
            Assert(result.Tops[edge.From].X + nodes[edge.From].Width <= result.Tops[edge.To].X + 1e-8,
                "backward wire: " + nodes[edge.From].Label + " -> " + nodes[edge.To].Label);
    }

    private static void Anchored(Snapshot snapshot, Result result)
    {
        if (snapshot.Nodes.Count == 0) return;
        Near(result.Anchor.X, snapshot.Nodes.Min(n => n.CurrentTop.X), "reported anchor X");
        Near(result.Anchor.Y, snapshot.Nodes.Min(n => n.CurrentTop.Y), "reported anchor Y");
        Near(result.Tops.Values.Min(t => t.X), result.Anchor.X, "placed anchor X", 1);
        Near(result.Tops.Values.Min(t => t.Y), result.Anchor.Y, "placed anchor Y", 1);
    }

    private static void Idempotent(Snapshot snapshot, Result result)
    {
        var again = Solve(Applied(snapshot, result));
        Assert(again.Moves.Count == 0, "second solve moved " + again.Moves.Count + " nodes");
        foreach (var node in snapshot.Nodes)
        {
            Near(again.Tops[node.Id].X, result.Tops[node.Id].X, "fixed top X");
            Near(again.Tops[node.Id].Y, result.Tops[node.Id].Y, "fixed top Y");
        }
    }

    private static void Guarantees(Snapshot snapshot, Result result)
    {
        NoOverlap(snapshot, result); Dataflow(snapshot, result); Anchored(snapshot, result); Idempotent(snapshot, result);
    }

    private static void GoldenTests()
    {
        Check("Owner's param interface: last-column outputs, clear top lane and aligned receivers", () => {
            var rec = Param(1, "Rec", 0, 0); rec.Widget = true;
            var pln = Param(2, "Pln", 120, 0);
            var explode = Component(3, "Explode", 120, 50, 2, 90, 80, 2);
            var item = Component(4, "Item", 250, 60, 3, 100, 110, 4);
            item.Inputs = new[] { new Vec2(0, 22), new Vec2(0, 55), new Vec2(0, 88) };
            item.Outputs = new[] { new Vec2(100, 22), new Vec2(100, 44), new Vec2(100, 66), new Vec2(100, 88) };
            var bottomLeft = Param(5, "Pt BottomLeft", 400, 60);
            var decon = Component(6, "pDecon", 400, 150, 1, 90, 90, 3);
            var num = Param(7, "Num", 550, 150);
            var topLeft = Param(8, "Pt TopLeft", 400, 250);
            var panel = Component(9, "Panel", 0, 200, 1, 140, 130); panel.Kind = NodeKind.Panel;
            var snapshot = Make(new[] { rec, pln, explode, item, bottomLeft, decon, num, topLeft, panel },
                Wire(rec, pln), Wire(rec, explode), Wire(explode, item, 0, 1),
                Wire(item, bottomLeft), Wire(item, decon, 0, 2), Wire(decon, num), Wire(item, topLeft, 0, 3));
            snapshot.Inbound = new[] { new ExternalInbound { Node = panel.Id, SourceGripY = 220 } };
            snapshot.Outbound = new[] { new ExternalOutbound { Node = rec.Id, TargetGripY = 10 } };
            var result = Solve(snapshot);
            Assert(new[] { rec, explode, item, decon }.Select(n => result.Ranks[n.Id])
                .SequenceEqual(new[] { 0, 1, 2, 3 }), "processing columns");
            var outputs = new[] { pln, bottomLeft, num, topLeft };
            Assert(outputs.All(n => result.Ranks[n.Id] == 4), "output params must share the last column");
            Assert(outputs.OrderBy(n => result.Tops[n.Id].Y).SequenceEqual(outputs), "output order");
            foreach (var output in outputs)
                Near(result.Tops[output.Id].X + output.Width, result.Tops[pln.Id].X + pln.Width, "output right alignment");
            Horizontal(result, rec, pln); Horizontal(result, item, bottomLeft); Horizontal(result, decon, num);
            double lane = result.Tops[rec.Id].Y + rec.Outputs[0].Y;
            Assert(result.Tops[explode.Id].Y >= lane + snapshot.Options.GapY, "Explode must clear the top lane");
            Guarantees(snapshot, result);
            Console.WriteLine("PASS Param interface coordinates: " + string.Join("; ", snapshot.Nodes.Select(n => n.Label +
                "=(" + Number(result.Tops[n.Id].X) + "," + Number(result.Tops[n.Id].Y) + "), column " + result.Ranks[n.Id])));
        });
        Check("Input params stay first while source-free widgets pull toward their consumer", () => {
            var chain = Enumerable.Range(1, 4).Select(i => Component(i, "Chain " + i, i * 150, i * 60, 3)).ToArray();
            var input = Param(5, "In", 0, 100); var slider = Slider(6, "Slider", 0, 200);
            var snapshot = Make(chain.Concat(new[] { input, slider }).ToArray(),
                Wire(chain[0], chain[1]), Wire(chain[1], chain[2]), Wire(chain[2], chain[3]),
                Wire(input, chain[3], 1), Wire(slider, chain[3], 2));
            snapshot.Inbound = new[] { new ExternalInbound { Node = input.Id, SourceGripY = 100 } };
            var result = Solve(snapshot);
            Assert(result.Ranks[input.Id] == 0 && result.Ranks[slider.Id] == 2 && result.Ranks[chain[3].Id] == 3,
                "input pin or widget pull incorrect");
            Guarantees(snapshot, result);
            snapshot.Options.ReturnConstraint = false;
            result = Solve(snapshot);
            Assert(result.Ranks[input.Id] == 2 && result.Ranks[slider.Id] == 2, "loose mode retained the input pin");
            Guarantees(snapshot, result);
        });
        Check("Terminal params pin last while Panels stay with their producer; loose disables output pins", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 150, 100);
            var c = Component(3, "C", 300, 100); var d = Component(4, "D", 450, 100);
            var output = Param(5, "Out", 150, 0);
            var panel = Component(6, "Panel", 150, 200, 1, 140, 130); panel.Kind = NodeKind.Panel;
            var snapshot = Make(new[] { a, b, c, d, output, panel },
                Wire(a, b), Wire(b, c), Wire(c, d), Wire(a, output), Wire(a, panel));
            var result = Solve(snapshot);
            Assert(result.Ranks[output.Id] == 3 && result.Ranks[panel.Id] == 1, "Param/Panel output distinction lost");
            Guarantees(snapshot, result);
            snapshot.Options.ReturnConstraint = false;
            result = Solve(snapshot);
            Assert(result.Ranks[output.Id] == 1 && result.Ranks[panel.Id] == 1, "loose mode retained the output pin");
            Guarantees(snapshot, result);
        });
        Check("Worked example 5.11: ranks, port order and horizontal main chain", () => {
            var a = Slider(1, "A", 10, 130); var b = Slider(2, "B", 5, 10);
            var p = Component(3, "Construct Point", 300, 80);
            var r = Slider(4, "R", 20, 0); var c = Component(5, "Circle", 550, 50);
            var s = Component(6, "Boundary Surfaces", 750, 30, 1);
            var snapshot = Make(new[] { a, b, p, r, c, s }, Wire(a, p), Wire(b, p, 1), Wire(p, c), Wire(r, c, 1), Wire(c, s));
            snapshot.Outbound = new[] { new ExternalOutbound { Node = s.Id, Port = 0, TargetGripY = 75 } };
            var result = Solve(snapshot);
            Assert(new[] { a, b, p, r, c, s }.Select(n => result.Ranks[n.Id]).SequenceEqual(new[] { 0, 0, 1, 1, 2, 3 }), "wrong columns");
            Assert(result.Tops[a.Id].Y < result.Tops[b.Id].Y, "A must be above B");
            Assert(result.Tops[p.Id].Y < result.Tops[r.Id].Y, "R must be below P");
            Near(result.Tops[b.Id].Y - result.Tops[a.Id].Y - a.Height, 6, "slider stack gap");
            Horizontal(result, p, c); Horizontal(result, c, s);
            Guarantees(snapshot, result);
        });
        Check("Diamond: split and join remain separated", () => {
            var a = Component(1, "Source", 0, 0); var b = Component(2, "Upper", 300, 120);
            var c = Component(3, "Lower", 250, 20); var d = Component(4, "Join", 500, 50);
            var snapshot = Make(new[] { a, b, c, d }, Wire(a, b), Wire(a, c), Wire(b, d), Wire(c, d, 1));
            var result = Solve(snapshot);
            Assert(result.Ranks[a.Id] == 0 && result.Ranks[b.Id] == 1 && result.Ranks[c.Id] == 1 && result.Ranks[d.Id] == 2, "diamond ranks");
            Guarantees(snapshot, result);
        });
        Check("Eight-slider fan-in: StackGap, input port order and upper-median alignment", () => {
            var sliders = Enumerable.Range(1, 8).Select(i => Slider(i, "Slider " + i, 0, (9 - i) * 60)).ToArray();
            var sink = Component(9, "Eight inputs", 400, 100, 8, 100, 176);
            var snapshot = Make(sliders.Concat(new[] { sink }).ToArray(), sliders.Select((n, i) => Wire(n, sink, i)).ToArray());
            var result = Solve(snapshot);
            for (int i = 1; i < sliders.Length; i++)
                Near(result.Tops[sliders[i].Id].Y - result.Tops[sliders[i - 1].Id].Y, 26, "widget stack spacing");
            Assert(result.CrossingsAfter == 0, "fanin crossings remain");
            Horizontal(result, sliders[3], sink, 3);
            Guarantees(snapshot, result);
        });
        Check("Six-node chain: all wires horizontal", () => {
            var nodes = Enumerable.Range(1, 6).Select(i => Component(i, "Chain " + i, 100 - i * 10, i * 50, 1)).ToArray();
            var snapshot = Make(nodes, Enumerable.Range(0, 5).Select(i => Wire(nodes[i], nodes[i + 1])).ToArray());
            var result = Solve(snapshot);
            for (int i = 0; i < 6; i++) Assert(result.Ranks[nodes[i].Id] == i, "chain rank");
            for (int i = 0; i < 5; i++) Horizontal(result, nodes[i], nodes[i + 1]);
            Guarantees(snapshot, result);
        });
        Check("Three-slider fan-in: median input is port 1", () => {
            var sliders = Enumerable.Range(1, 3).Select(i => Slider(i, "Input " + i, 0, (4 - i) * 60)).ToArray();
            var sink = Component(4, "Three inputs", 400, 0, 3, 70, 66);
            var snapshot = Make(sliders.Concat(new[] { sink }).ToArray(), sliders.Select((n, i) => Wire(n, sink, i)).ToArray());
            var result = Solve(snapshot);
            Horizontal(result, sliders[1], sink, 1);
            Guarantees(snapshot, result);
        });
        Check("Alignment falls back from an occupied median to the nearest free predecessor", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 100);
            var c = Component(3, "Upper", 200, 0); var d = Component(4, "Lower", 200, 100);
            var snapshot = Make(new[] { a, b, c, d }, Wire(a, c), Wire(b, c, 1), Wire(a, d), Wire(b, d, 1));
            var result = Solve(snapshot);
            Horizontal(result, a, c); Horizontal(result, b, d, 1);
            Guarantees(snapshot, result);
        });
        Check("Tall multi-input component: free lower input moves down to its target grip", () => {
            var a = Slider(1, "Top input", 0, 400); var b = Slider(2, "Bottom input", 0, 0);
            var tall = Component(3, "Tall", 300, 0, 4, 110, 320);
            var snapshot = Make(new[] { a, b, tall }, Wire(a, tall), Wire(b, tall, 3));
            var result = Solve(snapshot);
            Horizontal(result, a, tall);
            Horizontal(result, b, tall, 3);
            Near(result.Tops[b.Id].Y - result.Tops[a.Id].Y, 240, "balanced input spacing");
            Guarantees(snapshot, result);
        });
        Check("Owner's BBox fan-out: panel centres on Scale NU Y while the G lane stays level", () => {
            var bbox = Component(1, "BBox", 0, 60, 2, 70, 44, 2);
            var box = Component(2, "Box", 100, 0, 4, 70, 88, 3);
            var scale = Component(3, "Scale NU", 200, 50, 5, 80, 110, 2);
            var panel = Component(4, "1.6", 100, 150, 0, 70, 24);
            panel.Kind = NodeKind.Panel; panel.Widget = true;
            var snapshot = Make(new[] { bbox, box, scale, panel }, Wire(bbox, box), Wire(bbox, scale),
                Wire(box, scale, 1, 1), Wire(panel, scale, 2), Wire(panel, scale, 3), Wire(panel, scale, 4));
            snapshot.Inbound = new[] { new ExternalInbound { Node = bbox.Id, SourceGripY = 70 } };
            var result = Solve(snapshot);
            Assert(new[] { bbox, box, panel, scale }.Select(n => result.Ranks[n.Id])
                .SequenceEqual(new[] { 0, 1, 1, 2 }), "fan-out columns");
            Horizontal(result, panel, scale, 3); Horizontal(result, bbox, scale);
            double lane = result.Tops[bbox.Id].Y + bbox.Outputs[0].Y;
            Near(result.Tops[box.Id].Y, 0, "Box stays at the top");
            Near(lane, 104, "unchanged G lane");
            Near(result.Tops[bbox.Id].Y, 93, "BBox top");
            Near(result.Tops[scale.Id].Y, 93, "Scale NU top");
            Near(result.Tops[panel.Id].Y, 158, "balanced panel top");
            Assert(result.LaneEdges.Contains(snapshot.Edges[1]), "BBox -> G must reserve a lane");
            Assert(result.Tops[box.Id].Y + box.Height + snapshot.Options.GapY <= lane, "Box clears lane above");
            Assert(Math.Abs(result.Tops[bbox.Id].Y + bbox.Outputs[0].Y -
                result.Tops[box.Id].Y - box.Inputs[0].Y) > 1, "BBox -> Box must stay bent");
            Assert(Math.Abs(result.Tops[box.Id].Y + box.Outputs[1].Y -
                result.Tops[scale.Id].Y - scale.Inputs[1].Y) > 1, "Box -> P must stay bent");
            Guarantees(snapshot, result);
            Console.WriteLine("PASS BBox fan-out tops: " + string.Join("; ", snapshot.Nodes.Select(n => n.Label + "=(" +
                Number(result.Tops[n.Id].X) + "," + Number(result.Tops[n.Id].Y) + ")")) + "; lane y=" + Number(lane));
        });
        Check("Free single-wire source never moves upward from its compacted top", () => {
            var upper = Slider(1, "Upper", 0, 0); var lower = Slider(2, "Lower", 0, 100);
            var target = Component(3, "Target", 300, 0, 2);
            var snapshot = Make(new[] { upper, lower, target }, Wire(upper, target), Wire(lower, target, 1));
            var result = Solve(snapshot);
            Horizontal(result, upper, target);
            Near(result.Tops[lower.Id].Y - result.Tops[upper.Id].Y,
                upper.Height + snapshot.Options.StackGap, "lower source retains compacted top");
            Assert(result.Tops[lower.Id].Y + lower.Outputs[0].Y >
                result.Tops[target.Id].Y + target.Inputs[1].Y, "target input must be above free output");
            Guarantees(snapshot, result);
        });
        Check("Free source stops at the item below instead of overlapping it", () => {
            var bbox = Component(1, "BBox", 0, 60, 2, 70, 44, 2);
            var box = Component(2, "Box", 100, 0, 4, 70, 88, 3);
            var scale = Component(3, "Scale NU", 200, 50, 5, 80, 110, 2);
            var panel = Component(4, "1.6", 100, 150, 0, 70, 24); panel.Kind = NodeKind.Panel; panel.Widget = true;
            var below = Component(5, "Below", 100, 250, 0, 70, 24);
            var snapshot = Make(new[] { bbox, box, scale, panel, below }, Wire(bbox, box), Wire(bbox, scale),
                Wire(box, scale, 1, 1), Wire(panel, scale, 2), Wire(panel, scale, 3), Wire(panel, scale, 4));
            snapshot.Inbound = new[] { new ExternalInbound { Node = bbox.Id, SourceGripY = 70 } };
            var result = Solve(snapshot);
            Near(result.Tops[panel.Id].Y, 120, "panel at compacted limit");
            Near(result.Tops[panel.Id].Y + panel.Height + snapshot.Options.GapY,
                result.Tops[below.Id].Y, "maximum top allowed by item below");
            Assert(result.Tops[panel.Id].Y + panel.Outputs[0].Y <
                result.Tops[scale.Id].Y + scale.Inputs[3].Y, "panel must stop before reaching Y");
            Guarantees(snapshot, result);
        });
        Check("Parallel short wires align on their predecessor's upper-median input", () => {
            foreach (int count in new[] { 2, 3, 4 })
            {
                var panel = Component(1, "Panel", 0, 0, 0, 70, 24); panel.Widget = true; panel.Kind = NodeKind.Panel;
                var other = Slider(2, "Other", 0, 200);
                var target = Component(3, "Target", 300, 0, count + 2, 80, (count + 2) * 22);
                var snapshot = Make(new[] { panel, other, target }, Enumerable.Range(0, count)
                    .Select(p => Wire(panel, target, p)).Concat(new[] { Wire(other, target, count), Wire(other, target, count + 1) }).ToArray());
                var result = Solve(snapshot);
                Horizontal(result, panel, target, (count - 1) / 2);
                Guarantees(snapshot, result);
            }
        });
        Check("Parallel long wires reserve the upper-median input's lane", () => {
            foreach (int count in new[] { 2, 3, 4 })
            {
                var panel = Component(1, "Panel", 0, 0, 0, 70, 24); panel.Widget = true; panel.Kind = NodeKind.Panel;
                var middle = Component(2, "Middle", 100, 80, 1);
                var target = Component(3, "Target", 250, 0, count + 1, 80, (count + 1) * 22);
                var wires = Enumerable.Range(0, count).Select(p => Wire(panel, target, p)).ToArray();
                var snapshot = Make(new[] { panel, middle, target }, wires.Concat(new[] {
                    Wire(panel, middle), Wire(middle, target, count) }).ToArray());
                var result = Solve(snapshot);
                Horizontal(result, panel, target, (count - 1) / 2);
                Assert(result.LaneEdges.Count == 1 && result.LaneEdges.Contains(wires[(count - 1) / 2]), "median parallel lane");
                Guarantees(snapshot, result);
            }
        });
        Check("Owner's case: Panel-to-Join lane clears Cull and overrides the short input", () => {
            var txt0 = Param(1, "Txt0", 0, 0);
            var panel = Component(2, "Panel", 0, 40, 0, 70, 50); panel.Widget = true;
            panel.Kind = NodeKind.Panel;
            var split = Component(3, "Split", 100, 0, 2, 70, 44);
            var cull = Component(4, "Cull", 200, 0, 3, 70, 66);
            var join = Component(5, "Join", 300, 0, 2, 70, 44);
            var txt2 = Param(6, "Txt2", 400, 0);
            var snapshot = Make(new[] { txt0, panel, split, cull, join, txt2 },
                Wire(txt0, split), Wire(panel, split, 1), Wire(panel, join, 1),
                Wire(split, cull), Wire(cull, join), Wire(join, txt2));
            snapshot.Inbound = new[] { new ExternalInbound { Node = txt0.Id, SourceGripY = 10 } };
            var result = Solve(snapshot);
            Assert(snapshot.Nodes.Select(n => result.Ranks[n.Id]).SequenceEqual(new[] { 0, 0, 1, 2, 3, 4 }), "owner columns");
            Horizontal(result, panel, join, 1);
            double lane = result.Tops[panel.Id].Y + panel.Outputs[0].Y;
            Assert(lane >= result.Tops[cull.Id].Y + cull.Height + snapshot.Options.GapY ||
                lane <= result.Tops[cull.Id].Y - snapshot.Options.GapY, "lane does not clear Cull");
            Assert(lane >= result.Tops[cull.Id].Y + cull.Height + snapshot.Options.GapY, "owner lane must be below Cull");
            Horizontal(result, txt0, split); Horizontal(result, split, cull); Horizontal(result, join, txt2);
            var expected = new[] { new Vec2(0, 1), new Vec2(0, 68), new Vec2(100, 0),
                new Vec2(200, 11), new Vec2(300, 60), new Vec2(400, 72) };
            for (int i = 0; i < snapshot.Nodes.Count; i++)
            {
                Near(result.Tops[snapshot.Nodes[i].Id].X, expected[i].X, "owner X");
                Near(result.Tops[snapshot.Nodes[i].Id].Y, expected[i].Y, "owner Y");
            }
            Near(lane - result.Tops[cull.Id].Y - cull.Height, 16, "exact owner lane clearance");
            Guarantees(snapshot, result);
            Console.WriteLine("PASS Owner coordinates: " + string.Join("; ", snapshot.Nodes.Select(n => n.Label + "=(" +
                Number(result.Tops[n.Id].X) + "," + Number(result.Tops[n.Id].Y) + ")")) + "; lane y=" + Number(lane));
        });
        Check("Orphan panel: nearest main-island column", () => {
            var a = Component(1, "Source", 0, 20); var b = Component(2, "Sink", 300, 80);
            var panel = Component(3, "Orphan panel", 290, 200, 0, 110, 80);
            panel.Kind = NodeKind.Panel;
            var snapshot = Make(new[] { a, b, panel }, Wire(a, b));
            var result = Solve(snapshot);
            Assert(result.Ranks[panel.Id] == result.Ranks[b.Id], "orphan rank");
            Guarantees(snapshot, result);
        });
        Check("Wide orphan: recovered right-aligned column stays fixed", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 200, 0);
            var c = Component(3, "C", 400, 0); var panel = Component(4, "Wide orphan", 390, 200, 0, 800, 80);
            panel.Kind = NodeKind.Panel;
            var snapshot = Make(new[] { a, b, c, panel }, Wire(a, b), Wire(b, c));
            var result = Solve(snapshot);
            Assert(result.Ranks[panel.Id] == 2, "wide orphan lost its original nearest column");
            Assert(Solve(Applied(snapshot, result)).Ranks[panel.Id] == 2, "wide orphan drifted to another column");
            Guarantees(snapshot, result);
        });
        Check("Two islands: largest first, independent return ranks and left alignment", () => {
            var a = Component(1, "Main A", 200, 500); var b = Component(2, "Main B", 450, 700);
            var c = Component(3, "Main C", 700, 600); var x = Component(4, "Small X", -50, -100);
            var y = Component(5, "Small Y", 10, -50);
            var snapshot = Make(new[] { a, b, c, x, y }, Wire(a, b), Wire(b, c), Wire(x, y));
            snapshot.Outbound = new[] { new ExternalOutbound { Node = y.Id, TargetGripY = 30 } };
            var result = Solve(snapshot);
            Assert(result.Ranks[y.Id] == 1, "small island inherited main max rank");
            double mainBottom = new[] { a, b, c }.Max(n => result.Tops[n.Id].Y + n.Height);
            Near(new[] { x, y }.Min(n => result.Tops[n.Id].Y) - mainBottom, 32, "island gap");
            Near(result.Tops[a.Id].X, -50, "main left"); Near(result.Tops[x.Id].X, -50, "small left");
            Guarantees(snapshot, result);
        });
        Check("Main island ties and remaining islands follow original top order", () => {
            var nodes = Enumerable.Range(1, 6).Select(i => Component(i, "Island " + i, 0, i * 100)).ToArray();
            var snapshot = Make(nodes, Wire(nodes[4], nodes[5]), Wire(nodes[2], nodes[3]), Wire(nodes[0], nodes[1]));
            var result = Solve(snapshot);
            Assert(result.Tops[nodes[0].Id].Y < result.Tops[nodes[2].Id].Y && result.Tops[nodes[2].Id].Y < result.Tops[nodes[4].Id].Y, "island order");
            Guarantees(snapshot, result);
        });
        Check("All-unconnected selection: one left-aligned column in top order", () => {
            var a = Component(1, "First", 400, 10); var b = Slider(2, "Second", -100, 80);
            var c = Component(3, "Third", 10, 200, 1, 130, 80);
            var snapshot = Make(new[] { c, b, a });
            snapshot.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripY = 900 } };
            snapshot.Outbound = new[] { new ExternalOutbound { Node = c.Id, TargetGripY = -900 } };
            var result = Solve(snapshot);
            foreach (var n in snapshot.Nodes) { Near(result.Tops[n.Id].X, -100, "column X"); Assert(result.Ranks[n.Id] == 0, "orphan rank"); }
            Near(result.Tops[a.Id].Y, 10, "first Y"); Near(result.Tops[b.Id].Y, 70, "second Y"); Near(result.Tops[c.Id].Y, 106, "third Y");
            Guarantees(snapshot, result);
        });
        Check("Return constraint: sink pulled before feeders, optional loose ranks", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 200, 0);
            var c = Component(3, "C", 400, 0); var d = Component(4, "Return", 200, 100);
            var snapshot = Make(new[] { a, b, c, d }, Wire(a, b), Wire(b, c), Wire(a, d));
            snapshot.Outbound = new[] { new ExternalOutbound { Node = d.Id, TargetGripY = 100 } };
            Assert(Solve(snapshot).Ranks[d.Id] == 2, "return not fixed at max rank");
            snapshot.Options.ReturnConstraint = false;
            Assert(Solve(snapshot).Ranks[d.Id] == 1, "return constraint ignored option");
        });
        Check("Columns right-align unequal widths and retain GapX", () => {
            var a = Component(1, "Narrow", 0, 0, 1, 50); var b = Component(2, "Wide", 0, 100, 1, 140);
            var c = Component(3, "Consumer", 400, 0);
            var snapshot = Make(new[] { a, b, c }, Wire(a, c), Wire(b, c, 1));
            var result = Solve(snapshot);
            Near(result.Tops[a.Id].X + a.Width, result.Tops[b.Id].X + b.Width, "right aligned");
            Near(result.Tops[c.Id].X - result.Tops[b.Id].X - b.Width, 30, "column gap");
            Guarantees(snapshot, result);
        });
        Check("Custom gap options control columns, widget stacks and ordinary rows", () => {
            var a = Slider(1, "A", 0, 0); var b = Slider(2, "B", 0, 100);
            var c = Component(3, "Consumer", 300, 0); var orphan = Component(4, "Panel", 290, 300, 0);
            orphan.Kind = NodeKind.Panel;
            var snapshot = Make(new[] { a, b, c, orphan }, Wire(a, c), Wire(b, c, 1));
            snapshot.Options = new Options { GapX = 73, GapY = 31, StackGap = 9 };
            var result = Solve(snapshot);
            Near(result.Tops[c.Id].X - result.Tops[a.Id].X - a.Width, 73, "custom GapX");
            Near(result.Tops[b.Id].Y - result.Tops[a.Id].Y - a.Height, 9, "custom StackGap");
            Assert(result.Tops[orphan.Id].Y >= result.Tops[c.Id].Y + c.Height + 31, "custom GapY");
            Guarantees(snapshot, result);
        });
        Check("Empty selection succeeds with zero moves", () => {
            var result = Solve(new Snapshot()); Assert(result.Moves.Count == 0 && result.Tops.Count == 0, "empty result");
        });
        Check("Pivot rounding compares against original rounded pivot", () => {
            var a = Component(1, "Fractional", 10.1, 20.2, 1);
            a.PivotOffset = new Vec2(2.25, 4.25);
            var snapshot = Make(new[] { a }); var result = Solve(snapshot);
            Assert(result.Moves.Count == 0, "equivalent rounded pivot emitted a move");
            Near(result.Tops[a.Id].X + a.PivotOffset.X, 12, "rounded pivot X");
            Near(result.Tops[a.Id].Y + a.PivotOffset.Y, 24, "rounded pivot Y");
            Guarantees(snapshot, result);
        });
        Check("Midpoint rounding is consistent across negative canvas coordinates", () => {
            var a = Component(1, "Negative midpoint", -10.5, -20.5, 1);
            a.PivotOffset = new Vec2(0, 0);
            var snapshot = Make(new[] { a }); var result = Solve(snapshot);
            Near(result.Tops[a.Id].X, -10, "midpoint X"); Near(result.Tops[a.Id].Y, -20, "midpoint Y");
            Assert(result.Moves.Count == 0, "unchanged rounded pivot emitted");
            Guarantees(snapshot, result);
        });
        Check("Geometric fixed-point recognition rejects a displaced component", () => {
            var a = Component(1, "A", 0, 0, 1); var b = Component(2, "B", 200, 100, 1);
            var snapshot = Make(new[] { a, b }, Wire(a, b)); var result = Solve(snapshot);
            var disturbed = Applied(snapshot, result);
            disturbed.Nodes[1].CurrentTop = new Vec2(disturbed.Nodes[1].CurrentTop.X, disturbed.Nodes[1].CurrentTop.Y + 37);
            var repaired = Solve(disturbed);
            Assert(repaired.Moves.Count > 0, "disturbed layout was accepted as formatted");
            Horizontal(repaired, disturbed.Nodes[0], disturbed.Nodes[1]); Guarantees(disturbed, repaired);
        });
    }

    private static void BalanceTests()
    {
        Check("Free sources balance bottom-up to the upper median, capped by the settled source below", () => {
            foreach (bool blocked in new[] { false, true })
            {
                var upper = Component(1, "Upper source", 0, 0, 0, 70, 20);
                var lower = Component(2, "Lower source", 0, 100, 0, 70, blocked ? 200 : 20);
                var first = Component(3, "First target", 100, 0, 1, 80, 400);
                first.Inputs = new[] { new Vec2(0, 390) };
                var second = Component(4, "Second target", 100, 500, 1, 80, blocked ? 20 : 80);
                second.Inputs = new[] { new Vec2(0, blocked ? 10 : 60) };
                var snapshot = Make(new[] { upper, lower, first, second },
                    Wire(upper, first), Wire(upper, second), Wire(lower, second));
                Assert(Graph.TryCreate(snapshot, out var graph, out var diagnostic), diagnostic);
                var columns = new[] { new[] { upper, lower }, new[] { first, second } }
                    .Select((nodes, c) => nodes.Select(n => new OrderItem { Node = n, Column = c }).ToList()).ToArray();
                // Isolate the post-compaction pass with both sources unaligned.
                var items = columns.SelectMany(c => c).ToArray();
                var tops = Placement.Compact(graph, columns, items.ToDictionary(i => i, i => i),
                    items.ToDictionary(i => i, i => 0.0), 0);
                Near(tops[lower.Id].Y, blocked ? 326 : 466, "lower source settles first");
                Near(tops[upper.Id].Y, blocked ? 290 : 380, "upper median or final lower limit");
                if (blocked)
                    Near(tops[upper.Id].Y + upper.Height + snapshot.Options.GapY,
                        tops[lower.Id].Y, "tight clearance after moving down");
                else
                    Near(tops[upper.Id].Y + upper.Outputs[0].Y,
                        tops[first.Id].Y + first.Inputs[0].Y, "upper median across two targets");
                Guarantees(snapshot, new Result { Success = true, Tops = tops, Anchor = new Vec2(0, 0),
                    Ranks = items.ToDictionary(i => i.Node.Id, i => i.Column) });
            }
        });
        Check("Free source balance respects a virtual lane directly below it", () => {
            var source = Component(1, "Source", 0, 0, 0, 70, 20);
            var first = Component(2, "First target", 100, 0, 1, 80, 400);
            first.Inputs = new[] { new Vec2(0, 390) };
            var second = Component(3, "Second target", 100, 500, 1, 80, 20);
            var snapshot = Make(new[] { source, first, second }, Wire(source, first), Wire(source, second));
            Assert(Graph.TryCreate(snapshot, out var graph, out var diagnostic), diagnostic);
            var sourceItem = new OrderItem { Node = source, Column = 0 };
            var lane = new OrderItem { Node = new Node(), Virtual = true, Column = 0 };
            var firstItem = new OrderItem { Node = first, Column = 1 };
            var secondItem = new OrderItem { Node = second, Column = 1 };
            var columns = new[] { new List<OrderItem> { sourceItem, lane }, new List<OrderItem> { firstItem, secondItem } };
            var items = columns.SelectMany(c => c).ToArray();
            var roots = items.ToDictionary(i => i, i => i);
            roots[secondItem] = lane;
            var offsets = items.ToDictionary(i => i, i => 0.0);
            offsets[secondItem] = 90;
            var tops = Placement.Compact(graph, columns, roots, offsets, 0);
            double laneTop = tops[second.Id].Y - offsets[secondItem];
            Near(tops[source.Id].Y, 290, "source moves to lane limit");
            Near(tops[source.Id].Y + source.Height + snapshot.Options.GapY, laneTop, "virtual lane clearance");
            Assert(tops.Count == snapshot.Nodes.Count, "virtual lane emitted as a real node");
            NoOverlap(snapshot, new Result { Tops = tops });
        });
    }

    private static void OrderingTests()
    {
        Check("Port-level crossings include parallel wires and exclude shared grips", () => {
            var a = Component(1, "Source", 0, 0, 2, 90, 44, 2); var b = Component(2, "Target", 200, 0);
            var snapshot = Make(new[] { a, b }, Wire(a, b, 1, 0), Wire(a, b, 0, 1));
            var result = Solve(snapshot);
            Assert(result.CrossingsBefore == 1 && result.CrossingsAfter == 1, "parallel wires merged or crossing missed");
            snapshot.Edges = new[] { Wire(a, b), Wire(a, b, 1) };
            Assert(Solve(snapshot).CrossingsBefore == 0, "shared source counted as crossing");
        });
        Check("Long wires use virtual nodes in every intermediate column", () => {
            var nodes = Enumerable.Range(1, 4).Select(i => Component(i, "Chain " + i, i * 100, i * 30)).ToArray();
            var snapshot = Make(nodes, Wire(nodes[0], nodes[1]), Wire(nodes[1], nodes[2]), Wire(nodes[2], nodes[3]), Wire(nodes[0], nodes[3], 1));
            Graph graph; string diagnostic; Assert(Graph.TryCreate(snapshot, out graph, out diagnostic), diagnostic);
            Dictionary<Guid, int> ranks; var islands = Ranking.Assign(graph, out ranks);
            var order = new Ordering(graph, islands[0], ranks);
            Assert(order.Columns[1].Count(i => i.Virtual) == 1 && order.Columns[2].Count(i => i.Virtual) == 1, "virtual nodes missing");
            Assert(order.Segments.All(s => s.Count == 2), "long wire segments missing");
            var result = Solve(snapshot); Assert(result.Tops.Count == 4, "virtual position leaked"); Guarantees(snapshot, result);
        });
        Check("Bent long edges reserve no lane after competing for a source", () => {
            var nodes = new[] { Component(1, "Source", 0, 40, 0, 70, 50),
                Component(2, "Middle 1", 100, 0, 2, 70, 100),
                Component(3, "Middle 2", 200, 0, 3, 70, 66),
                Component(4, "Near target", 300, 0, 2, 70, 44),
                Component(5, "Far target", 400, 0, 1, 70, 20) };
            var feeder = Component(6, "Feeder", 0, 0, 1, 70, 20);
            var snapshot = Make(nodes.Concat(new[] { feeder }).ToArray(), Wire(feeder, nodes[1]),
                Wire(nodes[0], nodes[1], 1), Wire(nodes[1], nodes[2]), Wire(nodes[2], nodes[3]),
                Wire(nodes[3], nodes[4]), Wire(nodes[0], nodes[3], 1), Wire(nodes[0], nodes[4]));
            var result = Solve(snapshot);
            Horizontal(result, nodes[0], nodes[4]);
            Assert(result.LaneEdges.Count == 1 && result.LaneEdges[0] == snapshot.Edges.Last(),
                "only the complete long-edge chain should reserve a lane");
            double y = result.Tops[nodes[0].Id].Y + nodes[0].Outputs[0].Y;
            double targetY = result.Tops[nodes[3].Id].Y + nodes[3].Inputs[1].Y;
            Assert(Math.Abs(y - targetY) > 1, "competing long wire did not bend");
            foreach (var node in nodes.Skip(1).Take(2))
            {
                double t = (double)result.Ranks[node.Id] / result.Ranks[nodes[3].Id];
                double pathY = y * (1 - t) + targetY * t;
                var top = result.Tops[node.Id];
                Assert(pathY > top.Y - snapshot.Options.GapY &&
                    pathY < top.Y + node.Height + snapshot.Options.GapY,
                    "bent path still has an empty GapY clearance in " + node.Label + " at " + Number(pathY) +
                    " vs " + Number(top.Y) + ".." + Number(top.Y + node.Height));
            }
            Guarantees(snapshot, result);
        });
        Check("Recognition survives an accidentally level long wire through a node", () => {
            var nodes = Enumerable.Range(1, 3).Select(i => Component(i, "Chain " + i, i * 120, i * 30, 1)).ToArray();
            var snapshot = Make(nodes, Wire(nodes[0], nodes[1]), Wire(nodes[1], nodes[2]));
            var formatted = Applied(snapshot, Solve(snapshot));
            var accidental = Wire(nodes[0], nodes[2]);
            formatted.Edges = formatted.Edges.Concat(new[] { accidental }).ToArray();
            double y = formatted.Nodes[0].CurrentTop.Y + nodes[0].Outputs[0].Y;
            Near(y, formatted.Nodes[2].CurrentTop.Y + nodes[2].Inputs[0].Y, "accidental level wire");
            Assert(y > formatted.Nodes[1].CurrentTop.Y &&
                y < formatted.Nodes[1].CurrentTop.Y + nodes[1].Height, "accidental wire must pass through Middle");
            Assert(Graph.TryCreate(formatted, out var graph, out var diagnostic), diagnostic);
            Assert(FixedPoint.TryMatch(graph, new Vec2(formatted.Nodes.Min(n => n.CurrentTop.X),
                formatted.Nodes.Min(n => n.CurrentTop.Y)), out var ranks), "visible structure was not recognized");
            var result = Solve(formatted);
            Assert(result.Moves.Count == 0 && result.LaneEdges.Count == 0, "accidental wire reserved a lane or moved the layout");
            Guarantees(formatted, result);
        });
        Check("Several level links may join one group without hiding overlapping nodes", () => {
            var source = Component(1, "Source", 0, 0, 1, 70, 80, 2);
            var upper = Component(2, "Upper", 100, 10, 1, 70, 20);
            var lower = Component(3, "Lower", 100, 50, 1, 70, 20);
            var snapshot = Make(new[] { source, upper, lower }, Wire(source, upper), Wire(source, lower, 0, 1));
            var result = Solve(snapshot);
            Assert(result.Moves.Count == 0, "multiple level links were not recognized as one group");
            Guarantees(snapshot, result);
            lower.CurrentTop = upper.CurrentTop;
            snapshot.Edges = new[] { Wire(source, upper), Wire(source, lower) };
            result = Solve(snapshot);
            Assert(result.Moves.Count > 0, "overlapping level receivers were accepted as formatted");
            Guarantees(snapshot, result);
        });
        Check("External hints determine first and last column order", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 100);
            var c = Component(3, "C", 400, 0); var d = Component(4, "D", 400, 100);
            var snapshot = Make(new[] { a, b, c, d }, Wire(a, c), Wire(a, d), Wire(b, c), Wire(b, d));
            snapshot.Inbound = new[] { new ExternalInbound { Node = a.Id, SourceGripY = 500 }, new ExternalInbound { Node = b.Id, SourceGripY = 0 } };
            snapshot.Outbound = new[] { new ExternalOutbound { Node = c.Id, TargetGripY = 500 }, new ExternalOutbound { Node = d.Id, TargetGripY = 0 } };
            var result = Solve(snapshot);
            Assert(result.Tops[b.Id].Y < result.Tops[a.Id].Y && result.Tops[d.Id].Y < result.Tops[c.Id].Y, "external hint order");
            Guarantees(snapshot, result);
        });
        Check("Unreduced crossing order interpolates absolute wire grips instead of node tops", () => {
            var a = Component(1, "Source", 0, 0, 1, 70, 20);
            var b = Component(2, "Middle", 100, 30, 1, 70, 20);
            var c = Component(3, "Target", 200, 0, 2, 70, 100);
            var snapshot = Make(new[] { a, b, c }, Wire(a, b), Wire(b, c), Wire(a, c, 1));
            Assert(Graph.TryCreate(snapshot, out var graph, out var diagnostic), diagnostic);
            var islands = Ranking.Assign(graph, out var ranks);
            var order = new Ordering(graph, islands[0], ranks, false);
            Assert(order.Columns[1][0].Node.Id == b.Id && order.Columns[1][1].Virtual, "lane sorted above Middle");
            Near(order.Columns[1][1].Node.CurrentTop.Y, 42.5, "interpolated absolute wire Y");
        });
    }

    private static void Refused(Snapshot snapshot, string contains)
    {
        var result = LayoutSolver.Solve(snapshot);
        Assert(!result.Success && result.Moves.Count == 0 && result.Tops.Count == 0, "invalid snapshot accepted or moved");
        Assert(result.Diagnostic.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0, "diagnostic: " + result.Diagnostic);
    }

    private static void ValidationTests()
    {
        Check("Compaction constraint cycle returns a clean failure with zero moves", () => {
            var a = Component(1, "A", 0, 100); var b = Component(2, "B", 0, 0);
            var snapshot = Make(new[] { a, b }, Wire(a, b));
            var result = LayoutSolver.Solve(snapshot, (graph, ordering, ranks, x, lanes) => {
                var upper = new OrderItem { Node = a }; var lower = new OrderItem { Node = b };
                var upperLane = new OrderItem { Node = a, Virtual = true };
                var lowerLane = new OrderItem { Node = b, Virtual = true };
                var columns = new[] { new List<OrderItem> { upper, lower }, new List<OrderItem> { lowerLane, upperLane } };
                // Opposite column orders impose both A < B and B < A.
                return Placement.Compact(graph, columns,
                    new Dictionary<OrderItem, OrderItem> { [upper] = upper, [lower] = lower,
                        [upperLane] = upper, [lowerLane] = lower },
                    new Dictionary<OrderItem, double> { [upper] = 0, [lower] = 0, [upperLane] = 0, [lowerLane] = 0 }, x);
            });
            Assert(!result.Success && result.Moves.Count == 0 && result.Tops.Count == 0
                && result.Diagnostic.Contains("compaction") && result.Diagnostic.Contains("cycle"), result.Diagnostic);
        });
        Check("Invalid rounded graph rebuild returns a clean failure with zero moves", () => {
            var a = Component(1, "A", 0, 100); var b = Component(2, "B", 0, 0);
            var result = LayoutSolver.Solve(Make(new[] { a, b }, Wire(a, b)),
                (graph, ordering, ranks, x, lanes) => graph.Nodes.ToDictionary(n => n.Id,
                    n => new Vec2(double.PositiveInfinity, 0)));
            Assert(!result.Success && result.Moves.Count == 0 && result.Tops.Count == 0
                && result.Diagnostic.Contains("Rounded layout is invalid") && result.Diagnostic.Contains("coordinates"), result.Diagnostic);
        });
        Check("Topological queue chooses the lowest sorted ready index including newly ready nodes", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 0, 100);
            var c = Component(3, "C", 0, 200); var d = Component(4, "D", 0, 300);
            Assert(Graph.TryCreate(Make(new[] { d, c, b, a }, Wire(c, a), Wire(c, a), Wire(a, d)),
                out var graph, out var diagnostic), diagnostic);
            Assert(graph.Topological.Select(n => n.Id).SequenceEqual(new[] { b.Id, c.Id, a.Id, d.Id }), "ready order changed");
        });
        Check("Cycle refusal names the labels on an offending edge", () => {
            var a = Component(1, "Circle", 0, 0); var b = Component(2, "Move", 100, 0);
            var snapshot = Make(new[] { a, b }, Wire(a, b), Wire(b, a));
            Refused(snapshot, "Cycle"); var result = LayoutSolver.Solve(snapshot);
            Assert(result.Diagnostic.Contains("'Circle'") && result.Diagnostic.Contains("'Move'"), "labels absent");
            snapshot = Make(new[] { Component(3, "Unrelated", 0, -100), a, b }, Wire(a, b), Wire(b, a));
            Refused(snapshot, "Cycle");
            snapshot = Make(new[] { a }, Wire(a, a)); Refused(snapshot, "Circle");
        });
        Check("Invalid internal port indices are refused", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 100, 0);
            Refused(Make(new[] { a, b }, Wire(a, b, 2)), "port");
            Refused(Make(new[] { a, b }, Wire(a, b, 0, -1)), "port");
        });
        Check("Duplicate ids and missing internal/external nodes are refused", () => {
            var a = Component(1, "A", 0, 0); var b = Component(2, "B", 100, 0);
            Refused(Make(new[] { a, a }), "Duplicate");
            Refused(Make(new[] { a }, Wire(a, b)), "missing");
            var snapshot = Make(new[] { a }); snapshot.Inbound = new[] { new ExternalInbound { Node = b.Id } }; Refused(snapshot, "missing");
            snapshot.Inbound = new ExternalInbound[0]; snapshot.Outbound = new[] { new ExternalOutbound { Node = b.Id } }; Refused(snapshot, "missing");
        });
        Check("Bad external ports, sizes, coordinates and gaps are refused", () => {
            var a = Component(1, "A", 0, 0); var snapshot = Make(new[] { a });
            snapshot.Inbound = new[] { new ExternalInbound { Node = a.Id, Port = -1 } }; Refused(snapshot, "port");
            snapshot.Inbound = new ExternalInbound[0]; snapshot.Outbound = new[] { new ExternalOutbound { Node = a.Id, Port = 1 } }; Refused(snapshot, "port");
            snapshot.Outbound = new ExternalOutbound[0]; a.Width = 0; Refused(snapshot, "size");
            a.Width = 90; a.Height = -1; Refused(snapshot, "size"); a.Height = 44;
            a.CurrentTop = new Vec2(double.NaN, 0); Refused(snapshot, "coordinates");
            a.CurrentTop = new Vec2(0, 0); snapshot.Options.GapY = -1; Refused(snapshot, "Gaps");
        });
    }

    private sealed class Sample
    {
        internal int Seed;
        internal Snapshot Snapshot;
        internal Result Result;
        internal string Before;
    }

    private static Snapshot RandomDag(int seed)
    {
        var random = new Random(seed);
        int count = random.Next(2, 31);
        var nodes = new Node[count];
        var partition = new int[count];
        for (int i = 0; i < count; i++)
        {
            int inputs = random.Next(1, 5), outputs = random.Next(1, 5);
            nodes[i] = Component(i + 1, "N" + i, random.Next(-300, 1000) + random.Next(4) * 0.25,
                random.Next(-300, 1000) + random.Next(4) * 0.25, inputs,
                random.Next(40, 201) + random.Next(4) * 0.25, random.Next(20, 161) + random.Next(4) * 0.25, outputs);
            nodes[i].PivotOffset = new Vec2(random.Next(20) + random.Next(4) * 0.25, random.Next(20) + random.Next(4) * 0.25);
            partition[i] = random.NextDouble() < 0.15 ? -1 : seed % 3 == 0 ? i % 2 : 0;
        }
        var edges = new List<Edge>();
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                if (partition[i] >= 0 && partition[i] == partition[j] && random.NextDouble() < 0.18)
                {
                    edges.Add(Wire(nodes[i], nodes[j], random.Next(nodes[j].Inputs.Length), random.Next(nodes[i].Outputs.Length)));
                    if (random.NextDouble() < 0.12)
                        edges.Add(Wire(nodes[i], nodes[j], random.Next(nodes[j].Inputs.Length), random.Next(nodes[i].Outputs.Length)));
                }
        foreach (var node in nodes)
            node.Widget = !edges.Any(e => e.To == node.Id) && random.NextDouble() < 0.6;
        var inbound = new List<ExternalInbound>(); var outbound = new List<ExternalOutbound>();
        foreach (var node in nodes)
        {
            if (random.NextDouble() < 0.22) inbound.Add(new ExternalInbound { Node = node.Id, Port = random.Next(node.Inputs.Length), SourceGripY = random.Next(-500, 1500) });
            if (random.NextDouble() < 0.25) outbound.Add(new ExternalOutbound { Node = node.Id, Port = random.Next(node.Outputs.Length), TargetGripY = random.Next(-500, 1500) });
        }
        foreach (var node in nodes)
        {
            node.Kind = (NodeKind)random.Next(3);
            node.Widget = node.Widget && node.Kind != NodeKind.Component && !inbound.Any(e => e.Node == node.Id);
        }
        return new Snapshot { Nodes = nodes, Edges = edges, Inbound = inbound, Outbound = outbound,
            Options = new Options { ReturnConstraint = seed % 4 != 0 } };
    }

    private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }

    private static string Fingerprint(Snapshot snapshot)
    {
        return string.Join(";", snapshot.Nodes.Select(n => n.Id + ":" + n.Kind + ":" + Number(n.CurrentTop.X) + "," + Number(n.CurrentTop.Y))) + "|" +
            string.Join(";", snapshot.Edges.Select(e => e.From + ":" + e.FromPort + ">" + e.To + ":" + e.ToPort));
    }

    private static string Moves(Result result)
    {
        return string.Join(";", result.Moves.Select(m => m.Id + ":" + Number(m.Pivot.X) + "," + Number(m.Pivot.Y)));
    }

    private static void SameResult(Result a, Result b)
    {
        Assert(a.Success == b.Success && a.Diagnostic == b.Diagnostic && a.Unstable == b.Unstable, "different status");
        Assert(Moves(a) == Moves(b), "different moves or move order");
        Assert(a.LaneEdges.Select(e => e.From + ":" + e.FromPort + ">" + e.To + ":" + e.ToPort)
            .SequenceEqual(b.LaneEdges.Select(e => e.From + ":" + e.FromPort + ">" + e.To + ":" + e.ToPort)),
            "different reserved lanes or lane order");
        Assert(a.Rounds == b.Rounds && a.CrossingsBefore == b.CrossingsBefore && a.CrossingsAfter == b.CrossingsAfter && a.Log.SequenceEqual(b.Log), "different metrics");
        foreach (var id in a.Tops.Keys)
        {
            Near(a.Tops[id].X, b.Tops[id].X, "deterministic X"); Near(a.Tops[id].Y, b.Tops[id].Y, "deterministic Y");
            Assert(a.Ranks[id] == b.Ranks[id], "different ranks");
        }
    }

    private static void Each(List<Sample> samples, Action<Sample> action)
    {
        Assert(samples.Count >= 200, "random fixtures did not finish");
        foreach (var sample in samples)
        {
            try { action(sample); }
            catch (Exception e) { throw new Exception("seed " + sample.Seed + ": " + e.Message); }
        }
    }

    private static void RandomTests()
    {
        var samples = new List<Sample>();
        Check("Random DAG fixtures: 1000 fixed seeds, 2-30 nodes, fractional geometry", () => {
            for (int i = 0; i < 1000; i++)
            {
                int seed = 17011 + i * 7919;
                var snapshot = RandomDag(seed); string before = Fingerprint(snapshot);
                var result = LayoutSolver.Solve(snapshot);
                Assert(result.Success, "seed " + seed + ": " + result.Diagnostic);
                samples.Add(new Sample { Seed = seed, Snapshot = snapshot, Result = result, Before = before });
            }
        });
        Check("Random DAG property: idempotent", () => Each(samples, s => Idempotent(s.Snapshot, s.Result)));
        Check("Random DAG property: emitted layouts are recognized geometrically", () => Each(samples, s => {
            var applied = Applied(s.Snapshot, s.Result);
            Assert(Graph.TryCreate(applied, out var graph, out var diagnostic), diagnostic);
            Assert(FixedPoint.TryMatch(graph, new Vec2(applied.Nodes.Min(n => n.CurrentTop.X),
                applied.Nodes.Min(n => n.CurrentTop.Y)), out var ranks), "emitted layout was not recognized");
        }));
        Check("Random DAG property: deterministic under repetition and shuffled input", () => Each(samples, s => {
            SameResult(s.Result, LayoutSolver.Solve(s.Snapshot));
            var random = new Random(s.Seed);
            var shuffled = new Snapshot { Nodes = Shuffle(s.Snapshot.Nodes, random), Edges = Shuffle(s.Snapshot.Edges, random),
                Inbound = Shuffle(s.Snapshot.Inbound, random), Outbound = Shuffle(s.Snapshot.Outbound, random), Options = s.Snapshot.Options };
            SameResult(s.Result, LayoutSolver.Solve(shuffled));
        }));
        Check("Random DAG property: rectangles never overlap", () => Each(samples, s => NoOverlap(s.Snapshot, s.Result)));
        Check("Random DAG property: horizontal long wires clear intermediate nodes by GapY - 1", () => {
            int longEdges = 0, level = 0, lanes = 0;
            string firstFailure = null;
            Each(samples, s => {
                var nodes = s.Snapshot.Nodes.ToDictionary(n => n.Id);
                foreach (var edge in s.Snapshot.Edges)
                {
                    int from = s.Result.Ranks[edge.From], to = s.Result.Ranks[edge.To];
                    if (to - from <= 1) continue;
                    longEdges++;
                    double y = s.Result.Tops[edge.From].Y + nodes[edge.From].Outputs[edge.FromPort].Y;
                    double targetY = s.Result.Tops[edge.To].Y + nodes[edge.To].Inputs[edge.ToPort].Y;
                    bool reserved = s.Result.LaneEdges.Contains(edge);
                    if (Math.Abs(y - targetY) <= 1) level++;
                    // A bent edge may accidentally be level through a node. Only
                    // complete chains reserve lanes and promise node clearance.
                    if (!reserved) continue;
                    lanes++;
                    Assert(Math.Abs(y - targetY) <= 1, "reserved lane is not level");
                    foreach (var node in s.Snapshot.Nodes.Where(n => s.Result.Ranks[n.Id] > from && s.Result.Ranks[n.Id] < to))
                    {
                        var top = s.Result.Tops[node.Id];
                        // Other islands can reuse ranks but do not lie along this wire.
                        if (top.X <= s.Result.Tops[edge.From].X || top.X >= s.Result.Tops[edge.To].X) continue;
                        // Independently rounded pivots can shrink a separation by
                        // up to one unit. Integer geometry retains exact GapY.
                        if (y <= top.Y - s.Snapshot.Options.GapY + 1 + 1e-8 ||
                            y >= top.Y + node.Height + s.Snapshot.Options.GapY - 1 - 1e-8) continue;
                        if (firstFailure == null)
                            firstFailure = "seed " + s.Seed + ": " + nodes[edge.From].Label + " -> " + nodes[edge.To].Label +
                                " lane y=" + Number(y) + " needs GapY=" + Number(s.Snapshot.Options.GapY) +
                                " from " + node.Label + " bounds y=" + Number(top.Y) + ".." + Number(top.Y + node.Height);
                    }
                }
            });
            Console.WriteLine("PASS Level long-edge share (within 1 unit): " + level + "/" + longEdges + " (" +
                (100.0 * level / longEdges).ToString("F2", CultureInfo.InvariantCulture) + "%) across 1000 seeds");
            Console.WriteLine("PASS Reserved long-edge lane share: " + lanes + "/" + longEdges + " (" +
                (100.0 * lanes / longEdges).ToString("F2", CultureInfo.InvariantCulture) + "%) across 1000 seeds");
            Assert(firstFailure == null, firstFailure);
        });
        Check("Random DAG property: all internal wires flow left to right", () => Each(samples, s => Dataflow(s.Snapshot, s.Result)));
        Check("Random DAG property: union remains within one unit of anchor", () => Each(samples, s => {
            Anchored(s.Snapshot, s.Result);
            Near(s.Result.Tops.Values.Min(t => t.X), s.Result.Anchor.X, "corrected anchor X", 0.5);
            Near(s.Result.Tops.Values.Min(t => t.Y), s.Result.Anchor.Y, "corrected anchor Y", 0.5);
        }));
        Check("Random DAG property: moves contain only members in Guid order", () => Each(samples, s => {
            var ids = new HashSet<Guid>(s.Snapshot.Nodes.Select(n => n.Id));
            Assert(s.Result.Moves.All(m => ids.Contains(m.Id)), "non-member move");
            Assert(s.Result.Moves.Select(m => m.Id).SequenceEqual(s.Result.Moves.Select(m => m.Id).OrderBy(id => id)), "move order");
            foreach (var move in s.Result.Moves)
                Assert(move.Pivot.X == Math.Round(move.Pivot.X) && move.Pivot.Y == Math.Round(move.Pivot.Y), "fractional pivot");
        }));
        Check("Random DAG property: every layout settles within three rounds", () => Each(samples, s => {
            Assert(!s.Result.Unstable, string.Join(" ", s.Result.Log));
            Assert(s.Result.Rounds >= 1 && s.Result.Rounds <= 3, "round count");
        }));
        Check("Random DAG property: Solve does not mutate its input", () => Each(samples, s => Assert(s.Before == Fingerprint(s.Snapshot), "input changed")));
    }

    private static T[] Shuffle<T>(IReadOnlyList<T> input, Random random)
    {
        var result = input.ToArray();
        for (int i = result.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1); var item = result[i]; result[i] = result[j]; result[j] = item;
        }
        return result;
    }

    private static void Main()
    {
        GoldenTests(); BalanceTests(); OrderingTests(); ValidationTests(); RandomTests();
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
