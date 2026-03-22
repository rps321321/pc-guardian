using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PCGuardian;

/// <summary>
/// Tracks per-process I/O rates and TCP connection counts.
/// Uses GetProcessIoCounters (kernel32) for byte throughput and
/// GetExtendedTcpTable (iphlpapi) for connection counts.
/// Works without admin for user-owned processes; admin gives full visibility.
/// </summary>
internal sealed class BandwidthMonitor : IDisposable
{
    // --- P/Invoke: I/O counters ---

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // --- P/Invoke: TCP table ---

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order,
        int af, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct TcpRow
    {
        public uint state, localAddr, localPort, remoteAddr, remotePort, pid;
    }

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;

    // --- State ---

    record IoSnapshot(ulong BytesRead, ulong BytesWritten, DateTime Time, string Name);

    readonly Dictionary<int, IoSnapshot> _prev = new();
    readonly object _lock = new();
    readonly System.Threading.Timer _timer;
    volatile bool _disposed;

    List<AppTraffic> _current = [];
    long _totalIn, _totalOut;
    int _totalConns, _totalProcs;

    /// <summary>Fires on the thread-pool when new data is ready. Marshal to UI thread.</summary>
    public event Action? Updated;

    public BandwidthMonitor(int intervalMs = 2000)
    {
        _timer = new System.Threading.Timer(Tick, null, 0, intervalMs);
    }

    // --- Public API ---

    public List<AppTraffic> GetTraffic()
    {
        lock (_lock) return new List<AppTraffic>(_current);
    }

    public (long totalIn, long totalOut, int conns, int procs) GetTotals()
    {
        lock (_lock) return (_totalIn, _totalOut, _totalConns, _totalProcs);
    }

    // --- Core tick ---

    void Tick(object? state)
    {
        if (_disposed) return;

        try
        {
            var now = DateTime.UtcNow;

            // 1. Get TCP connections grouped by PID
            var connsByPid = GetConnectionCounts();

            // 2. Get I/O rates per process
            var traffic = new Dictionary<int, AppTraffic>();
            var newPrev = new Dictionary<int, IoSnapshot>();

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    int pid = proc.Id;
                    if (pid == 0) continue; // System Idle

                    string name = proc.ProcessName;
                    var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (handle == IntPtr.Zero) continue;

                    try
                    {
                        if (!GetProcessIoCounters(handle, out var io)) continue;

                        long inRate = 0, outRate = 0;
                        if (_prev.TryGetValue(pid, out var prev) && prev.Name == name)
                        {
                            double secs = (now - prev.Time).TotalSeconds;
                            if (secs > 0.1)
                            {
                                inRate = (long)((io.ReadTransferCount - prev.BytesRead) / secs);
                                outRate = (long)((io.WriteTransferCount - prev.BytesWritten) / secs);
                                if (inRate < 0) inRate = 0;
                                if (outRate < 0) outRate = 0;
                            }
                        }

                        newPrev[pid] = new IoSnapshot(io.ReadTransferCount, io.WriteTransferCount, now, name);

                        int conns = connsByPid.GetValueOrDefault(pid, 0);

                        // Only include processes with any activity
                        if (inRate > 0 || outRate > 0 || conns > 0 || io.ReadTransferCount + io.WriteTransferCount > 1_000_000)
                        {
                            traffic[pid] = new AppTraffic(
                                name,
                                ProcessMonitor.Categorize(name),
                                pid,
                                inRate, outRate,
                                io.ReadTransferCount, io.WriteTransferCount,
                                conns);
                        }
                    }
                    finally { CloseHandle(handle); }
                }
                catch { /* access denied or process exited */ }
                finally { proc.Dispose(); }
            }

            // 3. Aggregate by process name (merge PIDs like chrome with 10 processes)
            var merged = new Dictionary<string, AppTraffic>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in traffic.Values)
            {
                if (merged.TryGetValue(t.Name, out var existing))
                {
                    merged[t.Name] = existing with
                    {
                        InRate = existing.InRate + t.InRate,
                        OutRate = existing.OutRate + t.OutRate,
                        TotalIn = existing.TotalIn + t.TotalIn,
                        TotalOut = existing.TotalOut + t.TotalOut,
                        Connections = existing.Connections + t.Connections,
                    };
                }
                else
                {
                    merged[t.Name] = t;
                }
            }

            var sorted = merged.Values
                .OrderByDescending(t => t.InRate + t.OutRate)
                .ThenByDescending(t => t.Connections)
                .ToList();

            long totIn = sorted.Sum(t => t.InRate);
            long totOut = sorted.Sum(t => t.OutRate);
            int totConns = sorted.Sum(t => t.Connections);

            lock (_lock)
            {
                _prev.Clear();
                foreach (var kv in newPrev) _prev[kv.Key] = kv.Value;
                _current = sorted;
                _totalIn = totIn;
                _totalOut = totOut;
                _totalConns = totConns;
                _totalProcs = sorted.Count;
            }

            Updated?.Invoke();
        }
        catch { /* never crash the timer */ }
    }

    // --- TCP connection counts by PID ---

    Dictionary<int, int> GetConnectionCounts()
    {
        var counts = new Dictionary<int, int>();
        int size = 0;

        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return counts;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return counts;

            int numEntries = Marshal.ReadInt32(buf);
            if (numEntries <= 0 || numEntries > 100_000) return counts;

            int rowSize = Marshal.SizeOf<TcpRow>();
            var ptr = buf + 4;

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<TcpRow>(ptr);
                int pid = (int)row.pid;
                counts[pid] = counts.GetValueOrDefault(pid, 0) + 1;
                ptr += rowSize;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }

        return counts;
    }

    // --- Format helpers ---

    public static string FormatRate(long bytesPerSec) => bytesPerSec switch
    {
        >= 1_073_741_824 => $"{bytesPerSec / 1_073_741_824.0:F1} GB/s",
        >= 1_048_576 => $"{bytesPerSec / 1_048_576.0:F1} MB/s",
        >= 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
        > 0 => $"{bytesPerSec} B/s",
        _ => "—",
    };

    public static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        > 0 => $"{bytes} B",
        _ => "—",
    };

    // --- Dispose ---

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait for any in-flight Tick callback to finish
        using var waitHandle = new ManualResetEvent(false);
        _timer.Dispose(waitHandle);
        waitHandle.WaitOne();
    }
}

/// <summary>Per-process traffic summary.</summary>
internal record AppTraffic(
    string Name,
    string Category,
    int Pid,
    long InRate,       // bytes/sec download
    long OutRate,      // bytes/sec upload
    ulong TotalIn,     // cumulative bytes read (session)
    ulong TotalOut,    // cumulative bytes written (session)
    int Connections);  // active TCP connection count
