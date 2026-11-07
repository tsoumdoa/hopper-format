extern alias componentHarness;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HopperFormat;

internal static class Program
{
    private const int KeyDown = 0x100, KeyUp = 0x101, Character = 0x102;
    private static readonly int Observed = RegisterWindowMessage("Hopper.Grasshopper.CanvasShortcut.KeyObserved.v1");
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key, uint mode);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        using var form = new Form { KeyPreview = true, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000), Size = new Size(500, 300) };
        form.Shown += (_, _) => form.BeginInvoke((Action)(() =>
        {
            try { RunTests(form); _failures += HostAdapterTests.Run(); _failures += SessionTests.Run(form);
                _failures += componentHarness::ComponentRegressionTests.Run(); }
            catch (Exception error) { _failures++; Console.WriteLine("FAIL runner: " + error); }
            finally { form.Close(); }
        }));
        Application.Run(form);
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(Form form, string name, Action<Fixture> test)
    {
        try
        {
            using var fixture = new Fixture(form);
            test(fixture);
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error) { _failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static IntPtr KeyData(Keys key, bool up = false, bool repeat = false)
    {
        long value = 1L | ((long)MapVirtualKey((uint)key, 0) << 16);
        if (repeat || up) value |= 1L << 30;
        if (up) value |= 1L << 31;
        return new IntPtr(value);
    }

    private static void QueueTap(Control canvas, Keys key)
    {
        Assert(PostMessage(canvas.Handle, KeyDown, new IntPtr((int)key), KeyData(key)), "PostMessage down failed");
        Assert(PostMessage(canvas.Handle, KeyUp, new IntPtr((int)key), KeyData(key, up: true)), "PostMessage up failed");
    }

    private static void Tap(Control canvas, Keys key) { QueueTap(canvas, key); Application.DoEvents(); }
    private static void Send(Control canvas, int message, Keys key, bool repeat = false) =>
        SendMessage(canvas.Handle, message, new IntPtr((int)key), KeyData(key, message == KeyUp, repeat));

    private static void RunTests(Form form)
    {
        Check(form, "Baseline queued F reaches editor KeyPreview and WM_CHAR", f =>
        {
            f.Detach(); Tap(f.Canvas, Keys.F);
            Assert(f.ForwardedDown == 1 && f.Characters == 1, "baseline did not exercise WinForms preprocessing and TranslateMessage");
        });
        Check(form, "Two distinct F taps dispatch down/up and consume both keys and characters", f =>
        {
            QueueTap(f.Canvas, Keys.F); QueueTap(f.Canvas, Keys.F); Application.DoEvents();
            Assert(f.Down == 2 && f.Up == 2 && f.Activations == 1 && f.ForwardedDown == 0
                && f.ForwardedUp == 0 && f.Characters == 0, "FF dispatch or consumption failed");
        });
        Check(form, "Auto-repeated keydown bit 30 never dispatches a new tap", f =>
        {
            Send(f.Canvas, KeyDown, Keys.F);
            Send(f.Canvas, KeyDown, Keys.F, repeat: true); Send(f.Canvas, KeyDown, Keys.F, repeat: true);
            Send(f.Canvas, KeyUp, Keys.F);
            Assert(f.Down == 1 && f.Up == 1 && f.Activations == 0, "repeat became a tap");
            Tap(f.Canvas, Keys.F);
            Assert(f.Down == 2 && f.Activations == 1, "fresh second tap did not recover");
        });
        Check(form, "Auto-repeat after a focus reset never supplies a first tap", f =>
        {
            Tap(f.Canvas, Keys.F);
            using var editor = new TextBox(); form.Controls.Add(editor); editor.Focus(); f.Canvas.Focus();
            Send(f.Canvas, KeyDown, Keys.F, repeat: true); Send(f.Canvas, KeyUp, Keys.F); Tap(f.Canvas, Keys.F);
            Assert(f.Down == 2 && f.Activations == 0, "repeat survived focus reset as a tap");
        });
        Check(form, "A different key resets F without leaking delayed consumed characters", f =>
        {
            QueueTap(f.Canvas, Keys.F); QueueTap(f.Canvas, Keys.H); QueueTap(f.Canvas, Keys.F); Application.DoEvents();
            Assert(f.Activations == 0 && f.Pending == 1 && f.ForwardedDown == 1 && f.Characters == 1, "F-H-F completed FF or leaked F characters");
            Tap(f.Canvas, Keys.F); Assert(f.Activations == 1, "next F did not complete a fresh gesture");
        });
        Check(form, "Peer KeyObserved G down/up resets F state", f =>
        {
            Tap(f.Canvas, Keys.F);
            SendMessage(f.Canvas.Handle, Observed, new IntPtr((int)Keys.G), new IntPtr(KeyDown));
            SendMessage(f.Canvas.Handle, Observed, new IntPtr((int)Keys.G), new IntPtr(KeyUp));
            Tap(f.Canvas, Keys.F);
            Assert(f.Activations == 0 && f.Pending == 1, "observed peer key did not reset F");
        });
        Check(form, "WM_CHAR for a consumed F is swallowed by physical scan code", f =>
        {
            Send(f.Canvas, KeyDown, Keys.F); Send(f.Canvas, KeyUp, Keys.F);
            SendMessage(f.Canvas.Handle, Character, new IntPtr('x'), KeyData(Keys.F));
            Assert(f.Characters == 0, "consumed physical key character leaked on an alternate layout");
            SendMessage(f.Canvas.Handle, Character, new IntPtr('x'), KeyData(Keys.F));
            Assert(f.Characters == 1, "pending character count swallowed unrelated later input");
        });
        Check(form, "Consumed auto-repeat characters are swallowed too", f =>
        {
            Send(f.Canvas, KeyDown, Keys.F); Send(f.Canvas, KeyDown, Keys.F, repeat: true); Send(f.Canvas, KeyUp, Keys.F);
            SendMessage(f.Canvas.Handle, Character, new IntPtr('f'), KeyData(Keys.F));
            SendMessage(f.Canvas.Handle, Character, new IntPtr('f'), KeyData(Keys.F));
            Assert(f.Down == 1 && f.Characters == 0, "repeat dispatched or leaked a character");
        });
        Check(form, "Disposing the last registration releases the HWND subclass", f =>
        {
            Assert(!ReferenceEquals(NativeWindow.FromHandle(f.Canvas.Handle), f.BaselineWindow), "test never installed the native subclass");
            f.Detach();
            Assert(ReferenceEquals(NativeWindow.FromHandle(f.Canvas.Handle), f.BaselineWindow), "native subclass survived final dispose");
            Tap(f.Canvas, Keys.F);
            Assert(f.Down == 0 && f.ForwardedDown == 1 && f.Characters == 1, "disposed hook still consumes F");
        });
        Check(form, "Each registration declares its key and foreign keys reset it", f =>
        {
            int gDown = 0, gUp = 0, gPending = 0;
            using (CanvasShortcutHook.Attach(f.Canvas, Keys.G, _ => true,
                (_, down) => { if (down) { gDown++; gPending++; } else gUp++; }, () => gPending = 0))
            {
                Tap(f.Canvas, Keys.G); Tap(f.Canvas, Keys.F);
                Assert(gDown == 1 && gUp == 1 && gPending == 0 && f.Down == 1 && f.Pending == 1, "per-registration routing/reset failed");
                Tap(f.Canvas, Keys.G);
                Assert(f.Pending == 0 && gPending == 1 && f.Characters == 0, "G did not reset F or its character leaked");
            }
            Assert(!ReferenceEquals(NativeWindow.FromHandle(f.Canvas.Handle), f.BaselineWindow), "disposing one registration detached the other");
            Tap(f.Canvas, Keys.F); Assert(f.Down == 2, "remaining F registration stopped working");
        });
        foreach (bool peerLast in new[] { false, true })
            Check(form, "Peer plugin F-G-F and G-F-G reset both gestures (peer last: " + peerLast + ")", f =>
            {
                if (!peerLast) f.Detach();
                using var peer = new PeerHook(f.Canvas);
                if (!peerLast) f.Attach();
                QueueTap(f.Canvas, Keys.F); QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.F); Application.DoEvents();
                Assert(f.Activations == 0 && peer.Activations == 0 && f.Characters == 0, "F-G-F completed a gesture or leaked a letter");
                QueueTap(f.Canvas, Keys.G); QueueTap(f.Canvas, Keys.F); QueueTap(f.Canvas, Keys.G); Application.DoEvents();
                Assert(f.Activations == 0 && peer.Activations == 0 && f.Characters == 0, "G-F-G completed a gesture or leaked a letter");
                Tap(f.Canvas, Keys.G); Assert(peer.Activations == 1 && f.Activations == 0, "peer GG could not recover");
                Tap(f.Canvas, Keys.F); Tap(f.Canvas, Keys.F); Assert(f.Activations == 1, "FF could not recover");
            });
        Check(form, "Handle recreation resets taps and reattaches the subclass", f =>
        {
            Tap(f.Canvas, Keys.F); f.Canvas.Recreate(); f.Canvas.Focus(); Tap(f.Canvas, Keys.F);
            Assert(f.Activations == 0 && f.ForwardedDown == 0, "old gesture or missing hook after HWND recreation");
            Tap(f.Canvas, Keys.F); Assert(f.Activations == 1, "new HWND could not capture FF");
        });
        Check(form, "Text input focus preserves normal F typing", f =>
        {
            var original = new byte[256]; Assert(GetKeyboardState(original), "GetKeyboardState failed");
            var typing = (byte[])original.Clone(); typing[(int)Keys.CapsLock] = 0;
            try
            {
                // TranslateMessage uses this thread's lock state, which may be
                // inherited from the desktop. Keep the lowercase fixture stable.
                Assert(SetKeyboardState(typing), "SetKeyboardState failed");
                using var editor = new TextBox(); form.Controls.Add(editor); editor.Focus(); Tap(editor, Keys.F);
                Assert(editor.Text == "f" && f.Down == 0, "canvas hook stole text input");
            }
            finally { SetKeyboardState(original); }
        });
        Check(form, "Ineligible registration preserves host F and resets taps", f =>
        {
            Tap(f.Canvas, Keys.F); f.Eligible = false; Tap(f.Canvas, Keys.F); f.Eligible = true; Tap(f.Canvas, Keys.F);
            Assert(f.Activations == 0 && f.ForwardedDown == 1 && f.Characters == 1, "ineligible key captured or kept a stale tap");
        });
        Check(form, "Ctrl-F remains available to the host", f =>
        {
            var original = new byte[256]; Assert(GetKeyboardState(original), "GetKeyboardState failed");
            var modified = (byte[])original.Clone(); modified[(int)Keys.ControlKey] = 0x80;
            try
            {
                Assert(SetKeyboardState(modified), "SetKeyboardState failed");
                Send(f.Canvas, KeyDown, Keys.F); Send(f.Canvas, KeyUp, Keys.F);
                Assert(f.Down == 0 && f.ForwardedDown == 1, "modified F was intercepted");
            }
            finally { SetKeyboardState(original); }
        });
        Check(form, "Menu scanner matches exact shortcuts in nested menus", f =>
        {
            using var menu = new MenuStrip(); var parent = new ToolStripMenuItem("Parent");
            parent.DropDownItems.Add(new ToolStripMenuItem("Command") { ShortcutKeys = Keys.Control | Keys.F });
            menu.Items.Add(parent); form.Controls.Add(menu);
            Assert(CanvasShortcutHook.HasReservedShortcut(f.Canvas, Keys.Control | Keys.F)
                && !CanvasShortcutHook.HasReservedShortcut(f.Canvas, Keys.F), "menu scanner ignored modifiers or nested items");
        });
        Check(form, "Preprocessed menu commands interrupt F through KeyObserved", f =>
        {
            int clicks = 0; using var menu = new MenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Command", null, (_, _) => clicks++) { ShortcutKeys = Keys.F5 });
            form.Controls.Add(menu); form.MainMenuStrip = menu; f.Canvas.Focus();
            try
            {
                QueueTap(f.Canvas, Keys.F); QueueTap(f.Canvas, Keys.F5); QueueTap(f.Canvas, Keys.F); Application.DoEvents();
                Assert(clicks == 1 && f.Activations == 0, "menu preprocessing hid the interruption");
            }
            finally { form.MainMenuStrip = null; }
        });
    }

    private sealed class TestCanvas : Control { internal void Recreate() => RecreateHandle(); }

    private sealed class Fixture : IDisposable
    {
        internal readonly TestCanvas Canvas = new TestCanvas { Location = new Point(5, 30), Size = new Size(300, 200), TabStop = true };
        internal readonly NativeWindow BaselineWindow;
        internal int Down, Up, Pending, Activations, ForwardedDown, ForwardedUp, Characters;
        internal bool Eligible = true;
        private IDisposable _registration;
        private readonly Form _form;
        private readonly KeyEventHandler _downHandler, _upHandler;
        private readonly KeyPressEventHandler _characterHandler;

        internal Fixture(Form form)
        {
            _form = form;
            form.Controls.Add(Canvas); form.Activate(); Assert(Canvas.Focus(), "canvas focus failed");
            BaselineWindow = NativeWindow.FromHandle(Canvas.Handle);
            _downHandler = (_, e) => { if (Canvas.Focused && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) { ForwardedDown++; e.Handled = true; } };
            _upHandler = (_, e) => { if (Canvas.Focused && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) { ForwardedUp++; e.Handled = true; } };
            _characterHandler = (_, e) => { if (Canvas.Focused) { Characters++; e.Handled = true; } };
            form.KeyDown += _downHandler; form.KeyUp += _upHandler; form.KeyPress += _characterHandler;
            Attach();
        }

        internal void Attach() => _registration = CanvasShortcutHook.Attach(Canvas, Keys.F, _ => Eligible,
            (_, down) =>
            {
                if (down) { Down++; if (++Pending == 2) { Pending = 0; Activations++; } }
                else Up++;
            }, () => Pending = 0);
        internal void Detach() { _registration?.Dispose(); _registration = null; }
        public void Dispose()
        {
            Detach(); Canvas.Dispose();
            _form.KeyDown -= _downHandler; _form.KeyUp -= _upHandler; _form.KeyPress -= _characterHandler;
        }
    }

    // Independently subclassed peer, tested in both HWND subclass orders.
    private sealed class PeerHook : NativeWindow, IDisposable
    {
        private int _pending, _characters;
        internal int Activations;
        internal PeerHook(Control canvas) => AssignHandle(canvas.Handle);
        protected override void WndProc(ref Message message)
        {
            var key = (Keys)message.WParam.ToInt32();
            if (message.Msg == Observed)
            {
                if (key != Keys.G) _pending = 0;
                base.WndProc(ref message); return;
            }
            if (message.Msg == KeyDown)
            {
                SendMessage(Handle, Observed, message.WParam, new IntPtr(message.Msg));
                if (key == Keys.G)
                {
                    _characters++;
                    if (++_pending == 2) { _pending = 0; Activations++; }
                    return;
                }
            }
            else if (message.Msg == KeyUp && key == Keys.G) return;
            else if (message.Msg == Character && _characters > 0
                && ((message.LParam.ToInt64() >> 16) & 0xff) == MapVirtualKey((uint)Keys.G, 0))
            { _characters--; return; }
            base.WndProc(ref message);
        }
        public void Dispose() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
    }
}
