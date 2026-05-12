using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayMirror
{
    internal static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TrayMirror";

        public static void EnsureRegistered()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                if (string.IsNullOrWhiteSpace(exePath))
                    return;

                string value = $"\"{exePath}\"";

                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (key == null)
                    return;

                string? existing = key.GetValue(ValueName) as string;
                if (!string.Equals(existing, value, StringComparison.Ordinal))
                    key.SetValue(ValueName, value);
            }
            catch
            {
            }
        }
    }
}
