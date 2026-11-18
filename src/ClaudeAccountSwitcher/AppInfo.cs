using System.Diagnostics;
using System.Reflection;

namespace ClaudeAccountSwitcher;

public static class AppInfo
{
    public static string Version
    {
        get
        {
            var ver = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = ver.IndexOf('+');
            return plus > 0 ? ver[..plus] : ver;
        }
    }
}

/// <summary>Finds running Claude processes: the desktop app and Claude Code CLI share the exe name "claude".</summary>
public static class ClaudeProcesses
{
    public static bool DesktopRunning() => Any(path => !IsCli(path) && IsDesktop(path));

    public static bool CliRunning() => Any(IsCli);

    static bool IsCli(string path) => path.Contains("claude-code", StringComparison.OrdinalIgnoreCase) ||
                                      path.Contains(@"\.local\bin", StringComparison.OrdinalIgnoreCase) ||
                                      path.Contains("npm", StringComparison.OrdinalIgnoreCase);

    static bool IsDesktop(string path) => path.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) ||
                                          path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                                          path.Contains(@"\Claude\", StringComparison.OrdinalIgnoreCase);

    static bool Any(Func<string, bool> match)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                try
                {
                    if (match(p.MainModule?.FileName ?? "")) return true;
                }
                catch { /* access denied */ }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }
}
