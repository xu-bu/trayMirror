using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TrayMirror
{
    public static class Utils
    {
        private static readonly JsonSerializerOptions StringifyOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            ReferenceHandler = ReferenceHandler.IgnoreCycles
        };

        private static readonly Regex KnownFolderPathPrefix = new(
            @"^\{([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}(.*)$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string? NormalizeExecutablePath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            string path = Environment.ExpandEnvironmentVariables(rawPath.Trim());
            if (path.Length >= 2 && path[0] == '"' && path.IndexOf('"', 1) is int quoteIndex and > 1)
                path = path[1..quoteIndex];

            path = ResolveKnownFolderPath(path);

            const string exeSuffix = ".exe";
            int exeIndex = path.IndexOf(exeSuffix, StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
                path = path[..(exeIndex + exeSuffix.Length)];

            path = path.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }

        private static string ResolveKnownFolderPath(string path)
        {
            var match = KnownFolderPathPrefix.Match(path);
            if (!match.Success)
                return path;

            if (!Guid.TryParse(match.Groups[1].Value, out Guid folderId))
                return path;

            string? knownFolder = NativeMethods.GetKnownFolderPath(folderId);
            if (string.IsNullOrWhiteSpace(knownFolder))
                return path;

            string relativePath = match.Groups[2].Value.TrimStart('\\', '/');
            return string.IsNullOrWhiteSpace(relativePath)
                ? knownFolder
                : Path.Combine(knownFolder, relativePath);
        }

        public static string Stringify(object? value)
        {
            if (value is null)
                return "null";

            try
            {
                return JsonSerializer.Serialize(value, StringifyOptions);
            }
            catch (Exception ex)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"JsonSerializer failed: {ex.GetType().Name}: {ex.Message}");
                builder.AppendLine();
                AppendObject(builder, value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
                return builder.ToString();
            }
        }

        private static void AppendObject(StringBuilder builder, object? value, int depth, HashSet<object> visited)
        {
            const int maxDepth = 6;

            if (value is null)
            {
                builder.Append("null");
                return;
            }

            Type type = value.GetType();
            if (IsSimple(type))
            {
                builder.Append(value);
                return;
            }

            if (depth >= maxDepth)
            {
                builder.Append("...");
                return;
            }

            if (!type.IsValueType && !visited.Add(value))
            {
                builder.Append("<cycle>");
                return;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                AppendEnumerable(builder, enumerable, depth, visited);
                return;
            }

            builder.AppendLine(type.FullName);
            builder.Append(Indent(depth)).AppendLine("{");

            foreach (var member in GetReadableMembers(type))
            {
                builder.Append(Indent(depth + 1)).Append(member.Name).Append(": ");
                try
                {
                    AppendObject(builder, GetValue(member, value), depth + 1, visited);
                }
                catch (Exception ex)
                {
                    builder.Append($"<error: {ex.GetType().Name}: {ex.Message}>");
                }

                builder.AppendLine();
            }

            builder.Append(Indent(depth)).Append('}');
        }

        private static void AppendEnumerable(StringBuilder builder, IEnumerable enumerable, int depth, HashSet<object> visited)
        {
            builder.AppendLine("[");

            int count = 0;
            foreach (var item in enumerable)
            {
                if (count >= 100)
                {
                    builder.Append(Indent(depth + 1)).AppendLine("...");
                    break;
                }

                builder.Append(Indent(depth + 1));
                AppendObject(builder, item, depth + 1, visited);
                builder.AppendLine();
                count++;
            }

            builder.Append(Indent(depth)).Append(']');
        }

        private static IEnumerable<MemberInfo> GetReadableMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;

            foreach (var property in type.GetProperties(flags).Where(property => property.GetIndexParameters().Length == 0))
                yield return property;

            foreach (var field in type.GetFields(flags))
                yield return field;
        }

        private static object? GetValue(MemberInfo member, object instance) =>
            member switch
            {
                PropertyInfo property => property.GetValue(instance),
                FieldInfo field => field.GetValue(instance),
                _ => null
            };

        private static bool IsSimple(Type type) =>
            type.IsPrimitive ||
            type.IsEnum ||
            type == typeof(string) ||
            type == typeof(decimal) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) ||
            type == typeof(Guid) ||
            type == typeof(IntPtr) ||
            type == typeof(UIntPtr);

        private static string Indent(int depth) =>
            new(' ', depth * 2);

        private static bool? _lightTheme;

        /// <summary>Windows 应用/任务栏是否使用浅色主题。</summary>
        public static bool IsLightTheme()
        {
            if (_lightTheme.HasValue)
                return _lightTheme.Value;

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                _lightTheme = key?.GetValue("SystemUsesLightTheme") is int value ? value != 0 : true;
            }
            catch
            {
                _lightTheme = true;
            }

            return _lightTheme.Value;
        }

        public static void TraceLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Debug.WriteLine(line);

            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TrayMirror");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "traymirror.log"), line + Environment.NewLine);

                string fallbackLog = Path.Combine(AppContext.BaseDirectory, "traymirror.log");
                if (!string.Equals(fallbackLog, Path.Combine(logDir, "traymirror.log"), StringComparison.OrdinalIgnoreCase))
                    File.AppendAllText(fallbackLog, line + Environment.NewLine);
            }
            catch
            {
                // Best-effort logging only.
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object? x, object? y) =>
                ReferenceEquals(x, y);

            public int GetHashCode(object obj) =>
                RuntimeHelpers.GetHashCode(obj);
        }
    }
}
