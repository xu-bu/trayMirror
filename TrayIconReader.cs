using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TrayMirror
{
    /// <summary>
    /// Reads registered Win11 tray apps and falls back to toolbar/process discovery when needed.
    /// </summary>
    public class TrayIconReader : IDisposable
    {
        private bool _disposed;

        public MirroredIconDefinition[] ReadIcons()
        {
            var registeredIcons = ReadRegisteredRunningTrayApps();
            return registeredIcons.Length > 0
                ? registeredIcons
                : ReadTrayAppsAndExtractIcons();
        }

        private static MirroredIconDefinition[] ReadRegisteredRunningTrayApps()
        {
            try
            {
                var processIndex = ProcessIndex.Create();
                var registryEntries = NotifyIconRegistryReader.Read().ToList();

                var runningMatches = new List<RegistryProcessMatch>();
                foreach (var entry in registryEntries)
                {
                    var process = processIndex.Match(entry);
                    if (process is null)
                        continue;

                    if (ShouldSkipProcess(process.ProcessName))
                        continue;

                    if (ShouldSkipIcon(entry.DisplayName))
                        continue;

                    runningMatches.Add(new RegistryProcessMatch(entry, process));
                }

                var bestMatchesByKey = new Dictionary<string, RegistryProcessMatch>(StringComparer.OrdinalIgnoreCase);
                foreach (var match in runningMatches)
                {
                    var key = BuildRegistryMatchKey(match.Entry);
                    if (!bestMatchesByKey.TryGetValue(key, out var existing)
                        || IsPreferredRegistryMatch(match, existing))
                    {
                        bestMatchesByKey[key] = match;
                    }
                }

                var orderedMatches = bestMatchesByKey.Values
                    .OrderBy(match => match.Entry.IsPromoted == 1 ? 0 : 1)
                    .ThenBy(match => match.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var icons = new List<MirroredIconDefinition>();
                foreach (var match in orderedMatches)
                {
                    var icon = CreateMirroredIcon(match.Entry, match.Process);
                    if (icon.Icon is not null)
                        icons.Add(icon);
                }

                return icons.ToArray();
            }
            catch
            {
                return Array.Empty<MirroredIconDefinition>();
            }
        }

        private sealed record RegistryProcessMatch(NotifyIconEntry Entry, ProcessInfo Process);

        private static string BuildRegistryMatchKey(NotifyIconEntry entry) =>
            $"{entry.DisplayName}\u001F{entry.ExecutablePath ?? string.Empty}";

        private static bool IsPreferredRegistryMatch(RegistryProcessMatch candidate, RegistryProcessMatch current)
        {
            var candidateScore = CompletenessScore(candidate.Entry);
            var currentScore = CompletenessScore(current.Entry);
            if (candidateScore != currentScore)
                return candidateScore > currentScore;

            return string.Compare(
                candidate.Entry.RegistryKeyName,
                current.Entry.RegistryKeyName,
                StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static MirroredIconDefinition CreateMirroredIcon(NotifyIconEntry entry, ProcessInfo process)
        {
            string? exePath = PickExecutablePath(entry.ExecutablePath, process.Path);
            string processNameFromPath = Path.GetFileNameWithoutExtension(exePath) ?? string.Empty;

            var candidateProcessNames = new[]
                {
                    process.ProcessName,
                    processNameFromPath
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var tooltipKeywords = new[]
                {
                    entry.InitialTooltip,
                    entry.DisplayName,
                    process.ProcessName,
                    processNameFromPath
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MirroredIconDefinition(
                entry.DisplayName,
                candidateProcessNames,
                tooltipKeywords)
            {
                Icon = TryExtractIcon(exePath),
                ProcessId = process.Id,
                IconSourcePath = exePath
            };
        }

        private static string? PickExecutablePath(string? registryPath, string? processPath)
        {
            string? normalizedRegistry = Utils.NormalizeExecutablePath(registryPath);
            string? normalizedProcess = Utils.NormalizeExecutablePath(processPath);

            if (!string.IsNullOrWhiteSpace(normalizedRegistry) && File.Exists(normalizedRegistry))
                return normalizedRegistry;

            if (!string.IsNullOrWhiteSpace(normalizedProcess) && File.Exists(normalizedProcess))
                return normalizedProcess;

            return FirstNonEmpty(normalizedRegistry, normalizedProcess);
        }

        private static int CompletenessScore(NotifyIconEntry entry)
        {
            var score = 0;
            if (!string.IsNullOrWhiteSpace(entry.InitialTooltip)) score++;
            if (!string.IsNullOrWhiteSpace(entry.Publisher)) score++;
            if (entry.IsPromoted is not null) score++;
            if (entry.Uid is not null) score++;
            if (!string.IsNullOrWhiteSpace(entry.IconGuid)) score++;
            return score;
        }

        private static MirroredIconDefinition[] ReadTrayAppsAndExtractIcons()
        {
            var icons = new List<MirroredIconDefinition>();
            var appNames = new List<string>();

            try
            {
                IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (trayWnd == IntPtr.Zero) return DiscoverRunningAppsFallback();

                IntPtr toolbar = FindMainTray(trayWnd);
                if (toolbar == IntPtr.Zero) return DiscoverRunningAppsFallback();

                NativeMethods.GetWindowThreadProcessId(toolbar, out uint pid);
                IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_TRAY_READ, false, pid);
                if (hProcess == IntPtr.Zero) return DiscoverRunningAppsFallback();

                try
                {
                    int btnCount = (int)NativeMethods.SendMessage(toolbar,
                        NativeMethods.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
                    int btnSize = Marshal.SizeOf<NativeMethods.TBBUTTON>();

                    IntPtr remoteBtn = NativeMethods.VirtualAllocEx(hProcess, IntPtr.Zero,
                        (uint)btnSize, NativeMethods.MEM_COMMIT, NativeMethods.PAGE_READWRITE);
                    if (remoteBtn == IntPtr.Zero) return DiscoverRunningAppsFallback();

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

                            // Skip system tray icons
                            if (ShouldSkipIcon(tooltip)) continue;

                            appNames.Add(tooltip);
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

                // Now extract icons from the executables of these apps
                foreach (string appName in appNames)
                {
                    Icon? icon = TryExtractIconFromApp(appName);
                    if (icon == null) continue;

                    var app = new MirroredIconDefinition(
                        appName,
                        Array.Empty<string>(),
                        new[] { appName })
                    {
                        Icon = icon
                    };

                    icons.Add(app);
                }

                // If toolbar method returned no icons, fallback to process discovery
                if (icons.Count == 0)
                    return DiscoverRunningAppsFallback();
            }
            catch
            {
                // Fallback to process discovery on error
                return DiscoverRunningAppsFallback();
            }

            return icons.ToArray();
        }

        private static MirroredIconDefinition[] DiscoverRunningAppsFallback()
        {
            var apps = new List<MirroredIconDefinition>();
            var seenProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string? processName = process.ProcessName;
                        if (string.IsNullOrWhiteSpace(processName))
                            continue;

                        // Skip system processes and background services
                        if (ShouldSkipProcess(processName))
                            continue;

                        // Avoid duplicates
                        if (!seenProcessNames.Add(processName))
                            continue;

                        string? exePath = TryGetExecutablePath(process);
                        if (string.IsNullOrWhiteSpace(exePath))
                            continue;

                        string displayName = GetDisplayName(processName, exePath);
                        Icon? icon = TryExtractIcon(exePath);
                        if (icon == null) continue;

                        var app = new MirroredIconDefinition(
                            displayName,
                            new[] { processName },
                            new[] { displayName, processName })
                        {
                            Icon = icon,
                            ProcessId = process.Id,
                            IconSourcePath = exePath
                        };

                        apps.Add(app);
                    }
                    catch
                    {
                        // Skip processes that can't be accessed
                    }
                }
            }
            catch
            {
                // Return empty if process enumeration fails
            }

            return apps.ToArray();
        }

        private static bool ShouldSkipProcess(string processName)
        {
            string[] skipList =
            {
                "system", "smss", "csrss", "wininit", "winlogon", "services",
                "lsass", "svchost", "explorer", "dwm", "conhost", "rundll32",
                "dllhost", "taskhost", "wmiprvse", "unsecapp", "wmiapsrv",
                "audiodg", "rdrsvc", "wbemcom", "wmiadap", "wmiaprpl",
                "searchindexer", "msmpeng", "nissrv", "sppsvc", "wlidsvc",
                "dashost", "fontdrvhost", "ucrt64d", "vctools", "msedgewebview2",
                "backgroundtaskhost", "runtimebroker", "shellexperiencehost",
                "systemsettings", "windowsinternal", "microsoftedgecp"
            };

            return skipList.Any(name => processName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                   processName.StartsWith("msmpeng", StringComparison.OrdinalIgnoreCase) ||
                   processName.StartsWith("msdtc", StringComparison.OrdinalIgnoreCase);
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

        private static string GetDisplayName(string processName, string? exePath)
        {
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                try
                {
                    var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(fileVersion.FileDescription))
                        return fileVersion.FileDescription;
                }
                catch
                {
                    // Fall back to process name
                }
            }

            // Capitalize first letter
            if (processName.Length > 0)
                return char.ToUpper(processName[0]) + processName.Substring(1);

            return processName;
        }

        private static IntPtr FindMainTray(IntPtr trayWnd)
        {
            var tb = FindFirstDescendantByClass(trayWnd, "ToolbarWindow32");
            if (tb != IntPtr.Zero) return tb;

            IntPtr overflow = NativeMethods.FindWindow("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero)
            {
                tb = FindFirstDescendantByClass(overflow, "ToolbarWindow32");
                if (tb != IntPtr.Zero) return tb;
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindFirstDescendantByClass(IntPtr root, string wantedClass)
        {
            if (root == IntPtr.Zero) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            var sb = new System.Text.StringBuilder(256);

            bool Callback(IntPtr hwnd, IntPtr _)
            {
                if (found != IntPtr.Zero) return false;
                sb.Clear();
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                if (string.Equals(sb.ToString(), wantedClass, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }

                NativeMethods.EnumChildWindows(hwnd, Callback, IntPtr.Zero);
                return found == IntPtr.Zero;
            }

            NativeMethods.EnumChildWindows(root, Callback, IntPtr.Zero);
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

        private static Icon? TryExtractIconFromApp(string appName)
        {
            try
            {
                // Try to find a process with a matching name
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        string? exePath = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(exePath)) continue;

                        string fileName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                        string processName = process.ProcessName;

                        // Check if process name matches the app name
                        if (processName.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                        {
                            return TryExtractIcon(exePath);
                        }
                    }
                    catch
                    {
                        // Skip processes we can't access
                    }
                }
            }
            catch
            {
                // Return null on error
            }

            return null;
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

        private static bool ShouldSkipIcon(string tooltip)
        {
            string[] skipPrefixes =
            {
                "网络", "音量", "电源", "时钟", "显示桌面",
                "Network", "Volume", "Power", "Clock", "Show desktop"
            };

            return skipPrefixes.Any(prefix => tooltip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject())!; }
            finally { handle.Free(); }
        }

        private static string? FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private sealed record NotifyIconEntry(
            string RegistryKeyName,
            string? ExecutablePath,
            string? InitialTooltip,
            string? Publisher,
            int? IsPromoted,
            int? Uid,
            string? IconGuid)
        {
            public string DisplayName =>
                FirstNonEmpty(InitialTooltip, Path.GetFileNameWithoutExtension(ExecutablePath), Publisher, RegistryKeyName)
                ?? "(unknown)";

            public string? ExecutableFileName =>
                string.IsNullOrWhiteSpace(ExecutablePath) ? null : Path.GetFileName(ExecutablePath);
        }

        private static class NotifyIconRegistryReader
        {
            private const string NotifyIconSettingsPath = @"Control Panel\NotifyIconSettings";

            public static IEnumerable<NotifyIconEntry> Read()
            {
                using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath, writable: false);
                if (root is null)
                    yield break;

                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var subKey = root.OpenSubKey(subKeyName, writable: false);
                    if (subKey is null)
                        continue;

                    yield return new NotifyIconEntry(
                        subKeyName,
                        Utils.NormalizeExecutablePath(ReadString(subKey, "ExecutablePath")),
                        ReadString(subKey, "InitialTooltip"),
                        ReadString(subKey, "Publisher"),
                        ReadInt32(subKey, "IsPromoted"),
                        ReadInt32(subKey, "UID"),
                        ReadString(subKey, "IconGuid"));
                }
            }

            private static string? ReadString(RegistryKey key, string valueName) =>
                key.GetValue(valueName) as string;

            private static int? ReadInt32(RegistryKey key, string valueName)
            {
                var value = key.GetValue(valueName);
                return value switch
                {
                    int intValue => intValue,
                    long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
                    _ => null
                };
            }
        }

        private sealed class ProcessIndex
        {
            private readonly Dictionary<string, ProcessInfo> _byFullPath;
            private readonly Dictionary<string, List<ProcessInfo>> _byFileName;
            private readonly Dictionary<string, List<ProcessInfo>> _byProcessName;

            private ProcessIndex(IEnumerable<ProcessInfo> processes)
            {
                var processList = processes.ToList();

                _byFullPath = processList
                    .Where(process => !string.IsNullOrWhiteSpace(process.Path))
                    .GroupBy(process => process.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                _byFileName = processList
                    .Where(process => !string.IsNullOrWhiteSpace(process.FileName))
                    .GroupBy(process => process.FileName!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

                _byProcessName = processList
                    .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            }

            public static ProcessIndex Create() =>
                new(Process.GetProcesses().Select(ProcessInfo.FromProcess));

            public ProcessInfo? Match(NotifyIconEntry entry)
            {
                if (!string.IsNullOrWhiteSpace(entry.ExecutablePath) &&
                    _byFullPath.TryGetValue(entry.ExecutablePath, out var exactPathMatch))
                {
                    return exactPathMatch;
                }

                if (!string.IsNullOrWhiteSpace(entry.ExecutableFileName) &&
                    _byFileName.TryGetValue(entry.ExecutableFileName, out var fileNameMatches))
                {
                    return fileNameMatches.OrderBy(match => match.Id).First();
                }

                string? processName = Path.GetFileNameWithoutExtension(entry.ExecutablePath);
                if (!string.IsNullOrWhiteSpace(processName) &&
                    _byProcessName.TryGetValue(processName, out var processNameMatches))
                {
                    return processNameMatches.OrderBy(match => match.Id).First();
                }

                return null;
            }
        }

        private sealed record ProcessInfo(
            int Id,
            string ProcessName,
            string? Path)
        {
            public string? FileName =>
                string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetFileName(Path);

            public static ProcessInfo FromProcess(Process process)
            {
                try
                {
                    return new ProcessInfo(
                        process.Id,
                        process.ProcessName,
                        Utils.NormalizeExecutablePath(process.MainModule?.FileName));
                }
                catch
                {
                    return new ProcessInfo(process.Id, process.ProcessName, null);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
