using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace TrayMirror
{
    /// <summary>
    /// Uses the managed UI Automation API to read tray items on newer Windows 11 builds
    /// where the taskbar is XAML/WinUI-backed and no longer exposes ToolbarWindow32.
    /// </summary>
    internal class UiAutomationTrayReader
    {
        private static readonly string[] CandidateRootClasses =
        {
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow",
        };

        public List<TrayIconInfo> Read()
        {
            var result = new List<TrayIconInfo>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var root = AutomationElement.RootElement;

            foreach (var rootClass in CandidateRootClasses)
            {
                var classCondition = new PropertyCondition(AutomationElement.ClassNameProperty, rootClass);
                var trayRoot = root.FindFirst(TreeScope.Descendants, classCondition);
                if (trayRoot == null)
                    continue;

                var buttons = trayRoot.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                foreach (AutomationElement button in buttons)
                {
                    if (!TryCreateTrayIcon(button, out var icon))
                        continue;

                    string key = $"{icon.Tooltip}|{button.Current.AutomationId}|{button.Current.ClassName}";
                    if (seen.Add(key))
                        result.Add(icon);
                }
            }

            return result;
        }

        private static bool TryCreateTrayIcon(AutomationElement button, out TrayIconInfo icon)
        {
            icon = null!;

            string className = button.Current.ClassName ?? string.Empty;
            string automationId = button.Current.AutomationId ?? string.Empty;
            string name = NormalizeName(button.Current.Name);

            if (!IsTrayCandidate(className, automationId, name))
                return false;

            if (ShouldSkip(name))
                return false;

            icon = new TrayIconInfo
            {
                Index = -1,
                CommandId = 0,
                IsVisible = true,
                Tooltip = string.IsNullOrWhiteSpace(name) ? className : name,
                Icon = null,
            };
            return true;
        }

        private static bool IsTrayCandidate(string className, string automationId, string name)
        {
            if (className.StartsWith("SystemTray.", StringComparison.Ordinal))
                return true;

            if (string.Equals(automationId, "NotifyItemIcon", StringComparison.Ordinal) ||
                string.Equals(automationId, "SystemTrayIcon", StringComparison.Ordinal))
                return true;

            return name.Contains("tray", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("托盘", StringComparison.Ordinal);
        }

        private static bool ShouldSkip(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string[] prefixes =
            {
                "网络 ",
                "音量 ",
                "电源 ",
                "时钟 ",
                "显示桌面",
            };

            return prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
        }
    }
}
