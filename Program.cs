namespace TrayMirror
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StartupRegistration.EnsureRegistered();

            // 注意：不要调用 ReleaseStuckModifierKeys()（b9c846a 引入）。
            // 它用 SendInput 注入修饰键 KEYUP，会触发系统输入钩子把右键状态卡为
            // DOWN，导致任务栏按钮点击/右键全部失灵（2026-08-14 已定位并复现）。

            // when first run this program, it will get the mutex as true
            // then the second run try to get the mutex, it will get false
            using var mutex = new Mutex(true, "TrayMirror_SingleInstance", out bool haveMutex);
            if (!haveMutex)
            {
                MessageBox.Show("TrayMirror 已在运行中。", "TrayMirror",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var screens = Screen.AllScreens;
            if (screens.Length < 2)
            {
                MessageBox.Show("未检测到副屏，请连接第二台显示器后重试。",
                    "TrayMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 选取非主屏（可扩展为多副屏）
            var secondaryScreens = screens.Where(s => !s.Primary).ToArray();
            var overlays = secondaryScreens.Select(s => new Overlay(s)).ToList();

            // 为每个副屏创建一个 OverlayForm
            overlays.ForEach(f =>
            {
                f.PositionOverlay();
                f.Show();
            });

            // 系统托盘图标（右键可退出）

            Application.Run();

            overlays.ForEach(f => f.Dispose());
        }

        private static NotifyIcon BuildTrayIcon(System.Collections.Generic.List<Overlay> overlays)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("重新定位副屏覆盖层", null, (_, _) =>
            {
                overlays.ForEach(f =>
                {
                    f.PositionOverlay();
                    f.Invalidate();
                });
            });

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                Application.Exit();
            });

            var icon = new NotifyIcon
            {
                Text = "TrayMirror - 副屏托盘镜像",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };

            return icon;
        }
    }
}
