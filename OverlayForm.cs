using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TrayMirror
{
    /// <summary>
    /// Shows mirrored launcher icons on the secondary display when supported apps are running.
    /// </summary>
    public class OverlayForm : Form
    {
        private const int IconSize = 20;
        private const int IconMargin = 6;
        private const int BarHeight = 32;
        private const int HardcodedRightAnchorOffset = 158;
        private const int HardcodedTopOffsetFromBottom = 76;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly System.Windows.Forms.Timer _pollTimer = new();
        private readonly MirroredAppTracker _tracker = new();
        private readonly TrayIconReader _trayIconReader = new();
        private readonly ManagedShellTrayService _managedShell;
        private readonly Screen _targetScreen;
        private readonly MirroredIconState[] _icons = MirroredAppTracker.DefaultApps
            .Select(app => new MirroredIconState(app))
            .ToArray();

        public OverlayForm(Screen targetScreen, ManagedShellTrayService managedShell)
        {
            _managedShell = managedShell;
            _targetScreen = targetScreen;

            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia; // 背景完全透明，只显示图标
            DoubleBuffered = true;

            MouseClick += OnMouseClick;
            MouseMove += (_, _) => Cursor = FindIconAt(PointToClient(Cursor.Position)) != null
                ? Cursors.Hand
                : Cursors.Default;

            _pollTimer.Interval = 1500;
            _pollTimer.Tick += (_, _) => SafeRefreshMirroredAppStates();
            _pollTimer.Start();

            SafeRefreshMirroredAppStates();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void PositionOverlay()
        {
            int runningCount = Math.Max(1, _icons.Count(i => i.IsRunning));
            int width = IconMargin * 2 + IconSize * runningCount + IconMargin * (runningCount - 1);
            var bounds = _targetScreen.Bounds;

            int rightAnchor = bounds.Right - HardcodedRightAnchorOffset;
            int x = rightAnchor - width;
            int y = bounds.Bottom - HardcodedTopOffsetFromBottom;

            Location = new Point(x, y);
            Size = new Size(width, BarHeight);
            UpdateIconBounds();
            Region = new Region(new RectangleF(0, 0, width, BarHeight));

            if (IsHandleCreated)
            {
                ForceOverlayToTop(x, y, width, BarHeight);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 不绘制任何背景 — TransparencyKey 处理透明度
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;

            // 为每个图标绘制微妙的圆形背景（提升在亮色/暗色桌面上的可见度）
            using var iconBgBrush = new SolidBrush(Color.FromArgb(38, 38, 38));
            foreach (var iconState in _icons.Where(i => i.IsRunning))
            {
                var bgRect = new Rectangle(
                    iconState.Bounds.X - 1, iconState.Bounds.Y - 1,
                    iconState.Bounds.Width + 2, iconState.Bounds.Height + 2);
                g.FillRectangle(iconBgBrush, bgRect);
            }

            // 绘制图标
            foreach (var iconState in _icons.Where(i => i.IsRunning))
            {
                var icon = iconState.Icon ?? SystemIcons.Application;
                g.DrawIcon(icon, iconState.Bounds);
            }
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
                _trayIconReader.Dispose();
                foreach (var icon in _icons)
                    icon.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SafeRefreshMirroredAppStates()
        {
            try
            {
                RefreshMirroredAppStates();
            }
            catch
            {
                if (_icons.Any(i => i.IsRunning))
                {
                    PositionOverlay();
                    if (!Visible)
                        Show();
                }
            }
        }

        private void RefreshMirroredAppStates()
        {
            bool stateChanged = false;
            var trayIcons = TryReadTrayIcons();

            try
            {
                foreach (var iconState in _icons)
                {
                    var state = _tracker.GetState(iconState.App, TryFindTrayIcon(trayIcons, iconState.App));
                    bool runningChanged = iconState.IsRunning != state.IsRunning;
                    bool iconChanged = !string.Equals(iconState.IconSourcePath, state.IconSourcePath, StringComparison.OrdinalIgnoreCase);

                    iconState.IsRunning = state.IsRunning;
                    iconState.IconSourcePath = state.IconSourcePath;
                    iconState.ProcessId = state.ProcessId;

                    if (iconChanged)
                        iconState.ReplaceIcon(state.Icon);
                    else
                        state.Icon?.Dispose();

                    stateChanged |= runningChanged || iconChanged;
                }
            }
            finally
            {
                foreach (var icon in trayIcons)
                    icon.Dispose();
            }

            if (_icons.Any(i => i.IsRunning))
            {
                PositionOverlay();
                if (!Visible)
                    Show();
            }
            else if (Visible)
            {
                Hide();
            }

            if (stateChanged)
                Invalidate();
            else if (_icons.Any(i => i.IsRunning))
                ForceOverlayToTop(Left, Top, Width, Height);
        }

        private void ForceOverlayToTop(int x, int y, int width, int height)
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            Invalidate();
        }

        private void OnMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            var icon = FindIconAt(e.Location);
            if (icon == null)
                return;

            // 优先：通过 ManagedShell 转发点击到真实托盘图标
            var mIcon = _managedShell.FindIconByKeywords(icon.App.TrayTooltipKeywords);
            if (mIcon != null)
            {
                var screenPt = PointToScreen(e.Location);
                _managedShell.ForwardClick(mIcon, screenPt.X, screenPt.Y);
                return;
            }

            // 回退：直接激活窗口
            _tracker.Activate(icon.App);
        }

        private MirroredIconState? FindIconAt(Point location)
        {
            return _icons.FirstOrDefault(icon => icon.IsRunning && icon.Bounds.Contains(location));
        }

        private void UpdateIconBounds()
        {
            int x = Width - IconMargin - IconSize;
            foreach (var iconState in _icons)
            {
                if (!iconState.IsRunning)
                {
                    iconState.Bounds = Rectangle.Empty;
                    continue;
                }

                iconState.Bounds = new Rectangle(x, (BarHeight - IconSize) / 2, IconSize, IconSize);
                x -= IconSize + IconMargin;
            }
        }

        private List<TrayIconInfo> TryReadTrayIcons()
        {
            try
            {
                return _trayIconReader.ReadIcons();
            }
            catch
            {
                return new List<TrayIconInfo>();
            }
        }

        private static Icon? TryFindTrayIcon(List<TrayIconInfo> trayIcons, MirroredAppDefinition app)
        {
            var match = trayIcons
                .Where(icon => icon.IsVisible && icon.Icon != null)
                .FirstOrDefault(icon => app.TrayTooltipKeywords.Any(keyword =>
                    icon.Tooltip.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

            return match?.Icon == null ? null : (Icon)match.Icon.Clone();
        }

        private sealed class MirroredIconState : IDisposable
        {
            public MirroredIconState(MirroredAppDefinition app)
            {
                App = app;
            }

            public MirroredAppDefinition App { get; }
            public bool IsRunning { get; set; }
            public int ProcessId { get; set; }
            public string? IconSourcePath { get; set; }
            public Icon? Icon { get; private set; }
            public Rectangle Bounds { get; set; }

            public void ReplaceIcon(Icon? icon)
            {
                Icon?.Dispose();
                Icon = icon;
            }

            public void Dispose()
            {
                Icon?.Dispose();
            }
        }

        private static Rectangle? TryFindTaskbarBounds(Rectangle targetScreenBounds, bool targetIsPrimary)
        {
            Rectangle? best = null;
            int bestArea = 0;
            var className = new System.Text.StringBuilder(256);

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                className.Clear();
                NativeMethods.GetClassName(hwnd, className, className.Capacity);
                string name = className.ToString();
                string wantedClass = targetIsPrimary ? "Shell_TrayWnd" : "Shell_SecondaryTrayWnd";
                if (!string.Equals(name, wantedClass, StringComparison.Ordinal))
                    return true;

                if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                    return true;

                var taskbar = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                var intersection = Rectangle.Intersect(targetScreenBounds, taskbar);
                int area = intersection.Width * intersection.Height;
                if (area > bestArea)
                {
                    best = taskbar;
                    bestArea = area;
                }

                return true;
            }, IntPtr.Zero);

            return best;
        }
    }
}
