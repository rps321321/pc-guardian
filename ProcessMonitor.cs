using System.Diagnostics;

namespace PCGuardian;

internal sealed record ProcessInfo(
    string Name,
    string Path,
    string Company,
    string Category,
    double MemoryMB);

internal sealed class ProcessMonitor : IDisposable
{
    private readonly Database _db;
    private readonly System.Threading.Timer _timer;
    private readonly object _snapshotLock = new();
    private Dictionary<int, ProcessInfo> _lastSnapshot = new();

    public ProcessMonitor(Database db)
    {
        _db = db;

        // First tick: seed the snapshot without logging start events.
        var initial = TakeSnapshot();
        lock (_snapshotLock)
        {
            _lastSnapshot = initial;
        }

        // Fire every 30 seconds; no initial delay (first real diff after 30s).
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // ------------------------------------------------------------------
    //  Snapshot
    // ------------------------------------------------------------------

    private static Dictionary<int, ProcessInfo> TakeSnapshot()
    {
        var snapshot = new Dictionary<int, ProcessInfo>();
        Process[] processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            try
            {
                int pid = proc.Id;
                if (pid is 0 or 4) continue; // System Idle / System

                string name = proc.ProcessName;
                string path = TryGetPath(proc);
                string company = TryGetCompany(proc);
                string category = Categorize(name);
                double memoryMB = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);

                snapshot[pid] = new ProcessInfo(name, path, company, category, memoryMB);
            }
            catch
            {
                // Process may have exited between enumeration and property access.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return snapshot;
    }

    private static string TryGetPath(Process proc)
    {
        try { return proc.MainModule?.FileName ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string TryGetCompany(Process proc)
    {
        try { return proc.MainModule?.FileVersionInfo.CompanyName ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ------------------------------------------------------------------
    //  Diff & persist
    // ------------------------------------------------------------------

    private void Tick()
    {
        try
        {
            var current = TakeSnapshot();
            var now = DateTime.UtcNow;

            Dictionary<int, ProcessInfo> previous;
            lock (_snapshotLock)
            {
                previous = _lastSnapshot;
            }

            // Detect new processes (PIDs in current but not in last).
            foreach (var (pid, info) in current)
            {
                if (!previous.ContainsKey(pid))
                {
                    _db.UpsertProgram(info.Name, info.Path, info.Company, info.Category);
                    _db.StartSession(info.Name, info.Path, pid, now, info.MemoryMB);
                }
            }

            // Detect stopped processes (PIDs in last but not in current).
            foreach (var (pid, info) in previous)
            {
                if (!current.ContainsKey(pid))
                {
                    _db.EndSession(info.Name, pid, now);
                }
            }

            lock (_snapshotLock)
            {
                _lastSnapshot = current;
            }
        }
        catch (Exception ex)
        {
            // Timer callbacks must never throw; log and continue.
            Debug.WriteLine($"[ProcessMonitor] Tick error: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    //  Categorization
    // ------------------------------------------------------------------

    internal static string Categorize(string processName)
    {
        string lower = processName.ToLowerInvariant();

        if (IsAny(lower, "chrome", "msedge", "firefox", "opera", "brave"))
            return "Browser";

        if (IsAny(lower, "discord", "slack", "teams", "viber", "telegram", "whatsapp", "zoom"))
            return "Communication";

        if (IsAny(lower, "steam", "epicgameslauncher", "riotclientservices", "valorant"))
            return "Gaming";

        if (IsAny(lower, "code", "node", "python", "dotnet", "devenv", "rider", "idea", "cursor"))
            return "Development";

        if (IsAny(lower, "vnc", "anydesk", "teamviewer", "parsec", "rustdesk"))
            return "Remote Access";

        if (IsAny(lower, "svchost", "csrss", "lsass", "services", "smss", "wininit",
                        "conhost", "dllhost", "taskhostw"))
            return "System";

        if (IsAny(lower, "explorer", "dwm", "searchhost", "startmenuexperiencehost",
                        "shellexperiencehost", "widgets"))
            return "Windows";

        if (IsAny(lower, "powerpnt", "winword", "excel", "outlook", "notion", "onenote"))
            return "Productivity";

        if (IsAny(lower, "mpdefendercoreservice", "msmpeng", "securityhealthservice"))
            return "Security";

        return "Other";
    }

    private static bool IsAny(string value, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (value.Contains(kw, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    //  Disposal
    // ------------------------------------------------------------------

    public void Dispose()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        using var waitHandle = new ManualResetEvent(false);
        if (_timer.Dispose(waitHandle))
            waitHandle.WaitOne(TimeSpan.FromSeconds(10));
    }
}
