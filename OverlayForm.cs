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
        private readonly System.Windows.Forms.Timer _updateTimer;
        private MirroredIconDefinition? _hoveredIcon;
        private float _hoverAnimationProgress;

        public Overlay(Screen targetScreen)
        {
            // to know which screen is secondary screen
            _targetScreen = targetScreen;
            using var reader = new TrayIconReader();
            _icons.AddRange(reader.ReadIcons());

            // Setup update timer for real-time sync
            _updateTimer = new System.Windows.Forms.Timer
            {
                Interval = 1000 // Update every 1 second
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
                Invalidate();
            };
            CreateHandle();
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
                if (FindIconAt(PointToClient(new Point(x, y))) == null)
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
            int width = IconWidth * runningCount;
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
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
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

            var icon = FindIconAt(e.Location);
            if (icon == null)
                return;

            Utils.TraceLog($"Overlay click: {icon.DisplayName} at ({e.X},{e.Y})");
            _tracker.Activate(icon);
        }

        private MirroredIconDefinition? FindIconAt(Point location)
        {
            return _icons.FirstOrDefault(icon => icon.Icon != null && icon.Bounds.Contains(location));
        }

        private void UpdateIconBounds()
        {
            int x = Width - IconWidth;
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
        /// Re-discover tray apps so apps started after TrayMirror appear on the overlay,
        /// and apps that exited disappear.
        /// </summary>
        private void SyncIcons()
        {
            MirroredIconDefinition[] latest;
            try
            {
                using var reader = new TrayIconReader();
                latest = reader.ReadIcons();
            }
            catch
            {
                return; // keep last known state on reader failure
            }

            var latestKeys = latest.Select(GetIconKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingKeys = _icons.Select(GetIconKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (int i = _icons.Count - 1; i >= 0; i--)
            {
                if (!latestKeys.Contains(GetIconKey(_icons[i])))
                {
                    _icons[i].Icon?.Dispose();
                    _icons.RemoveAt(i);
                }
            }

            foreach (var icon in latest)
            {
                if (!existingKeys.Contains(GetIconKey(icon)))
                    _icons.Add(icon);
            }
        }

        private static string GetIconKey(MirroredIconDefinition icon) =>
            string.Join("|", icon.CandidateProcessNames);

        private void OnUpdateTimerTick(object? sender, EventArgs e)
        {
            _updateTimer.Stop();

            try
            {
                MaintainZOrder();

                var previousStates = _icons.Select(icon => icon.Icon != null).ToArray();

                SyncIcons();
                _tracker.UpdateIconStates(_icons);

                bool needsRepaint = previousStates.Length != _icons.Count;
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
            }
            finally
            {
                _updateTimer.Start();
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
                _updateTimer?.Stop();
                _updateTimer?.Dispose();
                foreach (var icon in _icons)
                {
                    icon.Icon?.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
