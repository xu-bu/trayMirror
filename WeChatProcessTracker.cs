using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;

namespace TrayMirror
{
    internal sealed class WeChatProcessTracker : IDisposable
    {
        private static readonly string[] CandidateProcessNames = { "Weixin", "WeChat", "wechat" };

        public string? LastIconSourcePath { get; set; }

        public WeChatState GetState()
        {
            var process = FindWeChatProcess();
            if (process == null)
                return WeChatState.NotRunning;

            string? exePath = TryGetExecutablePath(process);
            return new WeChatState(
                true,
                process.Id,
                exePath,
                TryExtractIcon(exePath));
        }

        public void ActivateWeChat()
        {
            var process = FindWeChatProcess();
            if (process == null)
                return;

            string? exePath = TryGetExecutablePath(process);
            IntPtr hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                hwnd = FindTopLevelWindow(process.Id, visibleOnly: true);
            if (hwnd == IntPtr.Zero)
                hwnd = FindTopLevelWindow(process.Id, visibleOnly: false);

            if (hwnd != IntPtr.Zero)
            {
                if (NativeMethods.IsIconic(hwnd))
                    NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                else
                    NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_SHOW);

                if (NativeMethods.SetForegroundWindow(hwnd))
                    return;
            }

            if (string.IsNullOrWhiteSpace(exePath))
                return;

            TryLaunchWeChat(exePath);
        }

        public void Dispose()
        {
        }

        private static Process? FindWeChatProcess()
        {
            return Process
                .GetProcesses()
                .Where(IsWeChatProcess)
                .OrderByDescending(p => p.StartTime)
                .FirstOrDefault();
        }

        private static bool IsWeChatProcess(Process process)
        {
            try
            {
                if (CandidateProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    return true;

                string? path = TryGetExecutablePath(process);
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                string fileName = Path.GetFileNameWithoutExtension(path);
                return CandidateProcessNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string? TryGetExecutablePath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static Icon? TryExtractIcon(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return null;

            try
            {
                using var extracted = Icon.ExtractAssociatedIcon(exePath);
                return extracted == null ? null : (Icon)extracted.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static IntPtr FindTopLevelWindow(int processId, bool visibleOnly)
        {
            IntPtr found = IntPtr.Zero;

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (ownerPid != processId)
                    return true;

                if (visibleOnly && !NativeMethods.IsWindowVisible(hwnd))
                    return true;

                found = hwnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        private static void TryLaunchWeChat(string exePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                });
            }
            catch
            {
            }
        }
    }

    internal sealed record WeChatState(bool IsRunning, int ProcessId, string? IconSourcePath, Icon? Icon)
    {
        public static WeChatState NotRunning { get; } = new(false, 0, null, null);
    }
}
