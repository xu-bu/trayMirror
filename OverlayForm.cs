using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TrayMirror
{
    /// <summary>
    /// 副屏右下角的无边框覆盖窗口，模拟系统托盘外观。
    /// 包含：图标条 + 系统信息栏（时钟、音量、网络）。
    /// </summary>
    public class OverlayForm : Form
    {
        // ── 布局常量 ────────────────────────────────────────────────
        private const int IconSize = 16;
        private const int IconPadding = 4;
        private const int BarHeight = 32;
        private const int InfoWidth = 120;  // 时钟 + 网络区宽度
        private const int MarginRight = 8;
        private const int MarginBottom = 4;

        // ── 依赖 ────────────────────────────────────────────────────
        private readonly SystemInfoProvider _sysInfo = new();
        private readonly TrayIconReader _reader = new();
        private readonly TrayWatcher _watcher = new();
        private readonly System.Windows.Forms.Timer _refreshTimer = new();
        private readonly System.Windows.Forms.Timer _clockTimer = new();

        // ── 状态 ────────────────────────────────────────────────────
        private List<TrayIconInfo> _icons = new();
        private Screen _targetScreen;
        private bool _pendingRefresh = false;

        // 图标点击区域映射（用于点击转发）
        private readonly List<(Rectangle Rect, TrayIconInfo Info)> _hitZones = new();

        public OverlayForm(Screen targetScreen)
        {
            _targetScreen = targetScreen;

            // ── 窗口属性 ────────────────────────────────────────────
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(32, 32, 32);
            Opacity = 0.92;
            DoubleBuffered = true;

            // 让窗口透过鼠标到底层（可选：如果想要点击穿透就开启）
            // SetWindowLong(Handle, GWL_EXSTYLE, GetWindowLong(Handle, GWL_EXSTYLE) | WS_EX_TRANSPARENT);

            PositionOnScreen();

            // ── 定时刷新（托盘图标变化节流，500ms）────────────────
            _refreshTimer.Interval = 500;
            _refreshTimer.Tick += (_, _) =>
            {
                if (_pendingRefresh)
                {
                    _pendingRefresh = false;
                    RefreshIcons();
                }
            };
            _refreshTimer.Start();

            // ── 时钟每秒刷新 ─────────────────────────────────────
            _clockTimer.Interval = 1000;
            _clockTimer.Tick += (_, _) => Invalidate();
            _clockTimer.Start();

            // ── WinEvent 钩子 ────────────────────────────────────
            _watcher.TrayChanged += (_, _) => _pendingRefresh = true;
            _watcher.Start();

            // ── 鼠标事件 ─────────────────────────────────────────
            MouseClick += OnMouseClick;
            MouseMove += (_, _) => Cursor = Cursors.Default;

            // 初次读取
            RefreshIcons();
        }

        // ── 定位到目标屏幕右下角 ─────────────────────────────────────
        private void PositionOnScreen()
        {
            int iconCount = Math.Max(_icons?.Count ?? 0, 4); // 预估宽度
            int iconsWidth = iconCount * (IconSize + IconPadding) + IconPadding;
            int totalWidth = iconsWidth + InfoWidth + MarginRight;
            totalWidth = Math.Max(totalWidth, 200);

            var bounds = _targetScreen.WorkingArea;
            Location = new Point(
                bounds.Right - totalWidth - MarginRight,
                bounds.Bottom - BarHeight - MarginBottom);
            Size = new Size(totalWidth, BarHeight);
        }

        // ── 刷新图标列表 ─────────────────────────────────────────────
        private void RefreshIcons()
        {
            foreach (var ic in _icons) ic.Dispose();
            _icons = _reader.ReadIcons();

            // 重新计算窗口宽度
            PositionOnScreen();
            Invalidate();
        }

        // ── 绘制 ─────────────────────────────────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            _hitZones.Clear();
            int x = IconPadding;
            int y = (BarHeight - IconSize) / 2;

            // ── 绘制托盘图标 ─────────────────────────────────────
            foreach (var info in _icons)
            {
                if (!info.IsVisible) continue;

                var rect = new Rectangle(x, y, IconSize, IconSize);
                _hitZones.Add((rect, info));

                if (info.Icon != null)
                {
                    NativeMethods.DrawIconEx(g.GetHdc(), x, y, info.Icon.Handle,
                        IconSize, IconSize, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
                    g.ReleaseHdc();
                }
                else
                {
                    // 无图标时画占位方块
                    using var placeholderBrush = new SolidBrush(Color.FromArgb(80, 80, 80));
                    g.FillRectangle(placeholderBrush, rect);
                }

                x += IconSize + IconPadding;
            }

            // ── 分隔线 ───────────────────────────────────────────
            int sepX = Width - InfoWidth - 1;
            using (var sepPen = new Pen(Color.FromArgb(60, 60, 60), 1))
                g.DrawLine(sepPen, sepX, 6, sepX, BarHeight - 6);

            // ── 系统信息区 ───────────────────────────────────────
            DrawSystemInfo(g, sepX + 8);
        }

        private void DrawSystemInfo(Graphics g, int startX)
        {
            using var font = new Font("Segoe UI", 8f, FontStyle.Regular);
            using var smallFont = new Font("Segoe UI", 7f, FontStyle.Regular);
            using var brush = new SolidBrush(Color.FromArgb(220, 220, 220));
            using var mutedBrush = new SolidBrush(Color.FromArgb(140, 140, 140));

            // 网络状态
            string netLabel = _sysInfo.GetNetworkLabel();
            string volLabel = $"♪ {_sysInfo.GetVolumePercent()}%";
            string timeStr = _sysInfo.GetTimeString();
            string dateStr = _sysInfo.GetDateString();

            // 时间（右对齐）
            int timeX = Width - MarginRight;
            var timeSize = g.MeasureString(timeStr, font);
            var dateSize = g.MeasureString(dateStr, smallFont);
            g.DrawString(timeStr, font, brush,
                timeX - timeSize.Width, (BarHeight / 2) - timeSize.Height - 1);
            g.DrawString(dateStr, smallFont, mutedBrush,
                timeX - dateSize.Width, BarHeight / 2 + 1);

            // 网络 + 音量（时间左侧）
            float infoRight = timeX - Math.Max(timeSize.Width, dateSize.Width) - 8;
            var netSize = g.MeasureString(netLabel, smallFont);
            var volSize = g.MeasureString(volLabel, smallFont);
            g.DrawString(netLabel, smallFont, mutedBrush,
                infoRight - netSize.Width, (BarHeight / 2) - netSize.Height);
            g.DrawString(volLabel, smallFont, mutedBrush,
                infoRight - volSize.Width, BarHeight / 2 + 1);
        }

        // ── 点击转发到主屏托盘 ───────────────────────────────────────
        private void OnMouseClick(object? sender, MouseEventArgs e)
        {
            foreach (var (rect, info) in _hitZones)
            {
                if (!rect.Contains(e.Location)) continue;

                // 找到主屏对应的真实图标位置并模拟点击
                IntPtr toolbar = FindTrayToolbar();
                if (toolbar == IntPtr.Zero) break;

                // 获取该图标在主屏上的屏幕坐标
                // 简化策略：点击对应索引位置中心点
                NativeMethods.GetWindowRect(toolbar, out var tbRect);
                int realX = tbRect.Left + info.Index * (IconSize + IconPadding) + IconSize / 2;
                int realY = tbRect.Top + IconSize / 2;

                IntPtr lParam = MakeLParam(realX - tbRect.Left, realY - tbRect.Top);

                if (e.Button == MouseButtons.Left)
                {
                    NativeMethods.PostMessage(toolbar, NativeMethods.WM_LBUTTONDOWN,
                        new IntPtr(1), lParam);
                    NativeMethods.PostMessage(toolbar, NativeMethods.WM_LBUTTONUP,
                        IntPtr.Zero, lParam);
                }
                else if (e.Button == MouseButtons.Right)
                {
                    NativeMethods.PostMessage(toolbar, NativeMethods.WM_RBUTTONDOWN,
                        new IntPtr(2), lParam);
                    NativeMethods.PostMessage(toolbar, NativeMethods.WM_RBUTTONUP,
                        IntPtr.Zero, lParam);
                }
                break;
            }
        }

        private static IntPtr FindTrayToolbar()
        {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return IntPtr.Zero;
            IntPtr notify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return IntPtr.Zero;
            IntPtr sysPager = NativeMethods.FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
            if (sysPager == IntPtr.Zero) return IntPtr.Zero;
            return NativeMethods.FindWindowEx(sysPager, IntPtr.Zero, "ToolbarWindow32", null);
        }

        private static IntPtr MakeLParam(int lo, int hi)
            => new IntPtr((hi << 16) | (lo & 0xFFFF));

        // ── 圆角边框 ─────────────────────────────────────────────────
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var region = new System.Drawing.Region(
                new RectangleF(0, 0, Width, BarHeight));
            Region = region;
        }

        // ── 清理 ─────────────────────────────────────────────────────
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Dispose();
                _clockTimer.Dispose();
                _watcher.Dispose();
                _reader.Dispose();
                foreach (var ic in _icons) ic.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}