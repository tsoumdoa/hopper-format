using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using HopperFormat;
using HopperFormat.Layout;

internal static class SessionTests
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint mode);
    private static int _failures;

    internal static int Run(Form form)
    {
        Check(form, "Session FF previews selection then runs once and refreshes outputs", f =>
        {
            Tap(f.Canvas);
            Assert(f.Session.Status == "Format selection (2 objects) · tap F again"
                && f.Document.UndoUtil.Records.Count == 0, "first tap moved objects or lost prompt");
            Tap(f.Canvas);
            Assert(f.Session.LastMoves > 0 && f.Session.Status.StartsWith("Formatted selection · ")
                && f.Document.UndoUtil.Records.Count == 1 && f.Owner.OutputRefreshes >= 2, "FF failed to apply or refresh outputs");
            var record = f.Document.UndoUtil.Records.Single();
            f.Session.Run();
            Assert(f.Session.Status == "Already formatted" && f.Session.LastMoves == 0
                && f.Document.UndoUtil.Records.Count == 1 && record.Before.Count <= 2, "menu path disagrees with FF/no-op");
        });
        Check(form, "Session menu formats a selected label group and leaves other nodes untouched", f =>
        {
            var group = new GH_Group { NickName = "Group only" };
            group.ObjectIDs.Add(f.First.InstanceGuid); f.Document.Objects.Add(group);
            f.Document.Selection.Clear(); f.Document.Selection.Add(group);
            f.Session.Run();
            Assert(f.Session.Status == "Already formatted" && f.Session.LastMoves == 0
                && !f.Session.Log(false).Any(line => line.Contains("Skipped"))
                && f.Document.UndoUtil.Creates == 0 && f.First.Attributes != null && f.Second.Attributes.Pivot == f.Second.InitialPivot,
                "label-only group was skipped or moved a loose node");
        });
        Check(form, "Session FF captures a selected group and writes one Format groups undo record", f =>
        {
            var group = new GH_Group { NickName = "Pipeline" };
            group.ObjectIDs.AddRange(new[] { f.First.InstanceGuid, f.Second.InstanceGuid });
            f.Document.Objects.Add(group); f.Document.Selection.Clear(); f.Document.Selection.Add(group);
            Tap(f.Canvas); Assert(f.Session.Status == "Format selection (1 objects) · tap F again", "group-only FF was not captured");
            Tap(f.Canvas);
            Assert(f.Session.LastMoves > 0 && f.Session.Status.StartsWith("Formatted groups")
                && f.Document.UndoUtil.Records.Single().Name == "Format groups", "group FF did not apply: " + f.Session.Status + " " + string.Join(";", f.Session.Log(true)));
            f.Session.Run(); Assert(f.Session.Status == "Already formatted" && f.Document.UndoUtil.Records.Count == 1, "group repeat moved");
        });
        Check(form, "Session refuses locked ancestors before solving or applying", f =>
        {
            var child = new GH_Group(); child.ObjectIDs.Add(f.First.InstanceGuid);
            var locked = new GH_Group { NickName = "Protected [LOCK]" }; locked.ObjectIDs.Add(child.InstanceGuid);
            f.Document.Objects.AddRange(new[] { child, locked });
            f.Session.Run();
            Assert(f.Session.Status.Contains("Protected [LOCK]") && f.Session.LastMoves == 0
                && f.Document.UndoUtil.Creates == 0 && f.First.Attributes.Pivot == f.First.InitialPivot
                && f.Second.Attributes.Pivot == f.Second.InitialPivot, "locked selection moved or did not name the group");
        });
        Check(form, "Session empty-selection F preserves last run outputs and schedules no refresh", f =>
        {
            var scribble = new TestObject { NickName = "Skipped notes" };
            f.Document.Selection.Add(scribble);
            f.Session.Run();
            Assert(f.Session.LastMoves > 0, "setup did not format with moves");
            var status = f.Session.Status;
            var moves = f.Session.LastMoves;
            var log = f.Session.Log(false).ToArray();
            var debugLog = f.Session.Log(true).ToArray();
            var refreshes = f.Owner.OutputRefreshes;
            var count = f.Document.UndoUtil.Records.Count;
            f.Document.Selection.Clear();
            Tap(f.Canvas);
            Assert(f.Session.Status == status && f.Session.LastMoves == moves
                && f.Session.Log(false).SequenceEqual(log) && f.Session.Log(true).SequenceEqual(debugLog)
                && f.Owner.OutputRefreshes == refreshes && f.ForwardedDown == 1
                && f.Document.UndoUtil.Records.Count == count, "empty-selection keydown/keyup changed outputs, scheduled a refresh or captured F");
        });
        Check(form, "Session empty-selection FF previews eligible groups, then formats all once with skipped count", f =>
        {
            var group = new GH_Group { NickName = "Pipeline", Document = f.Document };
            group.ObjectIDs.AddRange(new[] { f.First.InstanceGuid,f.Second.InstanceGuid }); f.Document.Objects.Add(group);
            var lockedNode = new TestComponent { InitialPivot = new PointF(900,900) };
            var locked = new GH_Group { NickName = "Fixed [lock]", Document = f.Document }; locked.ObjectIDs.Add(lockedNode.InstanceGuid);
            f.Document.Objects.AddRange(new IGH_DocumentObject[] { lockedNode,locked,f.Owner });
            f.Document.Selection.Clear();
            var refreshes = f.Owner.OutputRefreshes;
            Tap(f.Canvas);
            Assert(f.Session.Status == "Format all groups (1) · tap F again" && f.Document.UndoUtil.Creates == 0
                && f.First.Attributes == null && f.Owner.OutputRefreshes == refreshes + 1, "all preview counted/measured/applied incorrectly");
            Tap(f.Canvas);
            Assert(f.Session.Status.StartsWith("Formatted all groups · ") && f.Session.Status.EndsWith(" (1 skipped)")
                && f.Document.UndoUtil.Records.Single().Name == "Format all groups" && f.Session.LastMoves > 0
                && f.Session.Log(false).Any(l => l.Contains("Skipped group 'Fixed [lock]'")) && f.ForwardedDown == 0,
                "all FF failed: " + f.Session.Status);
            Assert(lockedNode.Attributes.Pivot == lockedNode.InitialPivot && f.Owner.Attributes.Pivot == f.Owner.InitialPivot, "fixed objects moved");
        });
        Check(form, "Session Format all groups menu runs with or without selection and no-op records nothing", f =>
        {
            var group = new GH_Group { NickName = "Pipeline", Document = f.Document };
            group.ObjectIDs.AddRange(new[] { f.First.InstanceGuid,f.Second.InstanceGuid }); f.Document.Objects.Add(group);
            f.Session.RunAll();
            Assert(f.Document.UndoUtil.Records.Single().Name == "Format all groups" && f.Session.LastMoves > 0, "selected menu failed");
            f.Document.Selection.Clear(); f.Session.RunAll();
            Assert(f.Session.Status == "Formatted all groups · 0 moves" && f.Session.LastMoves == 0
                && f.Document.UndoUtil.Records.Count == 1, "empty menu not fixed");
        });
        Check(form, "Session empty selection with only locked groups gives hint only and passes F through", f =>
        {
            var group = new GH_Group { NickName = "[lock]", Document = f.Document }; group.ObjectIDs.Add(f.First.InstanceGuid);
            f.Document.Objects.Add(group); f.Document.Selection.Clear();
            var status = f.Session.Status; var log = f.Session.Log(true).ToArray(); var refreshes = f.Owner.OutputRefreshes;
            Tap(f.Canvas);
            var text = typeof(FeedbackPill).GetField("_text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert((string)text.GetValue(f.Pill) == "No groups to format" && f.Session.Status == status && f.Session.LastMoves == 0
                && f.Session.Log(true).SequenceEqual(log) && f.Owner.OutputRefreshes == refreshes && f.ForwardedDown == 1
                && f.First.Attributes == null && f.Document.UndoUtil.Creates == 0, "ineligible all check had side effects");
        });
        Check(form, "Session pill factory failure keeps hook handlers and menu formatting working", f =>
        {
            Assert(f.PillAttempts == 1 && f.Session.Log(false).Count(line => line.Contains("injected pill failure")) == 1
                && f.Session.Status != "Error - see Log", "pill failure was not logged once or poisoned status");
            f.Session.Configure(f.Owner, f.Document, new Options());
            Assert(f.PillAttempts == 1 && f.Session.Log(false).Count() == 1, "same canvas retried or duplicated pill failure");
            Assert(NativeWindow.FromHandle(f.Canvas.Handle) is CanvasShortcutHook, "pill failure prevented hook attachment");
            Tap(f.Canvas); f.Canvas.Document = new GH_Document(); f.Canvas.Document = f.Document; Tap(f.Canvas);
            Assert(f.Document.UndoUtil.Creates == 0 && f.Session.Status.Contains("tap F again"), "document handler was not wired");
            f.Session.Run();
            Assert(f.Session.LastMoves > 0 && f.Session.Status.StartsWith("Formatted selection · ")
                && f.Document.UndoUtil.Records.Count == 1 && f.Owner.OutputRefreshes > 0, "menu failed without a pill");
            Tap(f.Canvas); Tap(f.Canvas);
            Assert(f.Session.Status == "Already formatted" && f.ForwardedDown == 0, "shortcut stopped working without a pill");
        }, failPill: true);
        Check(form, "Session solver refusal leaves all pivots and undo untouched", f =>
        {
            f.Session.Configure(f.Owner, f.Document, new Options { GapX = double.NaN });
            f.Session.Run();
            Assert(f.Session.Status.Contains("Gaps must be finite") && f.Document.UndoUtil.Creates == 0
                && f.First.Attributes.Pivot == f.First.InitialPivot && f.Second.Attributes.Pivot == f.Second.InitialPivot,
                "invalid options moved objects");
        });
        Check(form, "Session paint failure logs once and disables pill without further invalidations", f =>
        {
            Tap(f.Canvas);
            using var bitmap = new Bitmap(10, 10);
            var graphics = Graphics.FromImage(bitmap);
            graphics.Dispose();
            var invalidations = f.Canvas.Invalidations;
            f.Canvas.PaintOverlay(graphics);
            var refreshes = f.Owner.OutputRefreshes;
            for (int i = 0; i < 5; i++) f.Canvas.PaintOverlay(graphics);
            Assert(f.Session.Log(false).Count() == 1 && f.Session.Log(false).Single().StartsWith("Feedback paint:")
                && f.Canvas.Invalidations == invalidations && f.Owner.OutputRefreshes == refreshes,
                "paint failure repeated reporting, showing or invalidation");
            f.Document.FailSelection = true; Tap(f.Canvas); f.Document.FailSelection = false;
            Assert(f.Canvas.Invalidations == invalidations, "error reporting showed a disabled pill");
            f.Session.Run();
            Assert(f.Session.LastMoves > 0 && f.Canvas.Invalidations == invalidations, "menu failed or showed disabled pill");
            Tap(f.Canvas); Tap(f.Canvas);
            Assert(f.Session.Status == "Already formatted" && f.Canvas.Invalidations == invalidations,
                "FF failed or showed disabled pill");
            using var replacement = new GH_Canvas { Document = f.Document, Size = new Size(300, 200) };
            form.Controls.Add(replacement); Instances.ChangeCanvas(replacement);
            Assert(f.PillAttempts == 1, "replacement canvas recreated disabled pill");
        });
        Check(form, "Session pill timer failure is never reported through Show", f =>
        {
            Tap(f.Canvas);
            var invalidations = f.Canvas.Invalidations;
            f.PillGuard("Feedback timer", () => throw new InvalidOperationException("injected timer failure"));
            f.PillGuard("Feedback timer", () => throw new InvalidOperationException("repeated timer failure"));
            Assert(f.Session.Log(false).Count() == 1 && f.Canvas.Invalidations == invalidations,
                "pill-origin error showed the pill or logged again");
        });
        Check(form, "Session unsettled result applies moves and always exposes warning in status pill and Log", f =>
        {
            f.Unstable = true;
            f.Session.Run();
            const string warning = "Unstable: layout did not settle within three rounds.";
            Assert(f.Session.LastMoves > 0 && f.Document.UndoUtil.Records.Count == 1
                && f.Session.Status.EndsWith(" (not fully settled, FF again may adjust)")
                && f.Session.Log(false).Contains(warning) && f.Session.Log(true).Count(line => line == warning) == 1,
                "unsettled layout was not applied or warning was hidden/duplicated");
            var text = typeof(FeedbackPill).GetField("_text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert((string)text.GetValue(f.Pill) == f.Session.Status, "pill omitted unsettled warning");
        });
        Check(form, "Session Debug adds solver lines while skipped objects remain visible without it", f =>
        {
            var scribble = new TestObject { NickName = "Notes" }; f.Document.Objects.Add(scribble); f.Document.Selection.Add(scribble);
            f.Session.Run();
            Assert(f.Session.Log(false).Any(line => line.Contains("Skipped TestObject 'Notes'"))
                && f.Session.Log(true).Count() > f.Session.Log(false).Count(), "debug/skip logging lost information");
        });
        Check(form, "Session navigation and active interactions preserve host F", f =>
        {
            GH_Canvas.NavigationPanLeft = Keys.F;
            try { Tap(f.Canvas); }
            finally { GH_Canvas.NavigationPanLeft = Keys.None; }
            f.Canvas.IsActiveInteraction = true; Tap(f.Canvas); f.Canvas.IsActiveInteraction = false;
            f.Canvas.IsActiveWidget = true; Tap(f.Canvas); f.Canvas.IsActiveWidget = false;
            f.Canvas.IsActiveObject = true; Tap(f.Canvas); f.Canvas.IsActiveObject = false;
            f.Canvas.ModifiersEnabled = false; Tap(f.Canvas); f.Canvas.ModifiersEnabled = true;
            Assert(f.ForwardedDown == 5 && f.Document.UndoUtil.Creates == 0, "reserved/interaction key was captured");
        });
        Check(form, "Session changing documents resets a pending first tap", f =>
        {
            Tap(f.Canvas); f.Canvas.Document = new GH_Document(); f.Canvas.Document = f.Document; Tap(f.Canvas);
            Assert(f.Document.UndoUtil.Creates == 0 && f.Session.Status.Contains("tap F again"), "first tap survived document changes");
            Tap(f.Canvas); Assert(f.Document.UndoUtil.Records.Count == 1, "fresh FF did not recover");
        });
        Check(form, "Session disabled menu run reports status and refreshes without moving", f =>
        {
            var refreshes = f.Owner.OutputRefreshes;
            f.Owner.SessionEligible = false; f.Session.Run();
            Assert(f.Session.Status == "Formatter is disabled" && f.Owner.OutputRefreshes == refreshes + 1
                && f.Session.LastMoves == 0 && f.Document.UndoUtil.Creates == 0
                && f.First.Attributes == null && f.Second.Attributes == null, "disabled menu run was silent or moved objects");
            f.Owner.SessionEligible = true; f.Canvas.Document = new GH_Document(); f.Session.Run();
            Assert(f.Session.Status == "Open this document's canvas to format" && f.Owner.OutputRefreshes == refreshes + 2
                && f.Document.UndoUtil.Creates == 0, "non-current document menu run was silent or moved objects");
            using var detached = new FormatSession();
            detached.Run();
            Assert(detached.Status == "Formatter is not attached to a document", "detached menu run was silent");
        });
        Check(form, "Session solution-in-progress menu run cannot apply", f =>
        {
            f.Document.SolutionState = GH_ProcessStep.Process; f.Session.Run();
            Assert(f.Document.UndoUtil.Creates == 0 && f.Session.Status == "Wait for the current solution to finish", "unsafe run was not deferred/refused");
        });
        Check(form, "Session guard contains native eligibility errors and can recover", f =>
        {
            f.Document.FailSelection = true; Tap(f.Canvas);
            Assert(f.Session.Status == "Error - see Log" && f.Session.Log(false).Any(line => line.Contains("injected selection failure")),
                "native error escaped or was not reported");
            f.Document.FailSelection = false; Tap(f.Canvas); Tap(f.Canvas);
            Assert(f.Document.UndoUtil.Records.Count == 1, "session failed to recover after guarded error");
        });
        Check(form, "Session tracks a replacement canvas and releases its hook on disposal", f =>
        {
            using var replacement = new GH_Canvas { Document = f.Document, Size = new Size(300, 200) };
            form.Controls.Add(replacement); Instances.ChangeCanvas(replacement); replacement.Focus();
            var originalPivot = f.First.InitialPivot;
            Tap(replacement); Tap(replacement);
            Assert(f.Document.UndoUtil.Records.Count == 1 && f.First.Attributes.Pivot != originalPivot, "new canvas never attached");
            f.Session.Dispose();
            Assert(NativeWindow.FromHandle(replacement.Handle).GetType().Assembly == typeof(Control).Assembly,
                "session disposal retained a native subclass");
        });
        return _failures;
    }

    private static void Tap(Control canvas)
    {
        long data = 1L | ((long)MapVirtualKey((uint)Keys.F, 0) << 16);
        Assert(PostMessage(canvas.Handle, 0x100, new IntPtr((int)Keys.F), new IntPtr(data)), "post keydown failed");
        Assert(PostMessage(canvas.Handle, 0x101, new IntPtr((int)Keys.F), new IntPtr(data | (1L << 30) | (1L << 31))), "post keyup failed");
        Application.DoEvents();
    }
    private static void Check(Form form, string name, Action<Fixture> test, bool failPill = false)
    {
        try { using var fixture = new Fixture(form, failPill); test(fixture); Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly GH_Document Document = new GH_Document();
        internal readonly GH_Canvas Canvas;
        internal readonly HopperFormatComponent Owner = new HopperFormatComponent();
        internal readonly FormatSession Session;
        internal readonly TestComponent First = new TestComponent(0, 1) { InitialPivot = new PointF(80, 80) };
        internal readonly TestComponent Second = new TestComponent(1, 0) { InitialPivot = new PointF(30, 250) };
        internal int ForwardedDown;
        internal int PillAttempts;
        internal FeedbackPill Pill;
        internal Action<string, Action> PillGuard;
        internal bool Unstable;
        private readonly Form _form;
        private readonly KeyEventHandler _down;

        internal Fixture(Form form, bool failPill)
        {
            _form = form;
            Session = new FormatSession((canvas, guard) =>
            {
                PillAttempts++;
                if (failPill) throw new InvalidOperationException("injected pill failure");
                PillGuard = guard;
                return Pill = new FeedbackPill(canvas, guard);
            }, snapshot =>
            {
                var result = LayoutSolver.Solve(snapshot);
                if (Unstable)
                {
                    result.Unstable = true;
                    result.Log = result.Log.Concat(new[] { "Unstable: layout did not settle within three rounds." }).ToArray();
                }
                return result;
            });
            TestParam.Connect(First.Params.Output[0], Second.Params.Input[0]);
            Document.Objects.AddRange(new[] { First, Second }); Document.Selection.AddRange(new[] { First, Second });
            Canvas = new GH_Canvas { Document = Document, Location = new Point(5, 30), Size = new Size(300, 200) };
            form.Controls.Add(Canvas); Instances.ChangeCanvas(Canvas); form.Activate(); Assert(Canvas.Focus(), "canvas focus failed");
            _down = (_, e) => { if (Canvas.Focused && e.KeyCode == Keys.F) { ForwardedDown++; e.Handled = true; } };
            form.KeyDown += _down;
            Session.Configure(Owner, Document, new Options());
        }
        public void Dispose()
        {
            Session.Dispose(); Instances.ChangeCanvas(null); Canvas.Dispose(); _form.KeyDown -= _down;
        }
    }
}
