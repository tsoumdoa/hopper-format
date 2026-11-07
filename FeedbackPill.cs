using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Grasshopper.GUI.Canvas;

namespace HopperFormat
{
    internal sealed class FeedbackPill : IDisposable
    {
        private readonly GH_Canvas _canvas;
        private readonly Timer _timer;
        private readonly Font _font;
        private readonly Action<string, Action> _guard;
        private string _text;

        internal FeedbackPill(GH_Canvas canvas, Action<string, Action> guard)
        {
            _canvas = canvas;
            _guard = guard;
            _font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 9);
            _timer = new Timer { Interval = 1500 };
            _timer.Tick += OnTick;
            _canvas.CanvasPostPaintOverlay += Paint;
        }

        internal void Show(string text)
        {
            _text = text;
            _timer.Stop();
            _timer.Start();
            _canvas.Invalidate();
        }

        internal void Hide()
        {
            _timer.Stop();
            _text = null;
            _canvas.Invalidate();
        }

        private void OnTick(object sender, EventArgs e) => _guard("Feedback timer", Hide);
        private void Paint(GH_Canvas canvas) => _guard("Feedback paint", () =>
        {
            if (string.IsNullOrEmpty(_text) || canvas.Graphics == null) return;
            var g = canvas.Graphics;
            var state = g.Save();
            try
            {
                g.ResetTransform();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float dpi = g.DpiX / 96f;
                float width = Math.Min(canvas.ClientSize.Width - 16 * dpi, g.MeasureString(_text, _font).Width + 40 * dpi);
                if (width <= 0) return;
                var box = new RectangleF((canvas.ClientSize.Width - width) / 2, canvas.ClientSize.Height - 40 * dpi, width, 28 * dpi);
                using (var pill = new GraphicsPath())
                using (var background = new SolidBrush(Color.FromArgb(235, 245, 245, 245)))
                using (var ink = new SolidBrush(Color.FromArgb(45, 45, 45)))
                using (var accent = new Pen(Color.FromArgb(147, 51, 234), 2 * dpi))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    float d = box.Height;
                    pill.AddArc(box.X, box.Y, d, d, 90, 180);
                    pill.AddArc(box.Right - d, box.Y, d, d, 270, 180);
                    pill.CloseFigure();
                    g.FillPath(background, pill);
                    g.DrawPath(accent, pill);
                    g.DrawString(_text, _font, ink, box, format);
                }
            }
            finally { g.Restore(state); }
        });

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _canvas.CanvasPostPaintOverlay -= Paint;
            _font.Dispose();
            _canvas.Invalidate();
        }
    }
}
