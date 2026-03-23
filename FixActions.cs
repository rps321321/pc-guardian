using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PCGuardian;

internal sealed record FixResult(bool Success, string Message);

internal static class FixActions
{
    // -----------------------------------------------------------------
    // 1. Disable Remote Desktop
    // -----------------------------------------------------------------

    public static FixResult DisableRemoteDesktop()
    {
        try
        {
            if (!AdminHelper.IsAdmin())
                return new(false, "Administrator privileges required. Restart PC Guardian as admin.");

            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server", writable: true);
            if (key is null)
                return new(false, "Could not open Terminal Server registry key.");

            key.SetValue("fDenyTSConnections", 1, RegistryValueKind.DWord);
            return new(true, "Remote Desktop disabled (fDenyTSConnections set to 1).");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    // -----------------------------------------------------------------
    // 2. Stop and disable a Windows service
    // -----------------------------------------------------------------

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool ChangeServiceConfig(
        IntPtr hService, uint dwServiceType, uint dwStartType,
        uint dwErrorControl, string? lpBinaryPathName, string? lpLoadOrderGroup,
        IntPtr lpdwTagId, string? lpDependencies, string? lpServiceStartName,
        string? lpPassword, string? lpDisplayName);

    const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    const uint SERVICE_DISABLED = 0x4;

    public static FixResult StopService(string serviceName)
    {
        try
        {
            if (!AdminHelper.IsAdmin())
                return new(false, "Administrator privileges required to stop services.");

            using var sc = new ServiceController(serviceName);

            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }

            // Disable via registry — most reliable across .NET versions
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
            key?.SetValue("Start", 4, RegistryValueKind.DWord); // 4 = Disabled

            return new(true, $"Service '{serviceName}' stopped and set to Disabled.");
        }
        catch (InvalidOperationException)
        {
            return new(false, $"Service '{serviceName}' not found.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    // -----------------------------------------------------------------
    // 3. Kill all processes by name
    // -----------------------------------------------------------------

    public static FixResult KillProcess(string processName)
    {
        try
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0)
                return new(true, $"No running processes named '{processName}'.");

            int killed = 0;
            var errors = new List<string>();

            foreach (var proc in procs)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5_000);
                    killed++;
                }
                catch (Exception ex)
                {
                    errors.Add($"PID {proc.Id}: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }

            if (errors.Count > 0)
                return new(killed > 0,
                    $"Killed {killed}/{procs.Length}. Errors: {string.Join("; ", errors)}");

            return new(true, $"Killed {killed} instance(s) of '{processName}'.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    // -----------------------------------------------------------------
    // 4. Remove a firewall rule by name
    // -----------------------------------------------------------------

    public static FixResult RemoveFirewallRule(string ruleName)
    {
        try
        {
            if (!AdminHelper.IsAdmin())
                return new(false, "Administrator privileges required to modify firewall rules.");

            if (!System.Text.RegularExpressions.Regex.IsMatch(ruleName, @"^[\w\- ]+$"))
                return new FixResult(false, "Invalid rule name.");

            var output = RunCommand("netsh",
                $"advfirewall firewall delete rule name=\"{ruleName}\"");

            bool ok = output.Contains("Ok", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("deleted", StringComparison.OrdinalIgnoreCase);

            return ok
                ? new(true, $"Firewall rule '{ruleName}' removed.")
                : new(false, $"Could not remove rule. netsh output: {output.Trim()}");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    // -----------------------------------------------------------------
    // 5. Disable a startup entry from the Run registry keys
    // -----------------------------------------------------------------

    public static FixResult DisableStartupEntry(string name)
    {
        try
        {
            bool removed = false;

            // HKCU — always writable for the current user
            removed |= TryRemoveRunValue(
                Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                name);

            // HKLM — needs admin
            if (AdminHelper.IsAdmin())
            {
                removed |= TryRemoveRunValue(
                    Registry.LocalMachine,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    name);
            }

            return removed
                ? new(true, $"Startup entry '{name}' removed.")
                : new(false, $"Startup entry '{name}' not found in Run keys.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    static bool TryRemoveRunValue(RegistryKey hive, string subKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: true);
            if (key?.GetValue(name) is null) return false;
            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    // -----------------------------------------------------------------
    // 6. Block an inbound TCP port via Windows Firewall
    // -----------------------------------------------------------------

    public static FixResult BlockPort(int port, string description)
    {
        try
        {
            if (!AdminHelper.IsAdmin())
                return new(false, "Administrator privileges required to add firewall rules.");

            var ruleName = $"PCGuardian_Block_{port}";
            var output = RunCommand("netsh",
                $"advfirewall firewall add rule name=\"{ruleName}\" " +
                $"dir=in action=block protocol=TCP localport={port}");

            bool ok = output.Contains("Ok", StringComparison.OrdinalIgnoreCase);
            return ok
                ? new(true, $"Port {port} ({description}) blocked with rule '{ruleName}'.")
                : new(false, $"Failed to block port {port}. netsh output: {output.Trim()}");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    // -----------------------------------------------------------------
    // 7. Unblock a previously blocked port
    // -----------------------------------------------------------------

    public static FixResult UnblockPort(int port)
    {
        try
        {
            if (!AdminHelper.IsAdmin())
                return new(false, "Administrator privileges required to remove firewall rules.");

            var ruleName = $"PCGuardian_Block_{port}";
            var output = RunCommand("netsh",
                $"advfirewall firewall delete rule name=\"{ruleName}\"");

            bool ok = output.Contains("Ok", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("deleted", StringComparison.OrdinalIgnoreCase);

            return ok
                ? new(true, $"Port {port} unblocked (rule '{ruleName}' removed).")
                : new(false, $"Could not remove rule. netsh output: {output.Trim()}");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    // -----------------------------------------------------------------
    // Helper — run a command and capture stdout (no shell window)
    // -----------------------------------------------------------------

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
            // Read stderr asynchronously to avoid deadlock
            _ = proc.StandardError.ReadToEndAsync();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15_000);
            return output;
        }
        catch { return ""; }
    }
}
