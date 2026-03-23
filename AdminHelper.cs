using System.Diagnostics;
using System.Security.Principal;

namespace PCGuardian;

internal static class AdminHelper
{
    const string TaskName = "PCGuardianStartup";

    /// <summary>Returns true if the current process has admin privileges.</summary>
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Restarts the app as administrator. Returns true if the restart was initiated.
    /// </summary>
    public static bool RestartAsAdmin(string extraArgs = "")
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = extraArgs,
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Creates a scheduled task that runs the app as admin at logon — no UAC prompt.
    /// This is how real security tools auto-start with admin rights.
    /// Must be called from an admin process (the one-time UAC click).
    /// </summary>
    public static bool SetupAdminAutoStart(bool enable)
    {
        if (!IsAdmin()) return false;

        try
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;

            if (enable)
            {
                // Create a scheduled task that runs at logon with highest privileges
                var xml = $"""
                    <?xml version="1.0" encoding="UTF-16"?>
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <RegistrationInfo>
                        <Description>PC Guardian - Security Scanner (runs with admin rights)</Description>
                      </RegistrationInfo>
                      <Triggers>
                        <LogonTrigger>
                          <Enabled>true</Enabled>
                          <Delay>PT10S</Delay>
                        </LogonTrigger>
                      </Triggers>
                      <Principals>
                        <Principal>
                          <LogonType>InteractiveToken</LogonType>
                          <RunLevel>HighestAvailable</RunLevel>
                        </Principal>
                      </Principals>
                      <Settings>
                        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                        <AllowHardTerminate>true</AllowHardTerminate>
                        <StartWhenAvailable>true</StartWhenAvailable>
                        <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                        <AllowStartOnDemand>true</AllowStartOnDemand>
                        <Enabled>true</Enabled>
                        <Hidden>false</Hidden>
                        <RunOnlyIfIdle>false</RunOnlyIfIdle>
                        <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                      </Settings>
                      <Actions>
                        <Exec>
                          <Command>{SecurityXmlEscape(exePath)}</Command>
                          <Arguments>--minimized</Arguments>
                        </Exec>
                      </Actions>
                    </Task>
                    """;

                // Write XML to temp file, import with schtasks
                var xmlPath = Path.Combine(Path.GetTempPath(), "pcguardian-task.xml");
                File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);

                var result = RunCommand("schtasks.exe",
                    $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");

                try { File.Delete(xmlPath); } catch { }

                // Also remove any old registry startup entry (we're using task scheduler now)
                SettingsManager.SetStartWithWindows(false);

                return result.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                RunCommand("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Checks if the admin auto-start task exists.</summary>
    public static bool IsAdminAutoStartEnabled()
    {
        try
        {
            var result = RunCommand("schtasks.exe", $"/Query /TN \"{TaskName}\" /NH");
            return result.Contains(TaskName, StringComparison.OrdinalIgnoreCase)
                && !result.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string RunCommand(string exe, string args)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            _ = proc.StandardError.ReadToEndAsync();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            return output;
        }
        catch { return ""; }
    }

    static string SecurityXmlEscape(string input) =>
        input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
             .Replace("\"", "&quot;").Replace("'", "&apos;");
}
