using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HopperFormat
{
    // Subclass only the canvas HWND. Its WndProc runs before Control forwards a
    // key to the editor's KeyPreview handler (which can forward letters to Rhino).
    // An application-wide filter/hook would also intercept unrelated windows.
    internal sealed class CanvasShortcutHook : NativeWindow
    {
        private const int KeyDown = 0x100, KeyUp = 0x101, Character = 0x102;
        private const int SystemKeyDown = 0x104, SystemKeyUp = 0x105, KillFocus = 0x8;
        private static readonly int KeyObserved = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? RegisterWindowMessage("Hopper.Grasshopper.CanvasShortcut.KeyObserved.v1") : 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        private static readonly Dictionary<Control, CanvasShortcutHook> Hooks =
            new Dictionary<Control, CanvasShortcutHook>();
        private readonly Control _canvas;
        private readonly List<Registration> _registrations = new List<Registration>();
        private Registration _pressedOwner;
        private Keys _pressedKey;
        private readonly Dictionary<int, int> _pendingCharacters = new Dictionary<int, int>();

        internal static IDisposable Attach(Control canvas, Keys key, Func<Keys, bool> eligible,
            Action<Keys, bool> dispatch, Action reset)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            if (!Hooks.TryGetValue(canvas, out var hook))
            {
                hook = new CanvasShortcutHook(canvas);
                Hooks.Add(canvas, hook);
            }
            var registration = new Registration(hook, key, eligible, dispatch, reset);
            hook._registrations.Add(registration);
            return registration;
        }

        internal static bool HasReservedShortcut(Control canvas, Keys key)
        {
            var form = canvas.FindForm();
            return form != null && HasReservedShortcut(form.Controls, key);
        }

        private static bool HasReservedShortcut(Control.ControlCollection controls, Keys key)
        {
            foreach (Control control in controls)
            {
                if (control is MenuStrip menu && HasReservedShortcut(menu.Items, key)) return true;
                if (control.HasChildren && HasReservedShortcut(control.Controls, key)) return true;
            }
            return false;
        }

        private static bool HasReservedShortcut(ToolStripItemCollection items, Keys key)
        {
            foreach (ToolStripItem item in items)
                if (item is ToolStripMenuItem menu && (menu.ShortcutKeys == key
                    || HasReservedShortcut(menu.DropDownItems, key))) return true;
            return false;
        }

        private CanvasShortcutHook(Control canvas)
        {
            _canvas = canvas;
            canvas.HandleCreated += OnHandleCreated;
            canvas.HandleDestroyed += OnHandleDestroyed;
            canvas.Disposed += OnDisposed;
            canvas.PreviewKeyDown += OnPreviewKeyDown;
            if (canvas.IsHandleCreated) AssignHandle(canvas.Handle);
        }

        private void OnHandleCreated(object sender, EventArgs e) => AssignHandle(_canvas.Handle);

        private void OnHandleDestroyed(object sender, EventArgs e)
        {
            Reset();
            _pendingCharacters.Clear();
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }

        private void OnDisposed(object sender, EventArgs e) => Detach();

        private void OnPreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            // WinForms can execute a menu command in ProcessCmdKey before any
            // canvas WndProc sees the key. Observe preprocessing without changing
            // IsInputKey or swallowing the host's command.
            SendMessage(Handle, KeyObserved, new IntPtr((int)e.KeyCode),
                new IntPtr(e.Modifiers == Keys.None ? KeyDown : SystemKeyDown));
        }

        private void Detach()
        {
            Reset();
            _pendingCharacters.Clear();
            _canvas.HandleCreated -= OnHandleCreated;
            _canvas.HandleDestroyed -= OnHandleDestroyed;
            _canvas.Disposed -= OnDisposed;
            _canvas.PreviewKeyDown -= OnPreviewKeyDown;
            if (Handle != IntPtr.Zero) ReleaseHandle();
            Hooks.Remove(_canvas);
            foreach (var registration in _registrations) registration.Hook = null;
            _registrations.Clear();
        }

        private void Reset()
        {
            _pressedOwner = null;
            foreach (var registration in _registrations.ToArray()) registration.Reset();
        }

        private bool HasCanvasFocus()
        {
            return !_canvas.IsDisposed && _canvas.Focused && _canvas.Enabled && _canvas.Visible
                && Control.ModifierKeys == Keys.None
                && Control.MouseButtons == MouseButtons.None;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == KeyObserved)
            {
                // Other Hopper plugins can subclass this HWND too. Observe keys
                // through every subclass before one plugin consumes the original.
                // Thus F-G-F and G-F-G can never count as double taps.
                var key = (Keys)message.WParam.ToInt32();
                var scope = (message.LParam.ToInt32() == KeyDown || message.LParam.ToInt32() == KeyUp)
                    && HasCanvasFocus();
                foreach (var item in _registrations.ToArray())
                    if (!scope || key != item.Key || !item.Eligible(key))
                    {
                        if (_pressedOwner == item) _pressedOwner = null;
                        item.Reset();
                    }
                base.WndProc(ref message);
                return;
            }
            if (message.Msg == KillFocus) Reset();
            if (message.Msg == KeyDown || message.Msg == SystemKeyDown)
            {
                SendMessage(Handle, KeyObserved, message.WParam, new IntPtr(message.Msg));
                var key = (Keys)message.WParam.ToInt32();
                var scope = message.Msg == KeyDown && HasCanvasFocus();
                var owner = scope
                    ? _registrations.FirstOrDefault(registration => registration.Key == key && registration.Eligible(key)) : null;
                if (owner == null)
                {
                    Reset();
                }
                else
                {
                    // Reset every other manager. Several enabled components must
                    // neither arm duplicate operations nor keep a stale first tap.
                    foreach (var registration in _registrations.ToArray())
                        if (registration != owner) registration.Reset();
                    if (key == owner.Key)
                    {
                        // WM_CHAR can arrive after a different queued keydown or
                        // keyup. Match physical scan codes instead of the literal
                        // letter so alternate keyboard layouts are covered too.
                        var scan = (int)((message.LParam.ToInt64() >> 16) & 0xff);
                        var count = Math.Max(1, (int)(message.LParam.ToInt64() & 0xffff));
                        _pendingCharacters.TryGetValue(scan, out var pending);
                        _pendingCharacters[scan] = (int)Math.Min(int.MaxValue, (long)pending + count);
                    }
                    // Bit 30 identifies physical auto-repeat even after focus or
                    // mouse changes have reset the manager's tap state.
                    var repeated = (message.LParam.ToInt64() & (1L << 30)) != 0;
                    if (!repeated && (_pressedOwner == null || _pressedKey != key))
                    {
                        _pressedOwner = owner;
                        _pressedKey = key;
                        owner.Dispatch(key, true);
                    }
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            else if (message.Msg == KeyUp || message.Msg == SystemKeyUp)
            {
                SendMessage(Handle, KeyObserved, message.WParam, new IntPtr(message.Msg));
                var key = (Keys)message.WParam.ToInt32();
                foreach (var item in _registrations.ToArray())
                    if (key != item.Key) item.Reset();
                if (_pressedOwner != null && key == _pressedKey)
                {
                    var owner = _pressedOwner;
                    _pressedOwner = null;
                    owner.Dispatch(_pressedKey, false);
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            else if (message.Msg == Character
                && _pendingCharacters.TryGetValue((int)((message.LParam.ToInt64() >> 16) & 0xff), out var pending))
            {
                var scan = (int)((message.LParam.ToInt64() >> 16) & 0xff);
                var count = Math.Max(1, (int)(message.LParam.ToInt64() & 0xffff));
                if (pending <= count) _pendingCharacters.Remove(scan);
                else _pendingCharacters[scan] = pending - count;
                // TranslateMessage may already have queued WM_CHAR before we
                // swallowed WM_KEYDOWN; consuming the latter alone is insufficient.
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }

        private sealed class Registration : IDisposable
        {
            internal CanvasShortcutHook Hook;
            internal readonly Keys Key;
            internal readonly Func<Keys, bool> Eligible;
            internal readonly Action<Keys, bool> Dispatch;
            internal readonly Action Reset;

            internal Registration(CanvasShortcutHook hook, Keys key, Func<Keys, bool> eligible,
                Action<Keys, bool> dispatch, Action reset)
            {
                Hook = hook;
                Key = key;
                Eligible = eligible;
                Dispatch = dispatch;
                Reset = reset;
            }

            public void Dispose()
            {
                var hook = Hook;
                if (hook == null) return;
                Hook = null;
                hook._registrations.Remove(this);
                if (hook._pressedOwner == this)
                {
                    hook._pressedOwner = null;
                }
                Reset();
                if (hook._registrations.Count == 0) hook.Detach();
            }
        }
    }
}
