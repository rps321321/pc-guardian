using System.Diagnostics;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace PCGuardian;

internal static class MinerDetector
{
    // -----------------------------------------------------------------------
    // Known miner process names (case-insensitive match)
    // -----------------------------------------------------------------------

    private static readonly HashSet<string> KnownMinerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "xmrig", "xmr-stak", "cpuminer", "minerd", "minergate", "nicehash",
        "ethminer", "phoenix", "phoenixminer", "t-rex", "trex", "nbminer",
        "lolminer", "gminer", "teamredminer", "bzminer", "excavator", "nanominer",
        "wildrig", "srbminer", "claymore", "ccminer", "ewbf", "kawpowminer",
        "randomx", "moneroocean", "xmrig-nvidia", "xmrig-amd", "dstm",
        "cgminer", "bfgminer", "sgminer", "multiminer", "nheqminer",
        "cryptonight", "xmr-stak-cpu", "xmr-stak-rx", "minergate-cli",
        "nicehash-miner", "nicehashquickminer", "ethdcrminer64", "bminer",
        "zminer", "optiminer", "progpowminer", "autolykos", "cortex-miner",
        "cudo-miner", "cudominer", "honeyminer", "kryptex", "verthashminer",
        "miniz", "z-enemy", "funakoshi", "cast-xmr", "swarm", "miniZ",
        "srbminer-multi", "xmrig-proxy"
    };

    // -----------------------------------------------------------------------
    // Known mining pool ports
    // -----------------------------------------------------------------------

    private static readonly HashSet<int> KnownPoolPorts = new()
    {
        3333, 4444, 5555, 8888, 9999, 14444, 14433, 3334, 7777,
        20535, 10201, 6666, 13333
    };

    // -----------------------------------------------------------------------
    // Pool domain patterns
    // -----------------------------------------------------------------------

    private static readonly Regex PoolDomainRegex = new(
        @"(pool|mine|mining|miner|stratum)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> KnownPoolDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "nanopool", "ethermine", "f2pool", "poolin", "antpool", "nicehash",
        "minergate", "hashvault", "moneroocean", "2miners", "flexpool",
        "hiveon", "unmineable"
    };

    // -----------------------------------------------------------------------
    // Wallet address patterns
    // -----------------------------------------------------------------------

    private static readonly Regex BtcWalletRegex = new(
        @"[13][a-km-zA-HJ-NP-Z1-9]{25,34}",
        RegexOptions.Compiled);

    private static readonly Regex EthWalletRegex = new(
        @"0x[0-9a-fA-F]{40}",
        RegexOptions.Compiled);

    private static readonly Regex XmrWalletRegex = new(
        @"4[0-9AB][1-9A-HJ-NP-Za-km-z]{93}",
        RegexOptions.Compiled);

    // -----------------------------------------------------------------------
    // Mining algorithm keywords
    // -----------------------------------------------------------------------

    private static readonly HashSet<string> MiningAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "randomx", "ethash", "kawpow", "equihash", "cryptonight", "scrypt",
        "sha256", "x11", "x13", "x16r", "x16rv2", "lyra2rev2", "lyra2rev3",
        "zhash", "cuckoo", "cuckatoo", "cuckaroo", "beamhash", "progpow",
        "autolykos", "octopus", "etchash", "firopow", "verthash", "blake2s",
        "blake3", "keccak", "groestl", "skein"
    };

    // -----------------------------------------------------------------------
    // Whitelisted high-CPU processes
    // -----------------------------------------------------------------------

    private static readonly HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "ffmpeg", "HandBrake", "HandBrakeCLI", "blender", "x264", "x265",
        "python", "python3", "pythonw", "node", "FAHClient", "boinc",
        "boinc_client", "chrome", "MsMpEng", "MpCmdRun",
        "cl", "clang", "clang++", "gcc", "g++", "rustc", "dotnet", "go",
        "javac", "msbuild", "devenv", "rider64", "code",
        "AfterEffects", "Premiere", "resolve", "obs64",
        "7z", "WinRAR", "unrar"
    };

    // -----------------------------------------------------------------------
    // Stratum protocol pattern
    // -----------------------------------------------------------------------

    private static readonly Regex StratumRegex = new(
        @"stratum\+?(tcp|ssl|tls)?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // -----------------------------------------------------------------------
    // P/Invoke -- GetExtendedTcpTable (same pattern as ScanEngine)
    // -----------------------------------------------------------------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB = 5;

    private static int ToPort(uint raw) =>
        ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);

    private sealed record TcpConn(int RemotePort, string RemoteAddr, uint Pid);

    private static List<TcpConn> GetEstablishedConnections()
    {
        var list = new List<TcpConn>();
        int size = 0;

        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return list;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return list;

            int count = Marshal.ReadInt32(buf);
            if (count <= 0 || count > 100_000) return list;

            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ptr = buf + 4;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                    if (row.dwState == MIB_TCP_STATE_ESTAB)
                    {
                        list.Add(new TcpConn(
                            ToPort(row.dwRemotePort),
                            new IPAddress(row.dwRemoteAddr).ToString(),
                            row.dwOwningPid));
                    }
                }
                catch { /* skip malformed row */ }
                ptr += rowSize;
            }
        }
        catch { /* P/Invoke failure */ }
        finally { Marshal.FreeHGlobal(buf); }

        return list;
    }

    // -----------------------------------------------------------------------
    // WMI process info: ExecutablePath + CommandLine
    // -----------------------------------------------------------------------

    private sealed record ProcessInfo(
        int Pid, string Name, string ExecutablePath, string CommandLine);

    private static List<ProcessInfo> GetWmiProcessInfo(HashSet<int> pids)
    {
        var results = new List<ProcessInfo>();
        if (pids.Count == 0) return results;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    int pid = Convert.ToInt32(mo["ProcessId"]);
                    if (!pids.Contains(pid)) continue;

                    results.Add(new ProcessInfo(
                        pid,
                        mo["Name"]?.ToString() ?? "",
                        mo["ExecutablePath"]?.ToString() ?? "",
                        mo["CommandLine"]?.ToString() ?? ""));
                }
            }
        }
        catch (ManagementException) { }

        return results;
    }

    // -----------------------------------------------------------------------
    // Signature check
    // -----------------------------------------------------------------------

    private static bool IsSigned(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            var cert = X509Certificate.CreateFromSignedFile(filePath);
            return cert is not null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Path-based checks
    // -----------------------------------------------------------------------

    private static bool IsRunningFromTemp(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var upper = path.ToUpperInvariant();
        return upper.Contains(@"\TEMP\")
            || upper.Contains(@"\TMP\")
            || upper.Contains(@"$RECYCLE.BIN");
    }

    private static bool IsRunningFromAppData(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var upper = path.ToUpperInvariant();
        return upper.Contains(@"\APPDATA\");
    }

    private static bool IsRunningFromProgramFiles(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var upper = path.ToUpperInvariant();
        return upper.Contains(@"\PROGRAM FILES\")
            || upper.Contains(@"\PROGRAM FILES (X86)\");
    }

    // -----------------------------------------------------------------------
    // Command line analysis
    // -----------------------------------------------------------------------

    private static bool HasWalletAddress(string cmdLine) =>
        BtcWalletRegex.IsMatch(cmdLine)
        || EthWalletRegex.IsMatch(cmdLine)
        || XmrWalletRegex.IsMatch(cmdLine);

    private static bool HasStratumUrl(string cmdLine) =>
        StratumRegex.IsMatch(cmdLine);

    private static bool HasMiningAlgorithm(string cmdLine)
    {
        foreach (var algo in MiningAlgorithms)
        {
            if (cmdLine.Contains(algo, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool HasPoolDomain(string cmdLine)
    {
        if (PoolDomainRegex.IsMatch(cmdLine))
            return true;

        foreach (var pool in KnownPoolDomains)
        {
            if (cmdLine.Contains(pool, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Main scoring for a single process
    // -----------------------------------------------------------------------

    private static (int Score, List<string> Reasons) ScoreProcess(
        ProcessInfo info,
        ILookup<uint, TcpConn> connsByPid,
        bool hasHighCpu)
    {
        int score = 0;
        var reasons = new List<string>();
        string nameOnly = Path.GetFileNameWithoutExtension(info.Name).ToLowerInvariant();

        // --- Whitelist deduction (check early) ---
        if (Whitelist.Contains(nameOnly))
        {
            score -= 50;
            reasons.Add("Whitelisted high-CPU app (-50)");
        }

        // --- Known miner name (exact match) ---
        if (KnownMinerNames.Contains(nameOnly))
        {
            score += 50;
            reasons.Add($"Known miner name: {nameOnly} (+50)");
        }
        // --- Known miner name as substring in path ---
        else if (!string.IsNullOrWhiteSpace(info.ExecutablePath))
        {
            var pathLower = info.ExecutablePath.ToLowerInvariant();
            foreach (var miner in KnownMinerNames)
            {
                if (pathLower.Contains(miner))
                {
                    score += 35;
                    reasons.Add($"Miner name '{miner}' found in path (+35)");
                    break;
                }
            }
        }

        // --- Command line analysis ---
        string cmdLine = info.CommandLine ?? "";
        if (cmdLine.Length > 0)
        {
            if (HasWalletAddress(cmdLine))
            {
                score += 45;
                reasons.Add("Wallet address in command line (+45)");
            }

            if (HasStratumUrl(cmdLine))
            {
                score += 45;
                reasons.Add("Stratum pool URL in command line (+45)");
            }

            if (HasMiningAlgorithm(cmdLine))
            {
                score += 30;
                reasons.Add("Mining algorithm in command line (+30)");
            }
        }

        // --- Network connections ---
        var conns = connsByPid[(uint)info.Pid];
        bool hasPoolPort = false;
        bool hasPoolDomainConn = false;

        foreach (var conn in conns)
        {
            if (KnownPoolPorts.Contains(conn.RemotePort))
                hasPoolPort = true;
        }

        if (hasPoolPort)
        {
            score += 20;
            reasons.Add("Connection to known mining pool port (+20)");
        }

        // Check command line for pool domain references (network DNS not available here)
        if (cmdLine.Length > 0 && HasPoolDomain(cmdLine))
        {
            score += 30;
            reasons.Add("Mining pool domain in command line (+30)");
        }

        // --- Path-based scoring ---
        string exePath = info.ExecutablePath ?? "";

        if (IsRunningFromTemp(exePath))
        {
            score += 30;
            reasons.Add("Running from Temp/Recycle Bin (+30)");
        }
        else if (IsRunningFromAppData(exePath) && !IsRunningFromProgramFiles(exePath))
        {
            score += 15;
            reasons.Add("Running from AppData (non-standard) (+15)");
        }

        if (IsRunningFromProgramFiles(exePath))
        {
            score -= 10;
            reasons.Add("Running from Program Files (-10)");
        }

        // --- Signature check ---
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            if (IsSigned(exePath))
            {
                score -= 20;
                reasons.Add("Valid digital signature (-20)");
            }
            else
            {
                score += 25;
                reasons.Add("Unsigned binary (+25)");
            }
        }

        // --- No visible window + high CPU ---
        if (hasHighCpu)
        {
            try
            {
                using var proc = Process.GetProcessById(info.Pid);
                if (proc.MainWindowHandle == IntPtr.Zero)
                {
                    score += 10;
                    reasons.Add("No visible window + high CPU (+10)");
                }
            }
            catch { /* process may have exited */ }
        }

        return (Math.Max(score, 0), reasons);
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Analyzes all running processes for crypto mining activity using a
    /// weighted multi-signal scoring system.
    /// </summary>
    /// <param name="cpuPercent">Current total CPU usage percent (0-100).</param>
    /// <param name="gpuPercent">Current total GPU usage percent (0-100).</param>
    /// <returns>
    /// IsSuspicious: true if any process scored >= 50 (WARNING or ALERT).
    /// Reason: human-readable explanation of the worst finding.
    /// Score: highest score among all processes.
    /// </returns>
    public static (bool IsSuspicious, string Reason, int Score) Analyze(
        float cpuPercent, float gpuPercent)
    {
        try
        {
            return AnalyzeCore(cpuPercent, gpuPercent);
        }
        catch (Exception ex)
        {
            return (false, $"Miner detection error: {ex.Message}", 0);
        }
    }

    private static (bool IsSuspicious, string Reason, int Score) AnalyzeCore(
        float cpuPercent, float gpuPercent)
    {
        // Gather all process PIDs
        var allProcs = Process.GetProcesses();
        var pidSet = new HashSet<int>();
        foreach (var p in allProcs)
        {
            try { pidSet.Add(p.Id); }
            catch { /* skip */ }
            finally { p.Dispose(); }
        }

        // Get WMI info for all processes
        var wmiInfos = GetWmiProcessInfo(pidSet);

        // Get established TCP connections indexed by PID
        var connections = GetEstablishedConnections();
        var connsByPid = connections.ToLookup(c => c.Pid);

        // Determine if system is under high CPU/GPU load
        bool isHighCpu = cpuPercent > 60f || gpuPercent > 60f;

        // Score each process
        int worstScore = 0;
        string worstReason = "";
        string worstProcess = "";

        foreach (var info in wmiInfos)
        {
            var (score, reasons) = ScoreProcess(info, connsByPid, isHighCpu);

            if (score > worstScore)
            {
                worstScore = score;
                worstProcess = info.Name;
                worstReason = string.Join("; ", reasons);
            }
        }

        // Apply thresholds
        if (worstScore >= 80)
        {
            return (true,
                $"ALERT: Likely crypto miner detected — {worstProcess} (score {worstScore}). {worstReason}",
                worstScore);
        }

        if (worstScore >= 50)
        {
            return (true,
                $"WARNING: Possible crypto miner — {worstProcess} (score {worstScore}). {worstReason}",
                worstScore);
        }

        return (false, "CLEAN: No crypto mining activity detected.", worstScore);
    }
}
