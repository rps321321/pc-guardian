using System.Diagnostics;

namespace PCGuardian;

internal static class QuarantineManager
{
    private const string RulePrefix = "PCGuardian_Quarantine_";
    private const int NetshTimeoutMs = 10_000;

    private static readonly (string Name, string Protocol, string Ports)[] BlockedPorts =
    [
        ("RDP",      "TCP", "3389"),
        ("VNC",      "TCP", "5900-5902"),
        ("VNC_Web",  "TCP", "5800"),
        ("SSH",      "TCP", "22"),
        ("Telnet",   "TCP", "23"),
    ];

    private static readonly (string Name, string ExeName)[] RemoteAccessApps =
    [
        ("TeamViewer", "TeamViewer.exe"),
        ("AnyDesk",    "AnyDesk.exe"),
        ("Parsec",     "parsecd.exe"),
        ("RustDesk",   "rustdesk.exe"),
        ("Splashtop",  "SRService.exe"),
    ];

    private static readonly string[] ProcessesToKill =
    [
        "TeamViewer", "AnyDesk", "parsecd", "rustdesk",
        "SRService", "SRAgent", "SRFeature",
        "tvnserver", "winvnc", "vncserver",
    ];

    public static bool EnableQuarantine()
    {
        try
        {
            // Block ports (inbound + outbound for each)
            foreach (var (name, protocol, ports) in BlockedPorts)
            {
                string ruleName = $"{RulePrefix}{name}";
                RunNetsh($"advfirewall firewall add rule name=\"{ruleName}_In\" dir=in action=block protocol={protocol} localport={ports}");
                RunNetsh($"advfirewall firewall add rule name=\"{ruleName}_Out\" dir=out action=block protocol={protocol} localport={ports}");
            }

            // Block known remote access apps by program path
            foreach (var (name, exeName) in RemoteAccessApps)
            {
                string? exePath = FindExecutablePath(exeName);
                if (exePath is null) continue;

                string ruleName = $"{RulePrefix}App_{name}";
                RunNetsh($"advfirewall firewall add rule name=\"{ruleName}_In\" dir=in action=block program=\"{exePath}\"");
                RunNetsh($"advfirewall firewall add rule name=\"{ruleName}_Out\" dir=out action=block program=\"{exePath}\"");
            }

            // Kill running remote access processes
            foreach (string procName in ProcessesToKill)
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(procName))
                    {
                        proc.Kill();
                        proc.Dispose();
                    }
                }
                catch { /* process may have exited between enumeration and kill */ }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DisableQuarantine()
    {
        try
        {
            // Delete port-based rules (in + out)
            foreach (var (name, _, _) in BlockedPorts)
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}{name}_In\"");
                RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}{name}_Out\"");
            }

            // Delete app-based rules (in + out)
            foreach (var (name, _) in RemoteAccessApps)
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}App_{name}_In\"");
                RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}App_{name}_Out\"");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsQuarantineActive()
    {
        try
        {
            // Check for the RDP rule as a sentinel — if it exists, quarantine is on
            string output = RunNetsh($"advfirewall firewall show rule name=\"{RulePrefix}RDP_In\"");
            return !output.Contains("No rules match the specified criteria", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string RunNetsh(string args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.Start();

        string output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(NetshTimeoutMs))
        {
            process.Kill();
            throw new TimeoutException($"netsh timed out: {args}");
        }

        return output;
    }

    private static string? FindExecutablePath(string exeName)
    {
        string[] searchDirs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ];

        foreach (string dir in searchDirs)
        {
            try
            {
                var files = Directory.GetFiles(dir, exeName, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            catch { /* access denied on some directories is expected */ }
        }

        return null;
    }
}
