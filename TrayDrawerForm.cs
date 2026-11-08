using System.Drawing.Drawing2D;

namespace TrayMirror
{
    /// <summary>
    /// Windows 风格托盘抽屉：点击镜像条右侧的 ^ 后，在条上方弹出网格显示被隐藏的托盘图标。
    /// </summary>
    internal sealed class TrayDrawerForm : Form
    {
        private const int CellSize = 48;
        private const int CellGap = 6;
        private const int EdgePadding = 12;
        private const int MaxColumns = 5;
        private const int CornerRadius = 12;

        private readonly List<MirroredIconDefinition> _icons = new();
        private Rectangle _anchor;
        private int _hoveredIndex = -1;
        private bool _lightTheme = true;

        public event Action<MirroredIconDefinition>? IconActivated;

        public TrayDrawerForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;

            Deactivate += (_, _) =>
            {
                var fg = NativeMethods.GetForegroundWindow();
                var title = new System.Text.StringBuilder(256);
                NativeMethods.GetWindowText(fg, title, title.Capacity);
                var cls = new System.Text.StringBuilder(256);
                NativeMethods.GetClassName(fg, cls, cls.Capacity);
                Utils.TraceLog($"Drawer: deactivated, fg=0x{fg.ToInt64():X} class={cls} title={title}");
                Hide();
            };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                    Hide();
            };
            MouseMove += OnMouseMove;
            MouseClick += OnMouseClick;
            MouseLeave += (_, _) =>
            {
                if (_hoveredIndex != -1)
                {
                    _hoveredIndex = -1;
                    Invalidate();
                }
                Cursor = Cursors.Default;
            };
        }

        public void ShowFor(Rectangle anchor, IReadOnlyList<MirroredIconDefinition> icons)
        {
            _anchor = anchor;
            _lightTheme = Utils.IsLightTheme();
            UpdateIcons(icons);
            Show();
            BringToFront();
            Activate();
            Utils.TraceLog($"Drawer: shown bounds={Bounds}");
        }

        public void UpdateIcons(IReadOnlyList<MirroredIconDefinition> icons)
        {
            _icons.Clear();
            _icons.AddRange(icons.Where(icon => icon.Icon != null));
            _hoveredIndex = -1;
            ApplyLayout();
            Invalidate();
        }

        public void UpdateAnchor(Rectangle anchor)
        {
            _anchor = anchor;
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            if (_icons.Count == 0)
                return;

            int columns = Math.Min(MaxColumns, _icons.Count);
            int rows = (_icons.Count + MaxColumns - 1) / MaxColumns;
            int width = EdgePadding * 2 + columns * CellSize + (columns - 1) * CellGap;
            int height = EdgePadding * 2 + rows * CellSize + (rows - 1) * CellGap;

            var screen = Screen.FromRectangle(_anchor);
            var workArea = screen.WorkingArea;

            int x = _anchor.Right - width;
            int y = _anchor.Top - height - 8;
            if (y < workArea.Top + 4)
                y = _anchor.Bottom + 8;

            if (x < workArea.Left + 4)
                x = workArea.Left + 4;
            if (x + width > workArea.Right - 4)
                x = workArea.Right - width - 4;
            if (y + height > workArea.Bottom - 4)
                y = workArea.Bottom - height - 4;

            Bounds = new Rectangle(x, y, width, height);

            var oldRegion = Region;
            using var path = RoundedRect(new Rectangle(0, 0, width, height), CornerRadius);
            Region = new Region(path);
            oldRegion?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color background = _lightTheme ? Color.White : Color.FromArgb(32, 32, 32);
            g.Clear(background);

            for (int i = 0; i < _icons.Count; i++)
            {
                var cell = GetCell(i);

                if (i == _hoveredIndex)
                {
                    using var hoverPath = RoundedRect(new Rectangle(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4), 8);
                    using var hoverBrush = new SolidBrush(_lightTheme
                        ? Color.FromArgb(235, 235, 235)
                        : Color.FromArgb(56, 56, 56));
                    g.FillPath(hoverBrush, hoverPath);
                }

                var icon = _icons[i].Icon;
                if (icon == null)
                    continue;

                const int iconSize = 32;
                var target = new Rectangle(
                    cell.X + (cell.Width - iconSize) / 2,
                    cell.Y + (cell.Height - iconSize) / 2,
                    iconSize, iconSize);
                g.DrawIcon(icon, target);
            }

            using var borderPen = new Pen(_lightTheme
                ? Color.FromArgb(30, 0, 0, 0)
                : Color.FromArgb(60, 255, 255, 255));
            using var borderPath = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
            g.DrawPath(borderPen, borderPath);
        }

        private Rectangle GetCell(int index)
        {
            int column = index % MaxColumns;
            int row = index / MaxColumns;
            return new Rectangle(
                EdgePadding + column * (CellSize + CellGap),
                EdgePadding + row * (CellSize + CellGap),
                CellSize, CellSize);
        }

        private int HitTest(Point location)
        {
            for (int i = 0; i < _icons.Count; i++)
            {
                if (GetCell(i).Contains(location))
                    return i;
            }

            return -1;
        }

        private void OnMouseMove(object? sender, MouseEventArgs e)
        {
            int index = HitTest(e.Location);
            if (index != _hoveredIndex)
            {
                _hoveredIndex = index;
                Invalidate();
            }

            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        }

        private void OnMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            int index = HitTest(e.Location);
            if (index < 0)
                return;

            var icon = _icons[index];
            Hide();
            IconActivated?.Invoke(icon);
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
    }
}
