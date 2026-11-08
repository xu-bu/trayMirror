using System;
using System.Windows.Forms;

namespace TrayMirror
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Utils.TraceLog("Main: starting initialization");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
            Utils.TraceLog("Main: after mutex");

            var screens = Screen.AllScreens;
            Utils.TraceLog($"Main: detected {screens.Length} screens");
            foreach (var screen in screens)
            {
                Utils.TraceLog($"Main: screen {screen.DeviceName}, primary={screen.Primary}, bounds={screen.Bounds}");
            }

            if (screens.Length < 2)
            {
                MessageBox.Show("未检测到副屏，请连接第二台显示器后重试。",
                    "TrayMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Utils.TraceLog("Main: after Screen.AllScreens");

            // 选取非主屏（可扩展为多副屏）
            var secondaryScreens = screens.Where(s => !s.Primary).ToArray();
            Utils.TraceLog($"Main: creating {secondaryScreens.Length} overlays");
            var overlays = secondaryScreens.Select(s => new Overlay(s)).ToList();
            Utils.TraceLog("Main: overlays created");

            // 为每个副屏创建一个 OverlayForm
            overlays.ForEach(f =>
            {
                f.PositionOverlay();
                f.Show();
            });
            Utils.TraceLog("Main: overlays positioned and shown");

            // 系统托盘图标（右键可退出）
            // 必须持有引用（using var），否则 NotifyIcon 会被 GC 回收导致图标消失
            // 延迟创建托盘图标，避免第一次开机时系统托盘未初始化导致卡死
            Utils.TraceLog("Main: waiting 5 seconds for system tray to initialize");
            System.Threading.Thread.Sleep(5000);
            Utils.TraceLog("Main: building tray icon");
            using var trayIcon = BuildTrayIcon(overlays);
            Utils.TraceLog("Main: tray icon built, starting Application.Run");

            Application.Run();

            overlays.ForEach(f => f.Dispose());
        }

        private static NotifyIcon BuildTrayIcon(System.Collections.Generic.List<Overlay> overlays)
        {
            var menu = new ContextMenuStrip();

            // 菜单显示期间暂停覆盖层刷新，避免扫描任务阻塞 UI 线程导致菜单卡顿
            menu.Opening += (_, _) => overlays.ForEach(f => f.SetRefreshPaused(true));
            menu.Closed += (_, _) => overlays.ForEach(f => f.SetRefreshPaused(false));

            menu.Items.Add("重新定位副屏覆盖层", null, (_, _) =>
            {
                overlays.ForEach(f =>
                {
                    f.PositionOverlay();
                    f.Invalidate();
                });
            });

            menu.Items.Add(new ToolStripSeparator());

            // 开机启动开关：勾选状态直接反映计划任务是否存在
            var startupItem = new ToolStripMenuItem("开机启动")
            {
                Checked = StartupRegistration.IsEnabled()
            };
            startupItem.Click += (_, _) =>
            {
                bool enable = !startupItem.Checked;
                if (StartupRegistration.SetEnabled(enable))
                {
                    startupItem.Checked = enable;
                    Utils.TraceLog($"TrayIcon: startup {(enable ? "enabled" : "disabled")}");
                }
                else
                {
                    MessageBox.Show(
                        enable
                            ? "设置开机启动失败。请以管理员身份运行 TrayMirror 后重试。"
                            : "取消开机启动失败。请以管理员身份运行 TrayMirror 后重试。",
                        "TrayMirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) =>
            {
                Application.Exit();
            });

            var icon = new NotifyIcon
            {
                Text = "TrayMirror - 副屏托盘镜像",
                Icon = LoadAppIcon(),
                ContextMenuStrip = menu,
                Visible = true,
            };

            return icon;
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                using var stream = typeof(Program).Assembly.GetManifestResourceStream("TrayMirror.app.ico");
                if (stream != null)
                    return new Icon(stream, SystemInformation.SmallIconSize);
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"TrayIcon: failed to load app icon: {ex.Message}");
            }

            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
