using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;


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

            // 防止重复启动
            using var mutex = new System.Threading.Mutex(true, "TrayMirror_SingleInstance", out bool isNew);
            if (!isNew)
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

            // ManagedShell 托盘服务（用于可靠的点击转发）
            using var managedShell = new ManagedShellTrayService();

            // 为每个副屏创建一个 OverlayForm
            var overlays = secondaryScreens.Select(s => new OverlayForm(s, managedShell)).ToList();
            overlays.ForEach(f => f.Show());

            // 系统托盘图标（右键可退出）
            using var trayIcon = BuildTrayIcon(overlays);
            Application.Run();

            overlays.ForEach(f => f.Dispose());
        }

        private static NotifyIcon BuildTrayIcon(System.Collections.Generic.List<OverlayForm> overlays)
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
