using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TrayMirror
{
    internal sealed class MirroredAppTracker : IDisposable
    {
        // if app exits, update its state then it will disappear from tray mirror
        public void UpdateIconStates(IReadOnlyList<MirroredIconDefinition> icons)
        {
            if (icons.Count == 0)
                return;

            var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var icon in icons)
            {
                foreach (var name in icon.CandidateProcessNames)
                    candidateNames.Add(name);
            }

            var processes = SnapshotRunningProcesses(candidateNames);
            foreach (var icon in icons)
                ApplyIconState(icon, processes);
        }

        private static void ApplyIconState(MirroredIconDefinition icon, IReadOnlyList<RunningProcessInfo> processes)
        {
            var match = FindAppProcess(icon, processes);
            if (match == null)
                return; // 进程已退出：由 SyncIcons（Overlay）负责从列表移除，这里只刷新运行态

            icon.ProcessId = match.Id;
            icon.IconSourcePath = Utils.NormalizeExecutablePath(match.Path) ?? icon.IconSourcePath;

            Icon? newIcon = TryExtractIcon(match.Path);
            if (newIcon != null)
            {
                icon.Icon?.Dispose();
                icon.Icon = newIcon;
            }
        }
        /// <summary>
        /// Toggle the mirrored app by showing, restoring, or minimizing its main window.
        /// Win11 no longer exposes tray icons through ToolbarWindow32, so direct window
        /// activation is the reliable approach.
        /// </summary>
        public void Activate(MirroredIconDefinition app)
        {
            Utils.TraceLog($"Activate start: {app.DisplayName}");
            Utils.TraceLog($"  candidates=[{string.Join(", ", app.CandidateProcessNames)}]");
            Utils.TraceLog($"  tooltipKeywords=[{string.Join(", ", app.TrayTooltipKeywords)}]");
            Utils.TraceLog($"  cachedProcessId={app.ProcessId}, iconSource={app.IconSourcePath ?? "(null)"}");

            if (TryActivateEverything(app))
                return;

            var processes = FindAppProcessesForActivation(app);
            if (processes.Count == 0)
            {
                Utils.TraceLog("Activate abort: no matching processes");
                return;
            }

            Utils.TraceLog($"  matchedProcesses=[{string.Join(", ", processes.Select(p => $"{p.ProcessName}({p.Id})"))}]");

            var candidates = CollectWindowCandidates(app, processes);
            foreach (var candidate in candidates.OrderByDescending(c => c.Score))
            {
                Utils.TraceLog(
                    $"  windowCandidate hwnd=0x{candidate.Hwnd.ToInt64():X} score={candidate.Score} " +
                    $"pid={candidate.ProcessId} area={candidate.Area} visible={candidate.IsVisible} iconic={candidate.IsIconic} " +
                    $"title=\"{candidate.Title}\" class=\"{candidate.ClassName}\"");
            }

            IntPtr hwnd = candidates
                .OrderByDescending(candidate => candidate.Score)
                .Select(candidate => candidate.Hwnd)
                .FirstOrDefault();
            if (hwnd == IntPtr.Zero)
            {
                Utils.TraceLog("Activate abort: no suitable window");
                return;
            }

            Utils.TraceLog($"Activate target hwnd=0x{hwnd.ToInt64():X}");

            if (TryActivateTauriApp(app, hwnd, processes))
                return;

            ToggleAppWindow(hwnd, app);
        }

        /// <summary>
        /// Tauri apps (e.g. Clash Verge) use window.hide() internally; Win32 ShowWindow
        /// does not restore them. Relaunching the exe triggers single-instance show logic.
        /// </summary>
        private static bool TryActivateTauriApp(
            MirroredIconDefinition app,
            IntPtr hwnd,
            List<RunningProcessInfo> processes)
        {
            string className = GetWindowClassName(hwnd);
            if (!className.Contains("Tauri", StringComparison.OrdinalIgnoreCase))
                return false;

            bool isVisible = NativeMethods.IsWindowVisible(hwnd);
            bool isIconic = NativeMethods.IsIconic(hwnd);

            if (isVisible && !isIconic)
            {
                Utils.TraceLog("Tauri: hiding visible window with SW_HIDE");
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                LogWindowState("after tauri hide", hwnd);
                return true;
            }

            // Tauri 窗口的显示逻辑在应用内部（window.hide() 后跨进程 ShowWindow 无效）。
            // 应用注册了托盘回调窗口（tray_icon_app），向其发送托盘点击消息
            // （tray-icon crate 的 WM_USER_TRAYICON=6002 + WM_LBUTTONDOWN=0x201）
            // 会走与真实托盘点击完全相同的链路触发 show_main_window。
            if (TryShowTauriAppViaTrayClick(app, hwnd, processes))
                return true;

            if (TryNotifyTauriSingletonShow(app))
                return true;

            string? exePath = ResolveExecutablePath(app, processes);
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                Utils.TraceLog($"Tauri: cannot relaunch, exe path missing ({exePath ?? "null"})");
                return false;
            }

            Utils.TraceLog($"Tauri: relaunching exe to trigger show_main_window: {exePath}");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                });
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"Tauri: relaunch failed: {ex.Message}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 向应用的托盘回调窗口（tray_icon_app，tray-icon crate 注册）发送点击消息，
        /// 触发与真实托盘左键点击相同的应用内显示逻辑（Clash Verge 匹配
        /// Click{ Left, Down } 后调用 show_main_window）。
        /// 消息号 6002 (WM_USER_TRAYICON) 和 lParam=WM_LBUTTONDOWN 是 tray-icon 的
        /// 窗口过程约定；该消息只走窗口消息，不依赖任务栏/UI 可见性。
        /// </summary>
        private static bool TryShowTauriAppViaTrayClick(
            MirroredIconDefinition app,
            IntPtr mainHwnd,
            List<RunningProcessInfo> processes)
        {
            const uint WM_USER_TRAYICON = 6002;      // tray-icon crate 的托盘回调消息
            const uint WM_LBUTTONDOWN = 0x0201;      // tray_proc 匹配的按下事件

            int processId = app.ProcessId > 0
                ? app.ProcessId
                : processes.Select(process => process.Id).FirstOrDefault();

            IntPtr trayWindow = FindWindowByClassName(processId, "tray_icon_app");
            if (trayWindow == IntPtr.Zero)
            {
                Utils.TraceLog("Tauri: tray_icon_app window not found");
                return false;
            }

            Utils.TraceLog(
                $"Tauri: tray-click SendMessage(0x{trayWindow.ToInt64():X}, 6002, 0x201) " +
                $"-> {app.DisplayName}");
            NativeMethods.SendMessage(trayWindow, WM_USER_TRAYICON, IntPtr.Zero, (IntPtr)WM_LBUTTONDOWN);

            // 事件投递与应用内 show 是异步的（tauri 事件循环 + AsyncHandler），轮询确认。
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(150);
                if (NativeMethods.IsWindowVisible(mainHwnd))
                {
                    Utils.TraceLog("Tauri: tray-click showed the window");
                    return true;
                }
            }

            Utils.TraceLog("Tauri: tray-click did not show the window in time");
            return false;
        }

        private static IntPtr FindWindowByClassName(int processId, string className)
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                uint ownerPid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out ownerPid);
                if (ownerPid == processId &&
                    string.Equals(GetWindowClassName(hwnd), className, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// Clash Verge Rev (and similar Tauri apps) expose a local HTTP singleton server.
        /// Second instances call GET /commands/visible with x-instance-token to show the window.
        /// </summary>
        private static bool TryNotifyTauriSingletonShow(MirroredIconDefinition app)
        {
            foreach (string recordPath in FindSingletonInstanceRecords(app))
            {
                if (TryNotifySingletonShow(recordPath))
                    return true;
            }

            return false;
        }

        private static IEnumerable<string> FindSingletonInstanceRecords(MirroredIconDefinition app)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<string>();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            string[] knownDirs =
            [
                Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev"),
                Path.Combine(appData, "clash-verge-rev"),
                Path.Combine(appData, "clash-verge"),
            ];

            foreach (string dir in knownDirs)
                TryAddSingletonRecord(seen, results, Path.Combine(dir, "singleton-instance.json"));

            AppendSingletonRecordsFromPattern(seen, results, appData, "*clash*");

            foreach (var token in GetProcessMatchTokens(app))
            {
                if (!token.Contains("clash", StringComparison.OrdinalIgnoreCase))
                    continue;

                AppendSingletonRecordsFromPattern(seen, results, appData, "*" + token + "*");
            }

            return results;
        }

        private static void TryAddSingletonRecord(HashSet<string> seen, List<string> results, string path)
        {
            if (seen.Add(path) && File.Exists(path))
                results.Add(path);
        }

        private static void AppendSingletonRecordsFromPattern(
            HashSet<string> seen,
            List<string> results,
            string appData,
            string pattern)
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(appData, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"Tauri singleton: appdata scan failed ({pattern}): {ex.Message}");
                return;
            }

            foreach (string dir in directories)
                TryAddSingletonRecord(seen, results, Path.Combine(dir, "singleton-instance.json"));
        }

        private static bool TryNotifySingletonShow(string recordPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(recordPath));
                if (!doc.RootElement.TryGetProperty("port", out JsonElement portElement) ||
                    !doc.RootElement.TryGetProperty("token", out JsonElement tokenElement))
                {
                    Utils.TraceLog($"Tauri singleton: missing port/token in {recordPath}");
                    return false;
                }

                int port = portElement.GetInt32();
                string token = tokenElement.GetString() ?? string.Empty;
                if (port <= 0 || string.IsNullOrEmpty(token))
                    return false;

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"http://127.0.0.1:{port}/commands/visible");
                request.Headers.TryAddWithoutValidation("x-instance-token", token);

                using HttpResponseMessage response = client.Send(request);
                Utils.TraceLog(
                    $"Tauri singleton: GET /commands/visible port={port} status={(int)response.StatusCode} ({recordPath})");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"Tauri singleton: notify failed ({recordPath}): {ex.Message}");
                return false;
            }
        }

        private static string? ResolveExecutablePath(
            MirroredIconDefinition app,
            IReadOnlyList<RunningProcessInfo> processes)
        {
            string? fromIcon = Utils.NormalizeExecutablePath(app.IconSourcePath);
            if (!string.IsNullOrWhiteSpace(fromIcon) && File.Exists(fromIcon))
                return fromIcon;

            RunningProcessInfo? match = processes.FirstOrDefault(process => process.Id == app.ProcessId)
                ?? processes.FirstOrDefault();

            string? fromSnapshot = Utils.NormalizeExecutablePath(match?.Path);
            if (!string.IsNullOrWhiteSpace(fromSnapshot) && File.Exists(fromSnapshot))
                return fromSnapshot;

            if (app.ProcessId > 0)
            {
                string? fromProcessId = Utils.NormalizeExecutablePath(
                    NativeMethods.GetProcessImagePath(app.ProcessId));
                if (!string.IsNullOrWhiteSpace(fromProcessId) && File.Exists(fromProcessId))
                    return fromProcessId;
            }

            if (match != null)
            {
                string? fromMatchProcessId = Utils.NormalizeExecutablePath(
                    NativeMethods.GetProcessImagePath(match.Id));
                if (!string.IsNullOrWhiteSpace(fromMatchProcessId) && File.Exists(fromMatchProcessId))
                    return fromMatchProcessId;
            }

            return null;
        }

        private static bool TryActivateEverything(MirroredIconDefinition app)
        {
            if (!LooksLikeEverything(app))
                return false;

            IntPtr trayWnd = NativeMethods.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
            if (trayWnd == IntPtr.Zero)
            {
                Utils.TraceLog("Everything: EVERYTHING_TASKBAR_NOTIFICATION not found");
                return false;
            }

            const int toggleSearchWindowCommand = 40008;
            IntPtr result = NativeMethods.SendMessage(
                trayWnd,
                NativeMethods.WM_COMMAND,
                (IntPtr)toggleSearchWindowCommand,
                IntPtr.Zero);

            Utils.TraceLog(
                $"Everything: SendMessage WM_COMMAND {toggleSearchWindowCommand} " +
                $"to hwnd=0x{trayWnd.ToInt64():X}, result=0x{result.ToInt64():X}");
            return true;
        }

        private static bool LooksLikeEverything(MirroredIconDefinition app)
        {
            return MatchesAnyToken(app, "everything");
        }

        private static bool MatchesAnyToken(MirroredIconDefinition app, string token)
        {
            return GetProcessMatchTokens(app).Any(value =>
                value.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> GetProcessMatchTokens(MirroredIconDefinition app)
        {
            return app.CandidateProcessNames
                .Concat(app.TrayTooltipKeywords)
                .Append(app.DisplayName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
        }

        private static void ToggleAppWindow(IntPtr hwnd, MirroredIconDefinition app)
        {
            LogWindowState("before toggle", hwnd);

            if (NativeMethods.IsHungAppWindow(hwnd))
            {
                Utils.TraceLog("Toggle abort: target window is hung");
                return;
            }

            bool isVisible = NativeMethods.IsWindowVisible(hwnd);
            bool isIconic = NativeMethods.IsIconic(hwnd);
            bool shouldHide = isVisible && !isIconic;

            Utils.TraceLog($"Toggle decision: shouldHide={shouldHide} (visible={isVisible}, iconic={isIconic})");

            if (shouldHide)
            {
                // WeChat and many tray apps hide with SW_HIDE, not SW_MINIMIZE.
                bool hidden = NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                Utils.TraceLog($"Toggle hide ShowWindow(SW_HIDE)={hidden}");
                LogWindowState("after hide", hwnd);
                return;
            }

            RestoreAndFocusWindow(hwnd);
            LogWindowState("after show", hwnd);
        }

        private static void RestoreAndFocusWindow(IntPtr hwnd)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNORMAL);

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOP,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);

            NativeMethods.SwitchToThisWindow(hwnd, fAltTab: true);

            if (NativeMethods.IsWindowVisible(hwnd))
                NativeMethods.SetForegroundWindow(hwnd);

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            Utils.TraceLog(
                $"Restore done visible={NativeMethods.IsWindowVisible(hwnd)}, foreground=0x{foreground.ToInt64():X}");
        }

        private static void LogWindowState(string stage, IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            Utils.TraceLog(
                $"{stage}: hwnd=0x{hwnd.ToInt64():X} pid={pid} visible={NativeMethods.IsWindowVisible(hwnd)} " +
                $"iconic={NativeMethods.IsIconic(hwnd)} title=\"{GetWindowText(hwnd)}\" " +
                $"class=\"{GetWindowClassName(hwnd)}\"");
        }

        private static RunningProcessInfo? FindAppProcess(
            MirroredIconDefinition app,
            IReadOnlyList<RunningProcessInfo> processes)
        {
            if (app.ProcessId > 0)
            {
                var cached = processes.FirstOrDefault(process => process.Id == app.ProcessId);
                if (cached != null && IsTargetProcess(cached, app))
                    return cached;
            }

            return processes
                .Where(process => IsTargetProcess(process, app))
                .OrderByDescending(process => process.StartTime)
                .FirstOrDefault();
        }

        private static IReadOnlyList<RunningProcessInfo> SnapshotRunningProcesses(HashSet<string> candidateNames)
        {
            var matches = new List<RunningProcessInfo>();

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string processName = process.ProcessName;
                        if (!candidateNames.Contains(processName))
                            continue;

                        matches.Add(new RunningProcessInfo(
                            process.Id,
                            processName,
                            TryGetExecutablePath(process),
                            GetSafeStartTime(process)));
                    }
                    catch
                    {
                        // Ignore processes that disappear or deny access during snapshot.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore snapshot failures and keep the last known icon state.
            }

            return matches;
        }

        private static List<RunningProcessInfo> FindAppProcessesForActivation(MirroredIconDefinition app)
        {
            var matchTokens = GetProcessMatchTokens(app).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = new List<RunningProcessInfo>();
            bool includeWeChatFamily = IsWeChatFamily(app);

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (!ProcessMatchesActivation(process, matchTokens, includeWeChatFamily))
                            continue;

                        matches.Add(new RunningProcessInfo(
                            process.Id,
                            process.ProcessName,
                            TryGetExecutablePath(process),
                            GetSafeStartTime(process)));
                    }
                    catch
                    {
                        // Ignore processes that disappear or deny access during snapshot.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Ignore snapshot failures.
            }

            if (app.ProcessId > 0 &&
                matches.All(process => process.Id != app.ProcessId))
            {
                try
                {
                    using var cachedProcess = Process.GetProcessById(app.ProcessId);
                    matches.Add(new RunningProcessInfo(
                        cachedProcess.Id,
                        cachedProcess.ProcessName,
                        TryGetExecutablePath(cachedProcess),
                        GetSafeStartTime(cachedProcess)));
                    Utils.TraceLog($"  added cached process pid={cachedProcess.Id} name={cachedProcess.ProcessName}");
                }
                catch
                {
                    // Cached process may have exited.
                }
            }

            return matches;
        }

        private static bool ProcessMatchesActivation(
            Process process,
            HashSet<string> matchTokens,
            bool includeWeChatFamily)
        {
            string processName = process.ProcessName;
            if (ShouldIncludeProcess(processName, matchTokens, includeWeChatFamily))
                return true;

            if (matchTokens.Any(token =>
                    processName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return true;

            string? path = TryGetExecutablePath(process);
            if (path == null)
                return false;

            string fileName = Path.GetFileNameWithoutExtension(path);
            return matchTokens.Contains(fileName);
        }

        private static bool IsWeChatFamily(MirroredIconDefinition app)
        {
            static bool LooksLikeWeChat(string value) =>
                value.Contains("weixin", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("wechat", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("微信", StringComparison.OrdinalIgnoreCase);

            return app.CandidateProcessNames.Any(LooksLikeWeChat) ||
                   app.TrayTooltipKeywords.Any(LooksLikeWeChat);
        }

        private static bool ShouldIncludeProcess(
            string processName,
            HashSet<string> candidateNames,
            bool includeWeChatFamily)
        {
            if (candidateNames.Contains(processName))
                return true;

            if (!includeWeChatFamily)
                return false;

            return processName.Contains("WeChat", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("Weixin", StringComparison.OrdinalIgnoreCase);
        }

        private static List<WindowActivationCandidate> CollectWindowCandidates(
            MirroredIconDefinition app,
            List<RunningProcessInfo> processes)
        {
            var processIds = processes.Select(process => process.Id).ToHashSet();
            if (app.ProcessId > 0)
                processIds.Add(app.ProcessId);

            var candidates = new List<WindowActivationCandidate>();

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                string title = GetWindowText(hwnd);
                string className = GetWindowClassName(hwnd);
                bool isVisible = NativeMethods.IsWindowVisible(hwnd);
                bool isIconic = NativeMethods.IsIconic(hwnd);
                bool isHung = NativeMethods.IsHungAppWindow(hwnd);
                int area = GetWindowArea(hwnd);

                bool ownedByProcess = processIds.Contains((int)ownerPid);
                bool titleMatches = app.TrayTooltipKeywords.Any(keyword =>
                    !string.IsNullOrWhiteSpace(keyword) &&
                    title.Contains(keyword, StringComparison.OrdinalIgnoreCase));

                if (!ownedByProcess && !titleMatches)
                    return true;

                if (IsIgnorableWindowClass(className, title))
                    return true;

                int score = ScoreWindowForActivation(
                    app, title, className, isVisible, isIconic, isHung, ownedByProcess, area);
                if (score > 0)
                {
                    candidates.Add(new WindowActivationCandidate(
                        hwnd, score, (int)ownerPid, title, className, isVisible, isIconic, area));
                }

                return true;
            }, IntPtr.Zero);

            return candidates;
        }

        private static int GetWindowArea(IntPtr hwnd)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
                return 0;

            return Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
        }

        private static bool IsIgnorableWindowClass(string className, string title)
        {
            if (string.Equals(className, "IME", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(className, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(className, "EVERYTHING_TASKBAR_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
                return true;

            return className.EndsWith("MessageWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("SystemMessageWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("PowerMessageWindow", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("TrayIconMessageWindow", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyMainWindowClass(string className)
        {
            return className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("Tauri", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("Qt", StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("EVERYTHING", StringComparison.OrdinalIgnoreCase);
        }

        private static int ScoreWindowForActivation(
            MirroredIconDefinition app,
            string title,
            string className,
            bool isVisible,
            bool isIconic,
            bool isHung,
            bool ownedByProcess,
            int area)
        {
            if (isHung)
                return 0;

            bool hasTitle = !string.IsNullOrWhiteSpace(title);
            bool likelyMainWindow = IsLikelyMainWindowClass(className);
            if (!hasTitle && !likelyMainWindow)
                return 0;

            if (hasTitle &&
                (title.Contains("登录", StringComparison.OrdinalIgnoreCase) ||
                 title.Contains("Login", StringComparison.OrdinalIgnoreCase)))
                return 0;

            int score = ownedByProcess ? 800 : 300;

            if (isVisible || isIconic)
                score += 400;
            else
                score += 150;

            if (hasTitle &&
                app.TrayTooltipKeywords.Any(keyword =>
                    !string.IsNullOrWhiteSpace(keyword) &&
                    title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                score += 700;

            foreach (var token in GetProcessMatchTokens(app))
            {
                if (hasTitle && title.Contains(token, StringComparison.OrdinalIgnoreCase))
                    score += 300;

                if (likelyMainWindow &&
                    token.Contains("clash", StringComparison.OrdinalIgnoreCase) &&
                    className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase))
                    score += 400;
            }

            if (likelyMainWindow)
                score += 250;

            if (area >= 200 * 200)
                score += 300;
            else if (area >= 100 * 100)
                score += 100;
            else if (area > 0)
                score -= 400;

            if (isIconic)
                score -= 50;

            if (!isVisible && !isIconic)
                score -= 50;

            return score;
        }

        private static bool IsTargetProcess(RunningProcessInfo process, MirroredIconDefinition app)
        {
            if (app.CandidateProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(process.Path))
                return false;

            string fileName = Path.GetFileNameWithoutExtension(process.Path);
            return app.CandidateProcessNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
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
            exePath = Utils.NormalizeExecutablePath(exePath);
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

        private readonly record struct WindowActivationCandidate(
            IntPtr Hwnd,
            int Score,
            int ProcessId,
            string Title,
            string ClassName,
            bool IsVisible,
            bool IsIconic,
            int Area);

        private sealed record RunningProcessInfo(
            int Id,
            string ProcessName,
            string? Path,
            DateTime StartTime);
    }

    public sealed record MirroredIconDefinition(
        string DisplayName,
        string[] CandidateProcessNames,
        string[] TrayTooltipKeywords)
    {
        public Icon? Icon { get; set; }
        public int ProcessId { get; set; }
        public string? IconSourcePath { get; set; }
        public Rectangle Bounds { get; set; }

    }
}
