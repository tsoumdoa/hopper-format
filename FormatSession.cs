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
        private readonly Func<GH_Canvas, Action<string, Action>, FeedbackPill> _pillFactory;
        private bool _trackingCanvas, _running, _keyDown;
        private long? _firstTap;
        private Options _options = new Options();
        private readonly List<string> _log = new List<string>();
        private readonly List<string> _detail = new List<string>();

        internal string Status { get; private set; } = "Select components to format";
        internal int LastMoves { get; private set; }
        internal IEnumerable<string> Log(bool debug) => debug ? _log.Concat(_detail) : _log;

        internal FormatSession(Func<GH_Canvas, Action<string, Action>, FeedbackPill> pillFactory = null)
        {
            _pillFactory = pillFactory ?? ((canvas, guard) => new FeedbackPill(canvas, guard));
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
            try { _pill = _pillFactory(canvas, Guard); }
            catch (Exception error) { _log.Add($"Feedback pill: {error}"); }
        }

        private void UnwireCanvas()
        {
            ResetTaps();
            try { _shortcut?.Dispose(); }
            finally
            {
                _shortcut = null;
                _pill?.Dispose();
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
            _pill?.Hide();
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
                var selected = _document.SelectedObjects();
                eligible = selected.Any(SelectionSnapshot.IsNode);
                if (!eligible)
                {
                    // Hint only: eligibility checks must preserve the last run's outputs.
                    _pill?.Show("Select components to format");
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
                _firstTap = timestamp;
                Show($"Format selection ({_document.SelectedObjects().Count(SelectionSnapshot.IsNode)} objects) · tap F again");
                return;
            }
            _firstTap = null;
            Run();
        });

        internal void Run() => Guard("Format selection", () =>
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
                var capture = SelectionSnapshot.Capture(_document, _options);
                // Skips are always visible. Debug additionally includes solver details.
                _log.AddRange(capture.Skipped);
                var locked = GroupMembership.LockedDiagnostic(_document, capture.CandidateIds);
                if (locked != null) { Finish(locked); return; }
                if (capture.Objects.Count == 0) { Finish("Select components to format"); return; }
                var result = LayoutSolver.Solve(capture.Snapshot);
                _detail.AddRange(result.Log);
                if (!result.Success) { Finish(result.Diagnostic); return; }
                var success = FormatApplier.Apply(_document, result.Moves, capture.Objects, out var status);
                if (success) LastMoves = result.Moves.Count;
                Finish(status);
            }
            finally { _running = false; }
        });

        private void Finish(string status)
        {
            _log.Insert(0, status);
            Show(status);
        }

        private void Show(string status)
        {
            Status = status;
            if (CurrentCanvasDocument) _pill?.Show(status);
            _owner?.ScheduleOutputRefresh();
        }

        internal void Guard(string where, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                ResetTaps();
                _running = false;
                LastMoves = 0;
                Status = "Error - see Log";
                _log.Add($"{where}: {error}");
                try { if (CurrentCanvasDocument) _pill?.Show(Status); }
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
