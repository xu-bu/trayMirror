using System.Diagnostics;

namespace TrayMirror
{
    internal static class ProcessEnumerator
    {
        public static List<ProcessInfo> EnumerateProcesses(TimeSpan timeout)
        {
            var processes = new List<ProcessInfo>();
            var cts = new CancellationTokenSource(timeout);

            try
            {
                var task = Task.Run(() =>
                {
                    uint[] processIds = NativeMethods.EnumerateProcessIds();

                    foreach (uint pid in processIds)
                    {
                        if (cts.Token.IsCancellationRequested)
                            break;

                        if (pid == 0)
                            continue;

                        try
                        {
                            using var process = Process.GetProcessById((int)pid);
                            string? path = null;

                            try
                            {
                                path = process.MainModule?.FileName;
                            }
                            catch
                            {
                                path = NativeMethods.GetProcessImagePath(pid);
                            }

                            processes.Add(new ProcessInfo(
                                process.Id,
                                process.ProcessName,
                                Utils.NormalizeExecutablePath(path)));
                        }
                        catch
                        {
                            // Skip processes that disappear or deny access
                        }
                    }
                }, cts.Token);

                task.Wait(timeout);
            }
            catch
            {
                // Return whatever we collected on timeout or error
            }

            return processes;
        }

        public sealed record ProcessInfo(
            int Id,
            string ProcessName,
            string? Path)
        {
            public string? FileName =>
                string.IsNullOrWhiteSpace(Path) ? null : System.IO.Path.GetFileName(Path);
        }
    }
}
