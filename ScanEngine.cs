using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PCGuardian;

internal static class ScanEngine
{
    // -----------------------------------------------------------------------
    // P/Invoke — GetExtendedTcpTable for port + PID mapping
    // -----------------------------------------------------------------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;
    const uint MIB_TCP_STATE_LISTEN = 2;
    const uint MIB_TCP_STATE_ESTAB = 5;

    sealed record TcpEntry(
        string LocalAddr, int LocalPort,
        string RemoteAddr, int RemotePort,
        uint State, string Process);

    static int ToPort(uint raw) =>
        ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);

    static string ProcName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return "Unknown"; }
    }

    static List<TcpEntry> GetTcpTable()
    {
        var list = new List<TcpEntry>();
        int size = 0;

        // First call to determine buffer size
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return list;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return list;

            int count = Marshal.ReadInt32(buf);
            if (count <= 0 || count > 100_000) return list; // Sanity check

            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ptr = buf + 4;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                    list.Add(new TcpEntry(
                        new IPAddress(row.dwLocalAddr).ToString(),
                        ToPort(row.dwLocalPort),
                        new IPAddress(row.dwRemoteAddr).ToString(),
                        ToPort(row.dwRemotePort),
                        row.dwState,
                        ProcName(row.dwOwningPid)));
                }
                catch { /* skip malformed row */ }
                ptr += rowSize;
            }
        }
        catch { /* P/Invoke failure — return what we have */ }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    // -----------------------------------------------------------------------
    // Helper — run a process and capture stdout (safe, no shell)
    // -----------------------------------------------------------------------

    static string Run(string exe, string args)
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

            // Read stdout/stderr to avoid deadlock
            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(); } catch { }
            }
            return output.Trim();
        }
        catch { return ""; }
    }

    static Status Worst(IEnumerable<Status> s)
    {
        var list = s.ToList();
        if (list.Contains(Status.Danger)) return Status.Danger;
        if (list.Contains(Status.Warning)) return Status.Warning;
        return Status.Safe;
    }

    // -----------------------------------------------------------------------
    // 1. Remote Desktop
    // -----------------------------------------------------------------------

    static Category CheckRemoteDesktop()
    {
        bool on = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server");
            on = (int)(key?.GetValue("fDenyTSConnections") ?? 1) == 0;
        }
        catch { }

        var findings = new List<Finding>
        {
            new("Remote Desktop (RDP)",
                on ? "Enabled \u2014 someone could connect to your screen"
                   : "Disabled \u2014 no one can connect via RDP",
                on ? Status.Danger : Status.Safe)
        };

        if (on)
        {
            bool nla = false;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
                nla = (int)(key?.GetValue("UserAuthentication") ?? 0) == 1;
            }
            catch { }
            findings.Add(new("Password required",
                nla ? "Yes \u2014 a password is needed" : "No \u2014 anyone on your network could connect",
                nla ? Status.Warning : Status.Danger));
        }

        return new("rdp", "\uD83D\uDDA5\uFE0F", "Screen Control",
            "Can someone take over your screen?",
            on ? Status.Danger : Status.Safe,
            on ? "Remote Desktop is turned ON" : "Remote Desktop is OFF",
            findings,
            on ? "Go to Settings \u2192 System \u2192 Remote Desktop and turn it OFF."
               : "Great! Remote Desktop is turned off.");
    }

    // -----------------------------------------------------------------------
    // 2. Remote-access software
    // -----------------------------------------------------------------------

    static readonly string[] RemoteAppPatterns =
    [
        "TeamViewer", "AnyDesk", "VNC", "TigerVNC", "RealVNC", "UltraVNC",
        "TightVNC", "Parsec", "RustDesk", "LogMeIn", "RemotePC", "Splashtop",
        "Ammyy", "ScreenConnect", "ConnectWise", "Chrome Remote"
    ];

    static readonly string[] RemoteProcPatterns =
    [
        "vnc", "anydesk", "teamviewer", "parsec", "rustdesk",
        "logmein", "splashtop", "ammyy", "screenconnect"
    ];

    static Category CheckRemoteSoftware()
    {
        var installed = new List<string>();
        var regPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var path in regPaths)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(path);
                if (baseKey == null) continue;
                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = baseKey.OpenSubKey(subName);
                        var name = sub?.GetValue("DisplayName")?.ToString();
                        if (name != null && RemoteAppPatterns.Any(p =>
                            name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                            installed.Add(name);
                    }
                    catch { }
                }
            }
            catch { }
        }

        var running = new List<string>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (RemoteProcPatterns.Any(p =>
                    proc.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    running.Add(proc.ProcessName);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        var findings = new List<Finding>();
        var uniqueInstalled = installed.Distinct().ToList();
        var uniqueRunning = running.Distinct().ToList();

        if (uniqueInstalled.Count == 0 && uniqueRunning.Count == 0)
        {
            findings.Add(new("Remote access apps", "None found", Status.Safe));
        }

        foreach (var app in uniqueInstalled)
        {
            bool isRunning = uniqueRunning.Any(r =>
                app.Contains(r, StringComparison.OrdinalIgnoreCase));
            findings.Add(new(app,
                isRunning ? "Installed and RUNNING right now" : "Installed but not running",
                isRunning ? Status.Danger : Status.Warning));
        }

        foreach (var proc in uniqueRunning)
        {
            if (!findings.Any(f => f.Label.Contains(proc, StringComparison.OrdinalIgnoreCase)))
                findings.Add(new(proc, "Running now", Status.Danger));
        }

        bool hasRun = uniqueRunning.Count > 0, hasInst = uniqueInstalled.Count > 0;
        var status = hasRun ? Status.Danger : hasInst ? Status.Warning : Status.Safe;

        return new("remote-apps", "\uD83D\uDCE1", "Remote Access Apps",
            "Are there apps that let others see your screen?", status,
            hasRun ? $"{uniqueRunning.Count} remote app(s) running!"
            : hasInst ? $"{uniqueInstalled.Count} remote app(s) installed (not running)"
            : "No remote access apps found",
            findings,
            hasRun ? "Close remote access apps you don't recognize."
            : hasInst ? "Uninstall remote access apps you don't use."
            : "No remote access software found!");
    }

    // -----------------------------------------------------------------------
    // 3. Open ports
    // -----------------------------------------------------------------------

    static readonly Dictionary<int, string> RiskyPorts = new()
    {
        [22] = "SSH", [23] = "Telnet", [3389] = "Remote Desktop",
        [5900] = "VNC", [5901] = "VNC", [5902] = "VNC", [5800] = "VNC Web",
        [445] = "File Sharing (SMB)", [135] = "Windows RPC", [139] = "NetBIOS",
    };

    static readonly HashSet<int> CriticalPorts = [3389, 5900, 5901, 5902, 23];

    static Category CheckOpenPorts()
    {
        var tcp = GetTcpTable();
        var listeners = tcp.Where(t => t.State == MIB_TCP_STATE_LISTEN).ToList();
        var findings = new List<Finding>();
        var exposed = new List<TcpEntry>();

        foreach (var p in listeners)
        {
            // Skip localhost-only listeners — they're safe
            if (p.LocalAddr == "127.0.0.1") continue;
            if (!RiskyPorts.ContainsKey(p.LocalPort)) continue;

            exposed.Add(p);
            findings.Add(new($"Port {p.LocalPort} \u2014 {RiskyPorts[p.LocalPort]}",
                $"Open to network ({p.Process})",
                CriticalPorts.Contains(p.LocalPort) ? Status.Danger : Status.Warning));
        }

        if (findings.Count == 0)
            findings.Add(new("No risky ports open",
                "No known remote-access ports exposed to your network", Status.Safe));

        int totalNet = listeners.Count(p => p.LocalAddr != "127.0.0.1");
        findings.Add(new("Total network-accessible ports",
            $"{totalNet} port(s) listening",
            totalNet > 15 ? Status.Warning : Status.Safe));

        return new("ports", "\uD83D\uDEAA", "Open Doors",
            "Are there digital doors open on your PC?",
            Worst(findings.Select(f => f.Status)),
            exposed.Count > 0 ? $"{exposed.Count} risky port(s) open" : "No risky ports found",
            findings,
            exposed.Count > 0 ? "Close ports you don't need via Windows Firewall."
                               : "Your ports look good!");
    }

    // -----------------------------------------------------------------------
    // 4. Active connections
    // -----------------------------------------------------------------------

    static readonly string[] RecognizedProcs =
    [
        "chrome", "msedge", "firefox", "opera", "brave",
        "discord", "viber", "svchost", "mpdefendercoreservice",
        "claude", "node", "code", "spotify", "steam", "slack",
        "teams", "outlook", "onedrive", "msedgewebview2",
        "searchhost", "widgets", "riotclientservices",
    ];

    static Category CheckConnections()
    {
        var tcp = GetTcpTable();
        var estab = tcp.Where(t =>
            t.State == MIB_TCP_STATE_ESTAB &&
            t.RemoteAddr is not ("127.0.0.1" or "0.0.0.0")).ToList();

        var byProc = estab.GroupBy(c => c.Process).ToList();
        var findings = new List<Finding>();
        int unknown = 0;

        foreach (var g in byProc)
        {
            bool known = RecognizedProcs.Any(r =>
                g.Key.Contains(r, StringComparison.OrdinalIgnoreCase));
            if (!known) unknown++;
            var samples = string.Join(", ", g.Take(3).Select(c => $"{c.RemoteAddr}:{c.RemotePort}"));
            findings.Add(new($"{g.Key} \u2014 {g.Count()} connection(s)", samples,
                known ? Status.Safe : Status.Warning));
        }

        if (findings.Count == 0)
            findings.Add(new("No external connections", "Not connected to any outside servers", Status.Safe));

        return new("connections", "\uD83C\uDF10", "Active Connections",
            "Who is your PC talking to right now?",
            unknown > 3 ? Status.Warning : Status.Safe,
            $"{estab.Count} active connection(s) to the internet",
            findings,
            unknown > 0 ? "Review unrecognized processes making connections."
                         : "All connections look normal!");
    }

    // -----------------------------------------------------------------------
    // 5. Shared folders
    // -----------------------------------------------------------------------

    static readonly HashSet<string> DefaultShares = ["ADMIN$", "C$", "IPC$", "print$", "D$", "E$"];

    static Category CheckSharedFolders()
    {
        var findings = new List<Finding>();
        var customShares = new List<string>();
        int sessionCount = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, Path FROM Win32_Share");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                var name = obj["Name"]?.ToString() ?? "";
                if (!DefaultShares.Contains(name))
                {
                    customShares.Add(name);
                    findings.Add(new(name,
                        $"Sharing: {obj["Path"]}", Status.Warning));
                }
                obj.Dispose();
            }
        }
        catch { }

        if (customShares.Count == 0)
            findings.Add(new("Shared folders", "Only default system shares (normal)", Status.Safe));

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ComputerName, UserName FROM Win32_ServerSession");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                sessionCount++;
                findings.Add(new($"Connected: {obj["UserName"]}",
                    $"From: {obj["ComputerName"]}", Status.Danger));
                obj.Dispose();
            }
        }
        catch { }

        if (sessionCount == 0)
            findings.Add(new("Active sessions", "Nobody connected to your shares", Status.Safe));

        var status = sessionCount > 0 ? Status.Danger
            : customShares.Count > 0 ? Status.Warning : Status.Safe;

        return new("shares", "\uD83D\uDCC1", "Shared Folders",
            "Are you sharing files with anyone?", status,
            sessionCount > 0 ? $"{sessionCount} device(s) connected!"
            : customShares.Count > 0 ? $"{customShares.Count} custom shared folder(s)"
            : "No custom shared folders",
            findings,
            sessionCount > 0 ? "Someone is accessing your shared folders. Check if expected."
            : customShares.Count > 0 ? "Remove shared folders you don't need."
            : "No extra shares. Looking good!");
    }

    // -----------------------------------------------------------------------
    // 6. Remote services
    // -----------------------------------------------------------------------

    static readonly (string Name, string Friendly)[] RemoteServicesList =
    [
        ("TermService", "Remote Desktop Service"),
        ("WinRM", "Windows Remote Management"),
        ("sshd", "SSH Server"),
        ("RemoteRegistry", "Remote Registry Access"),
    ];

    static Category CheckRemoteServices()
    {
        var findings = new List<Finding>();

        foreach (var (name, friendly) in RemoteServicesList)
        {
            try
            {
                using var svc = new ServiceController(name);
                bool running = svc.Status == ServiceControllerStatus.Running;
                bool auto = svc.StartType == ServiceStartMode.Automatic;

                findings.Add(new(friendly,
                    running ? "Currently RUNNING"
                    : auto ? "Stopped but set to auto-start"
                    : "Stopped and disabled",
                    running ? Status.Danger : auto ? Status.Warning : Status.Safe));
            }
            catch
            {
                findings.Add(new(friendly, "Not installed", Status.Safe));
            }
        }

        return new("services", "\u2699\uFE0F", "Remote Services",
            "Are background services allowing remote access?",
            Worst(findings.Select(f => f.Status)),
            findings.Any(f => f.Status == Status.Danger) ? "Some remote services are running!"
            : findings.Any(f => f.Status == Status.Warning) ? "Some services set to auto-start"
            : "All remote services are off",
            findings,
            findings.Any(f => f.Status == Status.Danger)
                ? "Stop unneeded services: open services.msc and disable them."
                : "Remote services look good.");
    }

    // -----------------------------------------------------------------------
    // 7. Firewall
    // -----------------------------------------------------------------------

    static Category CheckFirewall()
    {
        var findings = new List<Finding>();
        int disabled = 0;

        var profiles = new[] { "DomainProfile", "StandardProfile", "PublicProfile" };
        foreach (var profile in profiles)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}");
                if ((int)(key?.GetValue("EnableFirewall") ?? 0) == 0)
                    disabled++;
            }
            catch { }
        }

        findings.Add(disabled > 0
            ? new("Windows Firewall", $"{disabled} profile(s) DISABLED!", Status.Danger)
            : new("Windows Firewall", "All profiles enabled", Status.Safe));

        // Firewall rules — using PowerShell as there's no clean managed API
        var psPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psPath)) psPath = "powershell.exe"; // Fallback to PATH
        var rulesOutput = Run(psPath,
            "-NoProfile -Command \"Get-NetFirewallRule -Direction Inbound -Enabled True -Action Allow -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'Remote|VNC|TeamViewer|AnyDesk|SSH|RDP|Parsec|RustDesk' } | Select-Object -ExpandProperty DisplayName\"");

        var rules = rulesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();

        if (rules.Count > 0)
            foreach (var r in rules)
                findings.Add(new(r, "Allows inbound remote connections", Status.Warning));
        else
            findings.Add(new("Remote access rules", "No rules allowing remote access", Status.Safe));

        return new("firewall", "\uD83D\uDEE1\uFE0F", "Firewall",
            "Is your security guard doing its job?",
            Worst(findings.Select(f => f.Status)),
            disabled > 0 ? "Firewall is partially disabled!"
            : rules.Count > 0 ? $"{rules.Count} rule(s) allowing remote access"
            : "Firewall is properly configured",
            findings,
            disabled > 0
                ? "Enable your firewall NOW! Settings \u2192 Privacy & Security \u2192 Windows Security."
                : rules.Count > 0 ? "Review firewall rules and remove unneeded ones."
                : "Your firewall looks solid!");
    }

    // -----------------------------------------------------------------------
    // 8. Active users
    // -----------------------------------------------------------------------

    static Category CheckActiveUsers()
    {
        var findings = new List<Finding>();
        int remoteCount = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LogonId, LogonType FROM Win32_LogonSession WHERE LogonType = 10");
            using var results = searcher.Get();
            remoteCount = results.Count;
        }
        catch { }

        findings.Add(new(Environment.UserName,
            "Local console session", Status.Safe));

        if (remoteCount > 0)
            findings.Add(new($"{remoteCount} remote session(s)",
                "Someone is connected via Remote Desktop!", Status.Danger));
        else
            findings.Add(new("Remote sessions", "No remote users detected", Status.Safe));

        return new("users", "\uD83D\uDC64", "Who's Logged In",
            "Is anyone else using your PC?",
            remoteCount > 0 ? Status.Danger : Status.Safe,
            remoteCount > 0 ? $"{remoteCount} remote user(s) detected!"
                             : "Only you are logged in",
            findings,
            remoteCount > 0
                ? "Someone is connected remotely! Disconnect them and change your password."
                : "Only you are using this PC. All clear!");
    }

    // -----------------------------------------------------------------------
    // 9. Startup Programs
    // -----------------------------------------------------------------------

    static Category CheckStartupPrograms()
    {
        var findings = new List<Finding>();

        void ReadReg(RegistryKey hive, string subKey)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey);
                if (key is null) return;
                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name)?.ToString() ?? "(unknown)";
                    findings.Add(new(name, value, Status.Warning));
                }
            }
            catch { }
        }

        ReadReg(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        ReadReg(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");

        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (Directory.Exists(startup))
                foreach (var f in Directory.GetFiles(startup, "*.lnk"))
                    findings.Add(new(Path.GetFileNameWithoutExtension(f), f, Status.Warning));
        }
        catch { }

        int count = findings.Count;
        var status = count <= 5 ? Status.Safe : Status.Warning;

        return new("startup", "\uD83D\uDE80", "Startup Programs",
            "What runs when your PC starts?", status,
            count == 0 ? "No startup programs" : $"{count} program(s) start with Windows",
            findings,
            "Disable unneeded startup programs via Task Manager \u2192 Startup tab.");
    }

    // -----------------------------------------------------------------------
    // 10. Scheduled Tasks
    // -----------------------------------------------------------------------

    static Category CheckScheduledTasks()
    {
        var findings = new List<Finding>();
        try
        {
            var csv = Run("schtasks.exe", "/query /fo CSV /nh /v");
            if (string.IsNullOrWhiteSpace(csv))
            {
                findings.Add(new("Scheduled tasks", "Could not query", Status.Warning));
                return BuildTasksCat(findings);
            }

            var suspicious = new[] { "powershell -enc", "downloadstring", "invoke-expression", "bypass", "hidden" };
            var tempPaths = new[] { @"\appdata\local\temp", @"\users\public", @"\temp\" };

            foreach (var rawLine in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = line.Split("\",\"");
                if (cols.Length < 13) continue;

                var taskName = cols[1];
                var action = cols.Length > 8 ? cols[8] : "";
                var state = cols.Length > 12 ? cols[12] : "";

                if (state.Contains("Disabled", StringComparison.OrdinalIgnoreCase)) continue;
                if (taskName.Contains(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                if (taskName.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) continue;

                var actionLower = action.ToLowerInvariant();
                bool isDanger = suspicious.Any(p => actionLower.Contains(p));
                bool isTemp = tempPaths.Any(p => actionLower.Contains(p));

                if ((actionLower.EndsWith(".vbs") || actionLower.EndsWith(".bat")) && isTemp)
                    isDanger = true;

                var shortName = taskName.Contains('\\') ? taskName[(taskName.LastIndexOf('\\') + 1)..] : taskName;
                var truncAction = action.Length > 120 ? action[..120] + "\u2026" : action;

                findings.Add(new(shortName, truncAction,
                    isDanger ? Status.Danger : isTemp ? Status.Warning : Status.Safe));
            }

            if (findings.Count == 0)
                findings.Add(new("Scheduled tasks", "Only built-in tasks found", Status.Safe));
        }
        catch (Exception ex)
        {
            findings.Add(new("Error", ex.Message, Status.Warning));
        }

        return BuildTasksCat(findings);

        static Category BuildTasksCat(List<Finding> f)
        {
            int d = f.Count(x => x.Status == Status.Danger);
            int w = f.Count(x => x.Status == Status.Warning);
            var overall = d > 0 ? Status.Danger : w > 0 ? Status.Warning : Status.Safe;
            return new("tasks", "\uD83D\uDCC5", "Scheduled Tasks",
                "Are there hidden tasks running on a schedule?", overall,
                d > 0 ? $"{d} suspicious task(s)!" : $"{f.Count} third-party task(s) found",
                f, d > 0 ? "Inspect flagged tasks in Task Scheduler (taskschd.msc)."
                : "Your scheduled tasks look clean.");
        }
    }

    // -----------------------------------------------------------------------
    // 11. Antivirus & Updates
    // -----------------------------------------------------------------------

    static Category CheckAntivirusStatus()
    {
        var findings = new List<Finding>();
        var worst = Status.Safe;

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2",
                "SELECT displayName, productState FROM AntiVirusProduct");
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using var mo = obj;
                var name = mo["displayName"]?.ToString() ?? "Unknown";
                var state = Convert.ToInt32(mo["productState"]);
                bool enabled = ((state >> 12) & 0xF) == 1;
                bool upToDate = ((state >> 4) & 0xF) == 0;
                var s = enabled && upToDate ? Status.Safe : enabled ? Status.Warning : Status.Danger;
                if (s > worst) worst = s;
                findings.Add(new(name, $"{(enabled ? "Enabled" : "Disabled")}, definitions {(upToDate ? "current" : "outdated")}", s));
            }
            if (findings.Count == 0)
            {
                findings.Add(new("No Antivirus", "No AV registered with Windows Security", Status.Danger));
                worst = Status.Danger;
            }
        }
        catch
        {
            findings.Add(new("AV Check", "Could not query SecurityCenter2", Status.Warning));
            if (worst < Status.Warning) worst = Status.Warning;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
            var raw = key?.GetValue("LastSuccessInstallTimeStart") as string;
            if (raw != null && DateTime.TryParse(raw, out var lastUpdate))
            {
                int days = (int)(DateTime.Now - lastUpdate).TotalDays;
                var s = days <= 30 ? Status.Safe : days <= 90 ? Status.Warning : Status.Danger;
                if (s > worst) worst = s;
                findings.Add(new("Windows Update", $"Last installed {days} days ago", s));
            }
            else findings.Add(new("Windows Update", "Could not determine last update", Status.Warning));
        }
        catch { findings.Add(new("Windows Update", "Registry read failed", Status.Warning)); }

        return new("antivirus", "\uD83E\uDDA0", "Antivirus & Updates",
            "Is your PC protected against threats?", worst,
            worst == Status.Safe ? "Antivirus active and up to date" : "Protection issues detected",
            findings, worst != Status.Safe ? "Check Windows Security and run Windows Update." : "Keep automatic updates enabled.");
    }

    // -----------------------------------------------------------------------
    // 12. DNS Settings
    // -----------------------------------------------------------------------

    static Category CheckDnsSettings()
    {
        var findings = new List<Finding>();
        var worst = Status.Safe;
        var knownSafe = new HashSet<string> { "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1", "9.9.9.9", "149.112.112.112", "208.67.222.222", "208.67.220.220" };

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var dns = nic.GetIPProperties().DnsAddresses;
                if (dns.Count == 0) continue;

                var servers = dns.Select(a => a.ToString()).ToList();
                bool allTrusted = servers.All(s =>
                    knownSafe.Contains(s) || s.StartsWith("192.168.") || s.StartsWith("10.") ||
                    s.StartsWith("172.") || s.StartsWith("fe80:") || s is "::1" or "127.0.0.1");

                var s = allTrusted ? Status.Safe : Status.Warning;
                if (s > worst) worst = s;
                findings.Add(new(nic.Name, string.Join(", ", servers), s));
            }

            if (findings.Count == 0)
                findings.Add(new("No adapters", "No active network adapters found", Status.Warning));
        }
        catch (Exception ex)
        {
            findings.Add(new("DNS check failed", ex.Message, Status.Warning));
            worst = Status.Warning;
        }

        return new("dns", "\uD83D\uDD0D", "DNS Settings",
            "Could someone be redirecting your internet traffic?", worst,
            worst == Status.Safe ? "DNS servers are trusted" : "Unknown DNS servers detected",
            findings, "Use trusted DNS like Cloudflare (1.1.1.1) or Google (8.8.8.8).");
    }

    // -----------------------------------------------------------------------
    // 13. USB Devices
    // -----------------------------------------------------------------------

    static Category CheckUsbDevices()
    {
        var findings = new List<Finding>();
        int historyCount = 0;

        try
        {
            using var usbStorKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBSTOR");
            if (usbStorKey != null)
            {
                foreach (string className in usbStorKey.GetSubKeyNames())
                {
                    using var classKey = usbStorKey.OpenSubKey(className);
                    if (classKey == null) continue;
                    foreach (string instanceId in classKey.GetSubKeyNames())
                    {
                        using var deviceKey = classKey.OpenSubKey(instanceId);
                        if (deviceKey == null) continue;
                        historyCount++;
                        string name = deviceKey.GetValue("FriendlyName")?.ToString()
                            ?? className.Replace("Disk&", "").Replace("_", " ").Trim();
                        findings.Add(new(name, "Previously connected USB storage", Status.Safe));
                    }
                }
            }
        }
        catch { findings.Add(new("USB check", "Could not read device history", Status.Warning)); }

        if (findings.Count == 0)
            findings.Add(new("USB Devices", "No USB storage history found", Status.Safe));

        return new("usb", "\uD83D\uDD0C", "USB Devices",
            "What devices have been plugged into your PC?", Status.Safe,
            $"{historyCount} USB storage device(s) in history",
            findings, "Avoid plugging in unknown USB drives \u2014 they can carry malware.");
    }

    // -----------------------------------------------------------------------
    // Full scan — each check is wrapped so one failure doesn't kill the scan
    // -----------------------------------------------------------------------

    public static Report RunFullScan()
    {
        var categories = new List<Category>();

        var checks = new (string Id, string Icon, string Title, Func<Category> Fn)[]
        {
            ("rdp", "\uD83D\uDDA5\uFE0F", "Screen Control", CheckRemoteDesktop),
            ("remote-apps", "\uD83D\uDCE1", "Remote Access Apps", CheckRemoteSoftware),
            ("ports", "\uD83D\uDEAA", "Open Doors", CheckOpenPorts),
            ("connections", "\uD83C\uDF10", "Active Connections", CheckConnections),
            ("shares", "\uD83D\uDCC1", "Shared Folders", CheckSharedFolders),
            ("services", "\u2699\uFE0F", "Remote Services", CheckRemoteServices),
            ("firewall", "\uD83D\uDEE1\uFE0F", "Firewall", CheckFirewall),
            ("users", "\uD83D\uDC64", "Who's Logged In", CheckActiveUsers),
            ("startup", "\uD83D\uDE80", "Startup Programs", CheckStartupPrograms),
            ("tasks", "\uD83D\uDCC5", "Scheduled Tasks", CheckScheduledTasks),
            ("antivirus", "\uD83E\uDDA0", "Antivirus & Updates", CheckAntivirusStatus),
            ("dns", "\uD83D\uDD0D", "DNS Settings", CheckDnsSettings),
            ("usb", "\uD83D\uDD0C", "USB Devices", CheckUsbDevices),
        };

        foreach (var (id, icon, title, fn) in checks)
        {
            try
            {
                categories.Add(fn());
            }
            catch (Exception ex)
            {
                // If a single check crashes, report it gracefully instead of killing everything
                categories.Add(new(id, icon, title,
                    "Could not complete this check",
                    Status.Warning,
                    $"Check failed: {ex.Message}",
                    [new("Error", ex.Message, Status.Warning)],
                    "Try running as Administrator for full access."));
            }
        }

        int safe = categories.Count(c => c.Status == Status.Safe);
        int warn = categories.Count(c => c.Status == Status.Warning);
        int danger = categories.Count(c => c.Status == Status.Danger);

        return new(DateTime.Now,
            Worst(categories.Select(c => c.Status)),
            categories, safe, warn, danger);
    }
}
