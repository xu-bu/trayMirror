using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ManagedShell;
using ManagedShell.WindowsTray;
using MNotifyIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace TrayMirror
{
    /// <summary>
    /// 封装 ManagedShell 的托盘服务，用于读取托盘图标和转发鼠标点击。
    /// </summary>
    public class ManagedShellTrayService : IDisposable
    {
        private readonly ShellManager _shellManager;
        private bool _initialized;

        public NotificationArea NotificationArea { get; }

        public ManagedShellTrayService()
        {
            var config = ShellManager.DefaultShellConfig;
            config.EnableTrayService = true;
            config.AutoStartTrayService = true;

            _shellManager = new ShellManager(config);
            NotificationArea = _shellManager.NotificationArea;

            // wait briefly for the tray service to initialize
            _initialized = true;
        }

        /// <summary>
        /// 在 ManagedShell 托管的托盘图标中查找匹配指定关键词的 NotifyIcon。
        /// </summary>
        public MNotifyIcon? FindIconByKeywords(string[] keywords)
        {
            if (!_initialized) return null;

            var allIcons = new List<MNotifyIcon>();
            allIcons.AddRange(EnumerateCollection(NotificationArea.PinnedIcons));
            allIcons.AddRange(EnumerateCollection(NotificationArea.UnpinnedIcons));

            return allIcons.FirstOrDefault(icon =>
            {
                string? tip = TryGetTooltip(icon);
                return !string.IsNullOrEmpty(tip)
                       && keywords.Any(k => tip.Contains(k, StringComparison.OrdinalIgnoreCase));
            });
        }

        /// <summary>
        /// 向目标应用的托盘回调窗口转发左键单击。
        /// </summary>
        public void ForwardClick(MNotifyIcon notifyIcon, int screenX, int screenY)
        {
            uint messageExtraInfo = (uint)((screenY << 16) | (screenX & 0xFFFF));
            int doubleClickTime = SystemInformation.DoubleClickTime;

            notifyIcon.IconMouseDown(System.Windows.Input.MouseButton.Left, messageExtraInfo, doubleClickTime);
            notifyIcon.IconMouseUp(System.Windows.Input.MouseButton.Left, messageExtraInfo, doubleClickTime);
        }

        /// <summary>
        /// 返回 ManagedShell 已知的所有托盘图标数量。
        /// </summary>
        public int TotalIconCount =>
            EnumerateCollection(NotificationArea.PinnedIcons).Count()
            + EnumerateCollection(NotificationArea.UnpinnedIcons).Count();

        /// <summary>
        /// 返回所有托盘图标的 tooltip 列表（用于调试）。
        /// </summary>
        public string[] GetAllTooltips()
        {
            var allIcons = new List<MNotifyIcon>();
            allIcons.AddRange(EnumerateCollection(NotificationArea.PinnedIcons));
            allIcons.AddRange(EnumerateCollection(NotificationArea.UnpinnedIcons));
            return allIcons.Select(icon => TryGetTooltip(icon) ?? "(no tooltip)").ToArray();
        }

        public void Dispose()
        {
            _shellManager.Dispose();
        }

        private static IEnumerable<MNotifyIcon> EnumerateCollection(ICollectionView collection)
        {
            foreach (var item in collection)
            {
                if (item is MNotifyIcon icon)
                    yield return icon;
            }
        }

        private static string? TryGetTooltip(MNotifyIcon icon)
        {
            try
            {
                var type = typeof(MNotifyIcon);

                var prop = type.GetProperty("ToolTip",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(icon) as string;

                var field = type.GetField("ToolTip",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                    return field.GetValue(icon) as string;

                // fallback: Title
                var titleProp = type.GetProperty("Title",
                    BindingFlags.Public | BindingFlags.Instance);
                return titleProp?.GetValue(icon) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
