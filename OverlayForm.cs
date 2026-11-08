using System.Drawing.Drawing2D;

namespace TrayMirror
{
    /// <summary>
    /// Shows mirrored launcher icons on the secondary display when supported apps are running.
    /// </summary>
    public class Overlay : Form
    {
        private const int IconWidth = 60;
        private const int OverlayHeight = 60;
        private const int HardcodedRightAnchorOffset = 158;
        private const int overlayBottomMargin = 65;
        private const int HoverOffset = 8;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;

        private readonly MirroredAppTracker _tracker = new();
        private readonly Screen _targetScreen;
        private readonly List<MirroredIconDefinition> _icons = new();
        private readonly List<MirroredIconDefinition> _hiddenIcons = new();
        private readonly System.Windows.Forms.Timer _updateTimer;
        private readonly TrayDrawerForm _drawer;
        private MirroredIconDefinition? _hoveredIcon;
        private float _hoverAnimationProgress;
        private bool _menuPaused;
        private bool _drawerPaused;
        private bool _scanRunning;
        private bool _disposed;
        private Rectangle _chevronBounds = Rectangle.Empty;
        private bool _chevronHovered;

        public Overlay(Screen targetScreen)
        {
            Utils.TraceLog("Overlay: constructor starting");
            // to know which screen is secondary screen
            _targetScreen = targetScreen;
            Utils.TraceLog("Overlay: creating TrayIconReader");
            using var reader = new TrayIconReader();
            Utils.TraceLog("Overlay: calling ReadAll");
            var snapshot = reader.ReadAll();
            _icons.AddRange(snapshot.Visible);
            _hiddenIcons.AddRange(snapshot.Hidden);
            Utils.TraceLog($"Overlay: ReadAll returned {_icons.Count} visible, {_hiddenIcons.Count} hidden icons");

            // 隐藏图标抽屉：点镜像条右侧的 ^ 展开
            _drawer = new TrayDrawerForm();
            _drawer.IconActivated += icon => _tracker.Activate(icon);
            _drawer.VisibleChanged += (_, _) =>
            {
                SetDrawerPaused(_drawer.Visible);
                Invalidate();
            };

            // Setup update timer for real-time sync
            _updateTimer = new System.Windows.Forms.Timer
            {
                // 扫描在后台线程执行，间隔不必太密；托盘图标变化本来就不频繁
                Interval = 3000
            };
            _updateTimer.Tick += OnUpdateTimerTick;
            _updateTimer.Start();

            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Fuchsia;
            TransparencyKey = Color.Fuchsia; // 背景完全透明，只显示图标
            DoubleBuffered = true;

            MouseClick += OnMouseClick;
            MouseMove += OnMouseMove;
            MouseLeave += (sender, e) =>
            {
                _hoveredIcon = null;
                _hoverAnimationProgress = 0f;
                _chevronHovered = false;
                Invalidate();
            };
            CreateHandle();
            Utils.TraceLog("Overlay: constructor completed");
        }

        // prevent tray mirror stealing key board focus
        protected override bool ShowWithoutActivation => true;

        // make sure no matter where you click, tray mirror is always on top of screen
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // 非图标区域对鼠标事件完全穿透（HTTRANSPARENT）：
        // 右键/左键/移动全部直达下层窗口，覆盖层永远截不走任务栏的鼠标事件。
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int x = unchecked((short)(int)m.LParam);
                int y = unchecked((short)((int)m.LParam >> 16));
                var point = PointToClient(new Point(x, y));
                if (FindIconAt(point) == null && !IsChevronPoint(point))
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
            }

            base.WndProc(ref m);
        }

        // this func only draws overlay but not the icons inside
        public void PositionOverlay()
        {
            int runningCount = Math.Max(1, _icons.Count(i => i.Icon != null));
            int cellCount = runningCount + (HasHiddenIcons ? 1 : 0);
            int width = IconWidth * cellCount;
            var bounds = _targetScreen.Bounds;

            int rightAnchor = bounds.Right - HardcodedRightAnchorOffset;
            int x = rightAnchor - width;
            int y = bounds.Bottom - overlayBottomMargin;

            // Use SetWindowPos for reliable multi-monitor positioning
            NativeMethods.SetWindowPos(
                Handle,
                IntPtr.Zero,
                x, y, width, OverlayHeight,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            Size = new Size(width, OverlayHeight);
            UpdateIconBounds();

            if (_drawer.Visible)
                _drawer.UpdateAnchor(RectangleToScreen(ClientRectangle));
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 必须用 BackColor(Fuchsia) 真实填充背景，TransparencyKey 才能把该颜色键控为透明。
            // 空实现会导致客户区未绘制、露出黑色底。
            e.Graphics.Clear(BackColor);
        }
        // draw icons
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;

            // 绘制图标（不绘制底衬，背景完全透明）
            foreach (var icon in _icons.Where(i => i.Icon != null))
            {
                int yOffset = (icon == _hoveredIcon) ? (int)(HoverOffset * _hoverAnimationProgress) : 0;
                var drawBounds = new Rectangle(
                    icon.Bounds.X, icon.Bounds.Y - yOffset,
                    icon.Bounds.Width, icon.Bounds.Height);
                g.DrawIcon(icon.Icon ?? SystemIcons.Application, drawBounds);
            }

            if (HasHiddenIcons && _chevronBounds.Width > 0)
                DrawDrawerButton(g);
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            bool overChevron = IsChevronPoint(e.Location);
            if (overChevron != _chevronHovered)
            {
                _chevronHovered = overChevron;
                Invalidate();
            }

            if (overChevron)
            {
                _hoveredIcon = null;
                _hoverAnimationProgress = 0f;
                Cursor = Cursors.Hand;
                return;
            }

            var icon = FindIconAt(e.Location);
            if (icon != _hoveredIcon)
            {
                _hoveredIcon = icon;
                _hoverAnimationProgress = icon != null ? 0f : 1f;
                Invalidate();
            }

            Cursor = icon != null ? Cursors.Hand : Cursors.Default;

            // Animate hover effect
            if (_hoveredIcon != null && _hoverAnimationProgress < 1f)
            {
                _hoverAnimationProgress = Math.Min(1f, _hoverAnimationProgress + 0.15f);
                Invalidate();
            }
            else if (_hoveredIcon == null && _hoverAnimationProgress > 0f)
            {
                _hoverAnimationProgress = Math.Max(0f, _hoverAnimationProgress - 0.15f);
                Invalidate();
            }
        }

        private void OnMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            if (IsChevronPoint(e.Location))
            {
                ToggleDrawer();
                return;
            }

            var icon = FindIconAt(e.Location);
            if (icon == null)
                return;

            Utils.TraceLog($"Overlay click: {icon.DisplayName} at ({e.X},{e.Y})");
            _tracker.Activate(icon);
        }

        private bool IsChevronPoint(Point point) =>
            HasHiddenIcons && _chevronBounds.Contains(point);

        private bool HasHiddenIcons => _hiddenIcons.Any(icon => icon.Icon != null);

        private void ToggleDrawer()
        {
            if (_drawer.Visible)
            {
                _drawer.Hide();
                return;
            }

            var hidden = _hiddenIcons.Where(icon => icon.Icon != null).ToList();
            if (hidden.Count == 0)
                return;

            _drawer.ShowFor(RectangleToScreen(ClientRectangle), hidden);
            Invalidate();
        }

        private void DrawDrawerButton(Graphics g)
        {
            bool light = Utils.IsLightTheme();
            const int pillSize = 34;
            int lift = _chevronHovered ? 2 : 0;

            var pill = new Rectangle(
                _chevronBounds.X + (_chevronBounds.Width - pillSize) / 2,
                _chevronBounds.Y + (_chevronBounds.Height - pillSize) / 2 - lift,
                pillSize, pillSize);

            using var path = RoundedRect(pill, 9);
            Color fill = light
                ? (_chevronHovered ? Color.FromArgb(238, 238, 238) : Color.White)
                : (_chevronHovered ? Color.FromArgb(64, 64, 64) : Color.FromArgb(44, 44, 44));
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            using var pen = new Pen(
                light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(240, 240, 240), 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            int cx = pill.X + pill.Width / 2;
            int cy = pill.Y + pill.Height / 2;
            Point[] glyph = _drawer.Visible
                ? new[] { new Point(cx - 6, cy - 3), new Point(cx, cy + 3), new Point(cx + 6, cy - 3) }
                : new[] { new Point(cx - 6, cy + 3), new Point(cx, cy - 3), new Point(cx + 6, cy + 3) };
            g.DrawLines(pen, glyph);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private MirroredIconDefinition? FindIconAt(Point location)
        {
            return _icons.FirstOrDefault(icon => icon.Icon != null && icon.Bounds.Contains(location));
        }

        private void UpdateIconBounds()
        {
            int x = Width - IconWidth;

            // 最右侧留给“抽屉”按钮，可见图标从右往左排
            if (HasHiddenIcons)
            {
                _chevronBounds = new Rectangle(x, 0, IconWidth, OverlayHeight);
                x -= IconWidth;
            }
            else
            {
                _chevronBounds = Rectangle.Empty;
            }

            foreach (var icon in _icons)
            {
                if (icon.Icon == null)
                {
                    icon.Bounds = Rectangle.Empty;
                    continue;
                }

                icon.Bounds = new Rectangle(x, 0, IconWidth, OverlayHeight);
                x -= IconWidth;
            }
        }

        /// <summary>
        /// 把后台扫描结果合并进现有图标列表（只在 UI 线程调用），
        /// 同时用最新快照刷新进程 ID 和图标路径，替代原来的 UpdateIconStates 二次枚举。
        /// </summary>
        private void ApplySnapshot(TrayIconReader.TrayIconSnapshot snapshot)
        {
            string hiddenBefore = BuildIconSignature(_hiddenIcons);

            MergeIcons(_icons, snapshot.Visible);
            MergeIcons(_hiddenIcons, snapshot.Hidden);

            if (!_drawer.Visible)
                return;

            if (!HasHiddenIcons)
            {
                _drawer.Hide();
            }
            else if (hiddenBefore != BuildIconSignature(_hiddenIcons))
            {
                _drawer.UpdateIcons(_hiddenIcons.Where(icon => icon.Icon != null).ToList());
            }
        }

        private static void MergeIcons(
            List<MirroredIconDefinition> current,
            MirroredIconDefinition[] latest)
        {
            var latestKeys = latest.Select(GetIconKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = current.Count - 1; i >= 0; i--)
            {
                if (!latestKeys.Contains(GetIconKey(current[i])))
                {
                    current[i].Icon?.Dispose();
                    current.RemoveAt(i);
                }
            }

            var existingKeys = current.Select(GetIconKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var icon in latest)
            {
                string key = GetIconKey(icon);
                if (existingKeys.Contains(key))
                {
                    var existing = current.FirstOrDefault(item => GetIconKey(item) == key);
                    if (existing != null)
                        MergeIconState(existing, icon);
                    else
                        icon.Icon?.Dispose();
                    continue;
                }

                current.Add(icon);
                existingKeys.Add(key);
            }
        }

        private static void MergeIconState(MirroredIconDefinition existing, MirroredIconDefinition latest)
        {
            existing.ProcessId = latest.ProcessId;

            bool pathChanged = !string.Equals(
                existing.IconSourcePath, latest.IconSourcePath, StringComparison.OrdinalIgnoreCase);
            bool iconMissing = existing.Icon == null && latest.Icon != null;

            if ((pathChanged || iconMissing) && latest.Icon != null)
            {
                existing.Icon?.Dispose();
                existing.Icon = latest.Icon;
                latest.Icon = null; // 所有权转移，避免重复释放
            }
            else
            {
                // 已有同名图标，这次新提取的副本直接释放，避免堆积 GDI 句柄
                latest.Icon?.Dispose();
            }

            existing.IconSourcePath = latest.IconSourcePath;
        }

        private static void DisposeIcons(TrayIconReader.TrayIconSnapshot snapshot)
        {
            foreach (var icon in snapshot.Visible)
                icon.Icon?.Dispose();
            foreach (var icon in snapshot.Hidden)
                icon.Icon?.Dispose();
        }

        private static string BuildIconSignature(IEnumerable<MirroredIconDefinition> icons) =>
            string.Join("|", icons.Select(GetIconKey));

        private static string GetIconKey(MirroredIconDefinition icon) =>
            string.Join("|", icon.CandidateProcessNames);

        /// <summary>
        /// 托盘菜单/图标抽屉打开时暂停刷新：扫描（枚举进程 + 提取图标）跑在 UI 线程上，
        /// 会和菜单、抽屉共用消息循环，导致悬停卡顿、点桌面后延迟消失。
        /// </summary>
        public void SetRefreshPaused(bool paused)
        {
            if (_menuPaused == paused)
                return;

            _menuPaused = paused;
            Utils.TraceLog($"Refresh: menu {(paused ? "paused" : "resumed")}");
            UpdateRefreshTimer();
        }

        private void SetDrawerPaused(bool paused)
        {
            if (_drawerPaused == paused)
                return;

            _drawerPaused = paused;
            Utils.TraceLog($"Refresh: drawer {(paused ? "paused" : "resumed")}");
            UpdateRefreshTimer();
        }

        private bool RefreshPaused => _menuPaused || _drawerPaused;

        private void UpdateRefreshTimer()
        {
            if (_disposed)
                return;

            if (RefreshPaused)
                _updateTimer.Stop();
            else if (!_updateTimer.Enabled)
                _updateTimer.Start();
        }

        /// <summary>
        /// 进程枚举 + 图标提取（单次 1~2 秒）放到线程池，UI 线程只负责合并结果和重画，
        /// 扫描期间鼠标/菜单/抽屉消息不再被阻塞。
        /// </summary>
        private async void OnUpdateTimerTick(object? sender, EventArgs e)
        {
            if (RefreshPaused || _scanRunning)
                return;

            _scanRunning = true;
            try
            {
                MaintainZOrder();

                TrayIconReader.TrayIconSnapshot snapshot;
                try
                {
                    snapshot = await Task.Run(() =>
                    {
                        using var reader = new TrayIconReader();
                        return reader.ReadAll();
                    });
                }
                catch (Exception ex)
                {
                    Utils.TraceLog($"Refresh: scan failed: {ex.Message}");
                    return;
                }

                if (_disposed || IsDisposed)
                {
                    DisposeIcons(snapshot);
                    return;
                }

                // 扫描期间菜单/抽屉被打开：丢弃结果，恢复后下一轮再扫
                if (RefreshPaused)
                {
                    DisposeIcons(snapshot);
                    return;
                }

                var previousStates = _icons.Select(icon => icon.Icon != null).ToArray();
                bool hadDrawer = HasHiddenIcons;

                ApplySnapshot(snapshot);

                bool needsRepaint = hadDrawer != HasHiddenIcons || previousStates.Length != _icons.Count;
                if (!needsRepaint)
                {
                    for (int i = 0; i < _icons.Count; i++)
                    {
                        if (previousStates[i] != (_icons[i].Icon != null))
                        {
                            needsRepaint = true;
                            break;
                        }
                    }
                }

                if (needsRepaint)
                {
                    PositionOverlay();
                    Invalidate();
                }

                if (_drawer.Visible)
                    _drawer.Invalidate();
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"Refresh: apply failed: {ex.Message}");
            }
            finally
            {
                _scanRunning = false;
            }
        }

        /// <summary>
        /// 无条件把 overlay 重新置顶到 TopMost 层顶部，确保始终盖住主屏/副屏任务栏。
        /// 已是最顶时此调用是无操作，开销可忽略。
        /// </summary>
        private void MaintainZOrder()
        {
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _updateTimer?.Stop();
                _updateTimer?.Dispose();
                foreach (var icon in _icons)
                {
                    icon.Icon?.Dispose();
                }
                foreach (var icon in _hiddenIcons)
                {
                    icon.Icon?.Dispose();
                }
                _drawer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
