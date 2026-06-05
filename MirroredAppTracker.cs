using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace TrayMirror
{
    internal sealed class MirroredAppTracker : IDisposable
    {
        public static readonly MirroredAppDefinition[] DefaultApps =
        {
            new("WeChat", new[] { "Weixin", "WeChat", "wechat" }, new[] { "WeChat", "微信" }),
            new("Everything", new[] { "Everything", "Everything64", "Everything32" }, new[] { "Everything" }),
            new("v2rayN", new[] { "v2rayN", "v2rayN.Desktop" }, new[] { "v2rayN", "v2ray" }),
        };

        public MirroredAppState GetState(MirroredAppDefinition app, Icon? preferredIcon = null)
        {
            var process = FindAppProcess(app);
            if (process == null)
            {
                preferredIcon?.Dispose();
                return MirroredAppState.NotRunning(app);
            }

            string? exePath = TryGetExecutablePath(process);
            bool hasPreferredIcon = preferredIcon != null;
            return new MirroredAppState(
                app,
                true,
                process.Id,
                hasPreferredIcon ? $"tray:{app.DisplayName}" : exePath,
                preferredIcon ?? TryExtractIcon(exePath));
        }

        public void Activate(MirroredAppDefinition app)
        {
            // 优先：点击主屏托盘图标，让应用自己的托盘逻辑处理（如 WeChat 的 toggle）
            if (TryClickTrayIcon(app))
                return;

            // 回退：直接查找窗口并激活
            var processes = FindAppProcesses(app).ToList();
            if (processes.Count == 0)
                return;

            IntPtr hwnd = FindBestAppWindow(app, processes);
            if (hwnd == IntPtr.Zero)
                return;

            bool isVisible = NativeMethods.IsWindowVisible(hwnd);
            bool isIconic = NativeMethods.IsIconic(hwnd);
            bool isHung = NativeMethods.IsHungAppWindow(hwnd);
            if (isHung)
                return;

            if (isIconic || !isVisible)
            {
                int showCommand = isIconic ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOW;
                NativeMethods.ShowWindowAsync(hwnd, showCommand);
            }

            ForceForegroundWindow(hwnd);
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// 在主屏系统托盘里找到对应应用的图标按钮，发送鼠标点击消息。
        /// 这样行为等同于用户直接点击系统托盘图标。
        /// </summary>
        private static bool TryClickTrayIcon(MirroredAppDefinition app)
        {
            try
            {
                IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (trayWnd == IntPtr.Zero) return false;

                IntPtr toolbar = FindTrayToolbar(trayWnd);
                if (toolbar == IntPtr.Zero) return false;

                NativeMethods.GetWindowThreadProcessId(toolbar, out uint pid);
                IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_TRAY_READ, false, pid);
                if (hProcess == IntPtr.Zero) return false;

                try
                {
                    int btnCount = (int)NativeMethods.SendMessage(toolbar,
                        NativeMethods.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                    int btnSize = Marshal.SizeOf<NativeMethods.TBBUTTON>();

                    IntPtr remoteBtn = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero,
                        (uint)btnSize, NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
                    if (remoteBtn == IntPtr.Zero) return false;

                    try
                    {
                        for (int i = 0; i < btnCount; i++)
                        {
                            NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETBUTTON,
                                new IntPtr(i), remoteBtn);

                            var buf = new byte[btnSize];
                            NativeMethods.ReadProcessMemory(hProcess, remoteBtn, buf, (uint)btnSize, out _);
                            var btn = BytesToStruct<NativeMethods.TBBUTTON>(buf);

                            string tooltip = ReadRemoteTooltip(toolbar, hProcess, btn.idCommand);
                            if (string.IsNullOrEmpty(tooltip)) continue;

                            if (!app.TrayTooltipKeywords.Any(k =>
                                    tooltip.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            // 找到对应按钮，发送点击
                            return ClickTrayButton(toolbar, hProcess, btn.idCommand);
                        }
                    }
                    finally
                    {
                        NativeMethods.VirtualFreeEx(hProcess, remoteBtn, 0, NativeMethods.MEM_RELEASE);
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(hProcess);
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static IntPtr FindTrayToolbar(IntPtr trayWnd)
        {
            IntPtr found = IntPtr.Zero;
            var sb = new System.Text.StringBuilder(256);

            bool Callback(IntPtr hwnd, IntPtr _)
            {
                if (found != IntPtr.Zero) return false;
                sb.Clear();
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                if (string.Equals(sb.ToString(), "ToolbarWindow32", StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }

                NativeMethods.EnumChildWindows(hwnd, Callback, IntPtr.Zero);
                return found == IntPtr.Zero;
            }

            NativeMethods.EnumChildWindows(trayWnd, Callback, IntPtr.Zero);
            return found;
        }

        private static string ReadRemoteTooltip(IntPtr toolbar, IntPtr hProcess, int commandId)
        {
            const int bufferBytes = 512;
            IntPtr remoteText = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero,
                bufferBytes, NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
            if (remoteText == IntPtr.Zero) return string.Empty;

            try
            {
                var len = NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETBUTTONTEXTW,
                    new IntPtr(commandId), remoteText);
                if (len.ToInt64() < 0) return string.Empty;

                var buffer = new byte[bufferBytes];
                if (!NativeMethods.ReadProcessMemory(hProcess, remoteText, buffer, bufferBytes, out _))
                    return string.Empty;

                string text = System.Text.Encoding.Unicode.GetString(buffer);
                int terminator = text.IndexOf('\0');
                return terminator >= 0 ? text[..terminator].Trim() : text.Trim();
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                NativeMethods.VirtualFreeEx(hProcess, remoteText, 0, NativeMethods.MEM_RELEASE);
            }
        }

        private static bool ClickTrayButton(IntPtr toolbar, IntPtr hProcess, int commandId)
        {
            const int rectSize = 16;
            IntPtr remoteRect = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero,
                rectSize, NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
            if (remoteRect == IntPtr.Zero) return false;

            try
            {
                IntPtr result = NativeMethods.SendMessage(toolbar, NativeMethods.TB_GETRECT,
                    new IntPtr(commandId), remoteRect);
                if (result == IntPtr.Zero) return false;

                var rectBuf = new byte[rectSize];
                NativeMethods.ReadProcessMemory(hProcess, remoteRect, rectBuf, rectSize, out _);

                int left = BitConverter.ToInt32(rectBuf, 0);
                int top = BitConverter.ToInt32(rectBuf, 4);
                int right = BitConverter.ToInt32(rectBuf, 8);
                int bottom = BitConverter.ToInt32(rectBuf, 12);

                int x = (left + right) / 2;
                int y = (top + bottom) / 2;

                // 转换为屏幕坐标，用 SendInput 模拟真实的鼠标点击
                var pt = new NativeMethods.POINT { X = x, Y = y };
                if (!NativeMethods.ClientToScreen(toolbar, out pt)) return false;

                // 保存当前光标位置
                NativeMethods.GetCursorPos(out var originalPos);

                // 原子操作：移动→按下→松开→移回
                NativeMethods.ClickAt(pt.X, pt.Y, originalPos.X, originalPos.Y);

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                NativeMethods.VirtualFreeEx(hProcess, remoteRect, 0, NativeMethods.MEM_RELEASE);
            }
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject())!; }
            finally { handle.Free(); }
        }

        private static Process? FindAppProcess(MirroredAppDefinition app)
        {
            return FindAppProcesses(app)
                .OrderByDescending(GetSafeStartTime)
                .FirstOrDefault();
        }

        private static IEnumerable<Process> FindAppProcesses(MirroredAppDefinition app)
        {
            return Process
                .GetProcesses()
                .Where(process => IsTargetProcess(process, app));
        }

        private static IntPtr FindBestAppWindow(MirroredAppDefinition app, List<Process> processes)
        {
            var processIds = processes.Select(process => process.Id).ToHashSet();
            var candidates = new List<WindowActivationCandidate>();

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (!processIds.Contains((int)ownerPid))
                    return true;

                string title = GetWindowText(hwnd);
                string className = GetWindowClassName(hwnd);
                bool isVisible = NativeMethods.IsWindowVisible(hwnd);
                bool isIconic = NativeMethods.IsIconic(hwnd);
                bool isHung = NativeMethods.IsHungAppWindow(hwnd);
                int score = ScoreWindowForActivation(app, title, className, isVisible, isIconic, isHung);

                if (score > 0)
                    candidates.Add(new WindowActivationCandidate(hwnd, score));

                return true;
            }, IntPtr.Zero);

            return candidates
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Hwnd)
                .FirstOrDefault();
        }

        private static int ScoreWindowForActivation(
            MirroredAppDefinition app,
            string title,
            string className,
            bool isVisible,
            bool isIconic,
            bool isHung)
        {
            if (isHung)
                return 0;

            if (string.IsNullOrWhiteSpace(title))
                return 0;

            if (title.Contains("登录", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Login", StringComparison.OrdinalIgnoreCase))
                return 0;

            int score = isVisible || isIconic ? 1000 : 250;

            if (app.TrayTooltipKeywords.Any(keyword => title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                score += 500;

            if (className.Contains("Qt", StringComparison.OrdinalIgnoreCase))
                score += 100;

            if (isIconic)
                score -= 50;

            if (!isVisible && !isIconic)
                score -= 100;

            return score;
        }

        private static bool IsTargetProcess(Process process, MirroredAppDefinition app)
        {
            try
            {
                if (app.CandidateProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    return true;

                string? path = TryGetExecutablePath(process);
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                string fileName = Path.GetFileNameWithoutExtension(path);
                return app.CandidateProcessNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static DateTime GetSafeStartTime(Process process)
        {
            try
            {
                return process.StartTime;
            }
            catch
            {
                return DateTime.MinValue;
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

        private static string GetWindowText(IntPtr hwnd)
        {
            var title = new System.Text.StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            return title.ToString();
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            return className.ToString();
        }

        private static void ForceForegroundWindow(IntPtr hwnd)
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();

            if (foreground == hwnd)
                return;

            uint currentThreadId = NativeMethods.GetCurrentThreadId();
            uint foregroundThreadId = foreground != IntPtr.Zero
                ? NativeMethods.GetWindowThreadProcessId(foreground, out _)
                : currentThreadId;

            bool attached = false;
            if (currentThreadId != foregroundThreadId)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
                attached = true;
            }

            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                    NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }

        private readonly record struct WindowActivationCandidate(IntPtr Hwnd, int Score);
    }

    internal sealed record MirroredAppDefinition(
        string DisplayName,
        string[] CandidateProcessNames,
        string[] TrayTooltipKeywords);

    internal sealed record MirroredAppState(
        MirroredAppDefinition App,
        bool IsRunning,
        int ProcessId,
        string? IconSourcePath,
        Icon? Icon)
    {
        public static MirroredAppState NotRunning(MirroredAppDefinition app) => new(app, false, 0, null, null);
    }
}
