using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace TrayMirror
{
    internal static class StartupRegistration
    {
        private const string TaskName = "TrayMirror";
        private const int StartupDelaySeconds = 60;

        /// <summary>是否已设置为开机启动（计划任务是否存在）。</summary>
        public static bool IsEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/query /tn \"{TaskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"StartupRegistration: query error: {ex.Message}");
                return false;
            }
        }

        /// <summary>打开/关闭开机启动，返回是否操作成功。</summary>
        public static bool SetEnabled(bool enabled)
        {
            return enabled ? CreateTask() : DeleteTask();
        }

        private static bool CreateTask()
        {
            string exePath = Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exePath))
                return false;

            // /delay 格式为 mmmm:ss（分:秒）
            string delayStr = $"0000:{StartupDelaySeconds:D2}";
            string baseArgs = $"/create /tn \"{TaskName}\" /tr \"{exePath}\" /sc onlogon /delay {delayStr} /f";

            // 优先以最高权限创建；非管理员运行时该参数会被拒绝，回退为当前用户权限。
            if (RunSchtasks(baseArgs + " /rl highest", out int highestExitCode))
            {
                Utils.TraceLog("StartupRegistration: task created (highest)");
                return true;
            }

            if (RunSchtasks(baseArgs, out int limitedExitCode))
            {
                Utils.TraceLog("StartupRegistration: task created (limited)");
                return true;
            }

            Utils.TraceLog($"StartupRegistration: task creation failed (highest={highestExitCode}, limited={limitedExitCode})");
            return false;
        }

        private static bool DeleteTask()
        {
            if (RunSchtasks($"/delete /tn \"{TaskName}\" /f", out _))
            {
                Utils.TraceLog("StartupRegistration: task deleted");
                return true;
            }

            // 任务本来就不存在，也视为成功。
            return !IsEnabled();
        }

        private static bool RunSchtasks(string args, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                // 先读输出再等待，避免输出缓冲满导致死锁。
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                exitCode = process.ExitCode;

                if (exitCode != 0)
                    Utils.TraceLog($"StartupRegistration: schtasks {args} failed with exit code {exitCode}: {stdout}{stderr}");

                return exitCode == 0;
            }
            catch (Exception ex)
            {
                Utils.TraceLog($"StartupRegistration: schtasks error: {ex.Message}");
                return false;
            }
        }
    }
}
