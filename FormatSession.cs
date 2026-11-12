using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using HopperFormat.Layout;

namespace HopperFormat
{
    internal sealed class FormatSession : IDisposable
    {
        private const int MinimumTapWindowMilliseconds = 650;
        private HopperFormatComponent _owner;
        private GH_Document _document;
        private GH_Canvas _canvas;
        private IDisposable _shortcut;
        private FeedbackPill _pill;
        private bool _pillDisabled;
        private readonly Func<GH_Canvas, Action<string, Action>, FeedbackPill> _pillFactory;
        private readonly Func<Snapshot, Result> _solve;
        private readonly Func<HierarchySnapshot, Result> _solveHierarchy;
        private bool _trackingCanvas, _running, _keyDown;
        private long? _firstTap;
        private Options _options = new Options();
        private readonly List<string> _log = new List<string>();
        private readonly List<string> _detail = new List<string>();

        internal string Status { get; private set; } = "Select components to format";
        internal int LastMoves { get; private set; }
        internal IEnumerable<string> Log(bool debug) => debug ? _log.Concat(_detail) : _log;

        internal FormatSession(Func<GH_Canvas, Action<string, Action>, FeedbackPill> pillFactory = null,
            Func<Snapshot, Result> solve = null, Func<HierarchySnapshot, Result> solveHierarchy = null)
        {
            _pillFactory = pillFactory ?? ((canvas, guard) => new FeedbackPill(canvas, guard));
            _solve = solve ?? LayoutSolver.Solve;
            _solveHierarchy = solveHierarchy ?? HierarchySolver.Solve;
        }

        internal void Configure(HopperFormatComponent owner, GH_Document document, Options options) => Guard("Configure", () =>
        {
            _owner = owner;
            _document = document;
            _options = options;
            if (!_trackingCanvas)
            {
                Instances.CanvasCreated += OnCanvasCreated;
                Instances.CanvasDestroyed += OnCanvasDestroyed;
                _trackingCanvas = true;
            }
            WireCanvas(Instances.ActiveCanvas);
        });

        private void OnCanvasCreated(GH_Canvas canvas) => Guard("Canvas created", () => WireCanvas(canvas));
        private void OnCanvasDestroyed(GH_Canvas canvas) => Guard("Canvas destroyed", () =>
        {
            if (ReferenceEquals(canvas, _canvas)) UnwireCanvas();
        });

        private void WireCanvas(GH_Canvas canvas)
        {
            if (ReferenceEquals(canvas, _canvas)) return;
            UnwireCanvas();
            if (canvas == null || canvas.IsDisposed) return;
            _canvas = canvas;
            _canvas.DocumentChanged += OnDocumentChanged;
            _canvas.LostFocus += OnReset;
            _canvas.MouseDown += OnMouseReset;
            _canvas.MouseWheel += OnMouseReset;
            _canvas.Disposed += OnCanvasDisposed;
            _shortcut = CanvasShortcutHook.Attach(canvas, Keys.F, CanCaptureShortcut, Dispatch,
                () => Guard("Shortcut reset", ResetTaps));
            try { if (!_pillDisabled) _pill = _pillFactory(canvas, GuardPill); }
            catch (Exception error) { _log.Add($"Feedback pill: {error}"); }
        }

        private void UnwireCanvas()
        {
            ResetTaps();
            try { _shortcut?.Dispose(); }
            finally
            {
                _shortcut = null;
                Guard("Feedback dispose", () => _pill?.Dispose(), true);
                _pill = null;
                if (_canvas != null)
                {
                    _canvas.DocumentChanged -= OnDocumentChanged;
                    _canvas.LostFocus -= OnReset;
                    _canvas.MouseDown -= OnMouseReset;
                    _canvas.MouseWheel -= OnMouseReset;
                    _canvas.Disposed -= OnCanvasDisposed;
                    _canvas = null;
                }
            }
        }

        private void OnCanvasDisposed(object sender, EventArgs e) => Guard("Canvas disposed", UnwireCanvas);
        private void OnDocumentChanged(object sender, GH_CanvasDocumentChangedEventArgs e) => Guard("Canvas document changed", () =>
        {
            ResetTaps();
            GuardPill("Feedback hide", () => _pill?.Hide());
        });
        private void OnReset(object sender, EventArgs e) => Guard("Canvas focus", ResetTaps);
        private void OnMouseReset(object sender, MouseEventArgs e) => Guard("Canvas mouse", ResetTaps);
        private void ResetTaps() { _firstTap = null; _keyDown = false; }

        private bool CurrentCanvasDocument => _canvas != null && !_canvas.IsDisposed
            && ReferenceEquals(_canvas, Instances.ActiveCanvas) && ReferenceEquals(_canvas.Document, _document);

        private bool CanCaptureShortcut(Keys key)
        {
            bool eligible = false;
            Guard("Shortcut eligibility", () =>
            {
                var scope = key == Keys.F && _owner != null && _owner.SessionEligible
                && CurrentCanvasDocument && !_running && _canvas.Enabled && _canvas.Visible && _canvas.Focused
                && _canvas.ModifiersEnabled && Control.ModifierKeys == Keys.None && Control.MouseButtons == MouseButtons.None
                && !_canvas.IsActiveInteraction && !_canvas.IsActiveWidget && !_canvas.IsActiveObject
                && GH_Canvas.NavigationPanLeft != key && GH_Canvas.NavigationPanRight != key
                && GH_Canvas.NavigationPanUp != key && GH_Canvas.NavigationPanDown != key
                && GH_Canvas.NavigationZoomIn != key && GH_Canvas.NavigationZoomOut != key
                && !CanvasShortcutHook.HasReservedShortcut(_canvas, key);
                if (!scope) return;
                var selected = _document.SelectedObjects().ToArray();
                eligible = selected.Length == 0 ? GroupMembership.TopLevelGroups(_document).Any(g =>
                    (g.NickName ?? string.Empty).IndexOf("[lock]", StringComparison.OrdinalIgnoreCase) < 0)
                    : selected.Any(o => SelectionSnapshot.IsNode(o) || o is Grasshopper.Kernel.Special.GH_Group);
                if (!eligible)
                {
                    // Hint only: eligibility checks must preserve the last run's outputs.
                    ShowPill(selected.Length == 0 ? "No groups to format" : "Select components to format");
                }
            });
            return eligible;
        }

        private void Dispatch(Keys key, bool down) => Guard("Shortcut dispatch", () =>
        {
            if (!down) { _keyDown = false; return; }
            if (_keyDown) return;
            _keyDown = true;
            var timestamp = Stopwatch.GetTimestamp();
            if (!_firstTap.HasValue || (timestamp - _firstTap.Value) * 1000.0 / Stopwatch.Frequency
                > Math.Max(MinimumTapWindowMilliseconds, SystemInformation.DoubleClickTime))
            {
                var selected = _document.SelectedObjects();
                if (selected.Count == 0)
                {
                    int count = FormatAllCapture.EligibleCount(_document);
                    if (count == 0) { _firstTap = null; ShowPill("No groups to format"); return; }
                    _firstTap = timestamp;
                    ShowPill($"Format all groups ({count}) · tap F again");
                }
                else
                {
                    _firstTap = timestamp;
                    Show($"Format selection ({selected.Count(o => SelectionSnapshot.IsNode(o) || o is Grasshopper.Kernel.Special.GH_Group)} objects) · tap F again");
                }
                return;
            }
            _firstTap = null;
            if (_document.SelectedObjects().Count == 0) RunAll(); else Run();
        });

        internal void Run() => Run(false);
        internal void RunAll() => Run(true);

        private void Run(bool formatAll) => Guard(formatAll ? "Format all groups" : "Format selection", () =>
        {
            ResetTaps();
            if (_running) return;
            if (_owner == null) { Show("Formatter is not attached to a document"); return; }
            if (!_owner.SessionEligible) { Show("Formatter is disabled"); return; }
            if (!CurrentCanvasDocument) { Show("Open this document's canvas to format"); return; }
            if (_document.SolutionState == GH_ProcessStep.Process)
            {
                Show("Wait for the current solution to finish");
                return;
            }
            _running = true;
            LastMoves = 0;
            _log.Clear();
            _detail.Clear();
            try
            {
                if (formatAll)
                {
                    var all = FormatAllCapture.Capture(_document, _options);
                    _log.AddRange(all.Log);
                    var outcome = FormatAllSolver.Solve(all.Snapshot);
                    var layout = outcome.Layout;
                    foreach (var line in layout.Log)
                        if (layout.Notices.Contains(line)) _log.Add(line);
                        else _detail.Add(line);
                    if (!layout.Success) { Finish(layout.Diagnostic); return; }
                    var backward = BackwardWireCapture.Count(_document, layout.Moves, all.Objects);
                    bool applied = FormatApplier.Apply(_document, layout.Moves, all.Objects, out var allStatus, true, true, all.Membership);
                    if (applied)
                    {
                        LastMoves = layout.Moves.Count;
                        if (outcome.Skipped > 0) allStatus += $" ({outcome.Skipped} skipped)";
                        if (layout.Unstable) allStatus += " (not fully settled, FF again may adjust)";
                        allStatus = ReportBackward(allStatus, backward);
                    }
                    Finish(allStatus);
                    return;
                }
                var capture = SelectionSnapshot.Capture(_document, _options);
                // Skips are always visible. Debug additionally includes solver details.
                _log.AddRange(capture.Skipped);
                if (capture.Diagnostic != null) { Finish(capture.Diagnostic); return; }
                var locked = capture.Membership.LockedDiagnostic(capture.CandidateIds);
                if (locked != null) { Finish(locked); return; }
                if (capture.Objects.Count == 0) { Finish("Select components to format"); return; }
                var result = capture.Hierarchy == null ? _solve(capture.Snapshot) : _solveHierarchy(capture.Hierarchy);
                foreach (var line in result.Log)
                    if (result.Notices.Contains(line)) _log.Add(line);
                    else _detail.Add(line);
                if (!result.Success) { Finish(result.Diagnostic); return; }
                var backwardSelection = BackwardWireCapture.Count(_document, result.Moves, capture.Objects);
                var success = FormatApplier.Apply(_document, result.Moves, capture.Objects, out var status, capture.ContainsGroups, membership: capture.Membership);
                if (success) LastMoves = result.Moves.Count;
                if (success && result.Unstable) status += " (not fully settled, FF again may adjust)";
                if (success) status = ReportBackward(status, backwardSelection);
                Finish(status);
            }
            finally { _running = false; }
        });

        private void Finish(string status)
        {
            // Refused/rolled-back runs have no final moves or touched wires.
            if (!_log.Any(line => line.StartsWith("Backward wires: ", StringComparison.Ordinal)))
                _log.Add("Backward wires: 0 -> 0");
            _log.Insert(0, status);
            Show(status);
        }

        private string ReportBackward(string status, BackwardWireCounts counts)
        {
            _log.Add($"Backward wires: {counts.Before} -> {counts.After}" +
                (counts.Created > 0 ? $" ({counts.Created} new)" : string.Empty));
            return status + (counts.Created > 0 ? $" · {counts.Created} new backward wire(s)" : string.Empty);
        }

        private void Show(string status)
        {
            Status = status;
            ShowPill(status);
            _owner?.ScheduleOutputRefresh();
        }

        private void ShowPill(string status)
        {
            if (!_pillDisabled && CurrentCanvasDocument)
                GuardPill("Feedback show", () => _pill?.Show(status));
        }

        private void GuardPill(string where, Action action)
        {
            if (!_pillDisabled) Guard(where, action, true);
        }

        internal void Guard(string where, Action action) => Guard(where, action, false);

        private void Guard(string where, Action action, bool fromPill)
        {
            try { action(); }
            catch (Exception error)
            {
                if (fromPill)
                {
                    _pillDisabled = true;
                    try { _pill?.Disable(); }
                    catch (Exception) { /* Keep the original pill error if disabling also fails. */ }
                }
                ResetTaps();
                _running = false;
                LastMoves = 0;
                Status = "Error - see Log";
                _log.Add($"{where}: {error}");
                try { if (!fromPill) ShowPill(Status); }
                catch (Exception) { /* Reporting must not escape a host callback. */ }
                try { _owner?.ScheduleOutputRefresh(); }
                catch (Exception) { /* The Log output retains the original exception. */ }
            }
        }

        public void Dispose() => Guard("Dispose", () =>
        {
            try { UnwireCanvas(); }
            finally
            {
                if (_trackingCanvas)
                {
                    Instances.CanvasCreated -= OnCanvasCreated;
                    Instances.CanvasDestroyed -= OnCanvasDestroyed;
                    _trackingCanvas = false;
                }
                _owner = null;
                _document = null;
            }
        });
    }
}
