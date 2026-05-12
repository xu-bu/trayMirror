using System;
using System.Drawing;
using System.Windows.Forms;

namespace TrayMirror
{
    /// <summary>
    /// Shows a single WeChat launcher icon on the secondary display when WeChat is running.
    /// </summary>
    public class OverlayForm : Form
    {
        private const int IconSize = 18;
        private const int IconMargin = 6;
        private const int BarHeight = 32;
        private const int MarginRight = 8;
        private const int MarginBottom = 4;
        private const int LeftShift = 86;

        private readonly System.Windows.Forms.Timer _pollTimer = new();
        private readonly WeChatProcessTracker _tracker = new();
        private readonly Screen _targetScreen;

        private Rectangle _iconRect;
        private bool _isWeChatRunning;
        private Icon? _wechatIcon;

        public OverlayForm(Screen targetScreen)
        {
            _targetScreen = targetScreen;

            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 32, 32);
            Opacity = 0.92;
            DoubleBuffered = true;

            MouseClick += OnMouseClick;
            MouseMove += (_, _) => Cursor = _isWeChatRunning && _iconRect.Contains(PointToClient(Cursor.Position))
                ? Cursors.Hand
                : Cursors.Default;

            _pollTimer.Interval = 1500;
            _pollTimer.Tick += (_, _) => RefreshWeChatState();
            _pollTimer.Start();

            RefreshWeChatState();
        }

        public void PositionOverlay()
        {
            int width = IconMargin * 2 + IconSize;
            var bounds = _targetScreen.Bounds;
            var workingArea = _targetScreen.WorkingArea;

            int x = bounds.Right - width - MarginRight - LeftShift;
            int y = bounds.Bottom - BarHeight - MarginBottom;

            int bottomTaskbarThickness = Math.Max(0, bounds.Bottom - workingArea.Bottom);
            if (bottomTaskbarThickness > 0)
            {
                x = workingArea.Right - width - MarginRight - LeftShift;
                y = workingArea.Bottom + Math.Max(0, (bottomTaskbarThickness - BarHeight) / 2);
            }

            Location = new Point(x, y);
            Size = new Size(width, BarHeight);

            if (IsHandleCreated)
            {
                NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HWND_TOPMOST,
                    x,
                    y,
                    width,
                    BarHeight,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            _iconRect = new Rectangle(IconMargin, (BarHeight - IconSize) / 2, IconSize, IconSize);

            using var bgBrush = new SolidBrush(Color.FromArgb(32, 32, 32));
            g.FillRectangle(bgBrush, ClientRectangle);

            if (!_isWeChatRunning)
                return;

            var icon = _wechatIcon ?? SystemIcons.Application;
            g.DrawIcon(icon, _iconRect);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Region = new Region(new RectangleF(0, 0, Width, BarHeight));
            PositionOverlay();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pollTimer.Dispose();
                _tracker.Dispose();
                _wechatIcon?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void RefreshWeChatState()
        {
            var state = _tracker.GetState();
            bool runningChanged = _isWeChatRunning != state.IsRunning;
            bool iconChanged = !string.Equals(_tracker.LastIconSourcePath, state.IconSourcePath, StringComparison.OrdinalIgnoreCase);

            _isWeChatRunning = state.IsRunning;
            _tracker.LastIconSourcePath = state.IconSourcePath;

            if (iconChanged)
                ReplaceIcon(state.Icon);

            if (_isWeChatRunning)
            {
                PositionOverlay();
                if (!Visible)
                    Show();
            }
            else if (Visible)
            {
                Hide();
            }

            if (runningChanged || iconChanged)
                Invalidate();
        }

        private void ReplaceIcon(Icon? icon)
        {
            _wechatIcon?.Dispose();
            _wechatIcon = icon;
        }

        private void OnMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !_isWeChatRunning || !_iconRect.Contains(e.Location))
                return;

            _tracker.ActivateWeChat();
        }
    }
}
