using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PCGuardian;

/// <summary>
/// Polls CPU, GPU, RAM, and disk I/O counters every 2 seconds.
/// Thread-safe; fires <see cref="Updated"/> on the thread-pool when new data is ready.
/// </summary>
internal sealed class PerfService : IDisposable
{
    // --- P/Invoke: RAM ---

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // --- GPU regex ---

    static readonly Regex EngTypeRegex = new(@"engtype_(\w+)", RegexOptions.Compiled);
    static readonly Regex LuidRegex = new(@"luid_0x[0-9a-fA-F]+_0x([0-9a-fA-F]+)", RegexOptions.Compiled);

    // --- Constants ---

    const int TickIntervalMs = 2000;
    const int MaxFailures = 3;
    const int GpuReEnumerateTicks = 15; // every 30s at 2s interval

    // --- State ---

    readonly object _lock = new();
    readonly System.Threading.Timer _timer;
    volatile bool _disposed;

    PerfReading? _latest;
    int _tickCount;

    // CPU
    PerformanceCounter? _cpuCounter;
    int _cpuFailures;
    bool _cpuDead;

    // GPU
    PerformanceCounter[]? _gpuCounters;
    string[]? _gpuLuids; // parallel array: adapter LUID per counter
    int _gpuFailures;
    bool _gpuDead;
    int _gpuEnumerateTick;

    // Disk
    PerformanceCounter? _diskRead;
    PerformanceCounter? _diskWrite;
    int _diskFailures;
    bool _diskDead;

    /// <summary>Fires on the thread-pool when new data is ready. Marshal to UI thread.</summary>
    public event Action? Updated;

    public PerfService()
    {
        InitCpu();
        InitGpu();
        InitDisk();

        // Prime rate-based counters (first NextValue() always returns 0)
        PrimeCounter(_cpuCounter);
        PrimeCounter(_diskRead);
        PrimeCounter(_diskWrite);
        PrimeGpuCounters();

        // Start timer; skip first tick via _tickCount starting at -1
        _tickCount = -1;
        _timer = new System.Threading.Timer(Tick, null, TickIntervalMs, TickIntervalMs);
    }

    // --- Public API ---

    public PerfReading? GetLatest()
    {
        lock (_lock) return _latest;
    }

    // --- Init helpers ---

    void InitCpu()
    {
        try
        {
            // Prefer "Processor Information" / "% Processor Utility" (more accurate on modern CPUs)
            _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuCounter.NextValue(); // validate it exists
        }
        catch
        {
            try
            {
                _cpuCounter?.Dispose();
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            }
            catch
            {
                _cpuCounter?.Dispose();
                _cpuCounter = null;
                _cpuDead = true;
            }
        }
    }

    void InitGpu()
    {
        try
        {
            EnumerateGpuCounters();
        }
        catch
        {
            _gpuCounters = null;
            _gpuLuids = null;
        }
    }

    void EnumerateGpuCounters()
    {
        lock (_lock)
        {
            // Dispose previous
            if (_gpuCounters is not null)
            {
                foreach (var c in _gpuCounters)
                {
                    try { c.Dispose(); } catch { }
                }
            }

            var cat = new PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames();

            var counters = new List<PerformanceCounter>();
            var luids = new List<string>();

            foreach (var inst in instances)
            {
                var etMatch = EngTypeRegex.Match(inst);
                if (!etMatch.Success || !string.Equals(etMatch.Groups[1].Value, "3D", StringComparison.OrdinalIgnoreCase))
                    continue;

                var luidMatch = LuidRegex.Match(inst);
                if (!luidMatch.Success) continue;

                try
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    counters.Add(pc);
                    luids.Add(luidMatch.Groups[1].Value);
                }
                catch { }
            }

            _gpuCounters = counters.ToArray();
            _gpuLuids = luids.ToArray();
        }
    }

    void InitDisk()
    {
        try
        {
            _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
            _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        }
        catch
        {
            _diskRead?.Dispose();
            _diskWrite?.Dispose();
            _diskRead = null;
            _diskWrite = null;
            _diskDead = true;
        }
    }

    static void PrimeCounter(PerformanceCounter? pc)
    {
        try { pc?.NextValue(); } catch { }
    }

    void PrimeGpuCounters()
    {
        if (_gpuCounters is null) return;
        foreach (var c in _gpuCounters)
        {
            try { c.NextValue(); } catch { }
        }
    }

    // --- Core tick ---

    void Tick(object? state)
    {
        if (_disposed) return;

        var tick = Interlocked.Increment(ref _tickCount);

        // Skip first tick — priming tick, rate counters return 0
        if (tick == 0) return;

        try
        {
            float cpu = ReadCpu();
            float gpu = ReadGpu(tick);
            var (ramUsedPct, ramTotalMB, ramAvailMB) = ReadRam();
            var (diskR, diskW) = ReadDisk();

            var reading = new PerfReading(
                DateTime.UtcNow,
                cpu, gpu,
                ramUsedPct, ramTotalMB, ramAvailMB,
                diskR, diskW);

            lock (_lock) { _latest = reading; }
            Updated?.Invoke();
        }
        catch { /* never crash the timer */ }
    }

    // --- CPU ---

    float ReadCpu()
    {
        if (_cpuDead || _cpuCounter is null) return 0f;
        try
        {
            float val = _cpuCounter.NextValue();
            _cpuFailures = 0;
            return Math.Clamp(val, 0f, 100f);
        }
        catch
        {
            if (++_cpuFailures >= MaxFailures) _cpuDead = true;
            return 0f;
        }
    }

    // --- GPU ---

    float ReadGpu(int tick)
    {
        if (_gpuDead) return 0f;

        try
        {
            // Re-enumerate instances periodically (instances change as apps start/stop)
            if (tick % GpuReEnumerateTicks == 0 || _gpuCounters is null || _gpuCounters.Length == 0)
            {
                EnumerateGpuCounters();
                PrimeGpuCounters();
                _gpuEnumerateTick = tick;
                return 0f; // skip this tick after re-enumerate (priming)
            }

            // Skip the tick right after re-enumeration (priming tick)
            if (tick == _gpuEnumerateTick + 1) return 0f;

            if (_gpuCounters is null || _gpuCounters.Length == 0) return 0f;

            // Sum utilization per adapter LUID, then take MAX across adapters
            var sumByLuid = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _gpuCounters.Length; i++)
            {
                try
                {
                    float val = _gpuCounters[i].NextValue();
                    string luid = _gpuLuids![i];
                    sumByLuid[luid] = sumByLuid.GetValueOrDefault(luid) + val;
                }
                catch (InvalidOperationException)
                {
                    // Known .NET bug: GPU Engine instances can vanish mid-read.
                    // Trigger re-enumerate on next tick.
                    _gpuEnumerateTick = tick - GpuReEnumerateTicks;
                }
                catch { }
            }

            float maxUtil = 0f;
            foreach (var val in sumByLuid.Values)
            {
                if (val > maxUtil) maxUtil = val;
            }

            _gpuFailures = 0;
            return Math.Clamp(maxUtil, 0f, 100f);
        }
        catch (InvalidOperationException)
        {
            // Category-level failure; re-enumerate next tick
            return 0f;
        }
        catch
        {
            if (++_gpuFailures >= MaxFailures) _gpuDead = true;
            return 0f;
        }
    }

    // --- RAM ---

    static (uint usedPct, ulong totalMB, ulong availMB) ReadRam()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem))
            return (0, 0, 0);

        return (
            mem.dwMemoryLoad,
            mem.ullTotalPhys / (1024 * 1024),
            mem.ullAvailPhys / (1024 * 1024));
    }

    // --- Disk ---

    (float read, float write) ReadDisk()
    {
        if (_diskDead) return (0f, 0f);
        try
        {
            float r = _diskRead?.NextValue() ?? 0f;
            float w = _diskWrite?.NextValue() ?? 0f;
            _diskFailures = 0;
            return (Math.Max(r, 0f), Math.Max(w, 0f));
        }
        catch
        {
            if (++_diskFailures >= MaxFailures) _diskDead = true;
            return (0f, 0f);
        }
    }

    // --- Dispose ---

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Wait for any in-flight Tick callback to finish before disposing counters
        using var waitHandle = new ManualResetEvent(false);
        _timer.Dispose(waitHandle);
        waitHandle.WaitOne();

        lock (_lock)
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null;

            _diskRead?.Dispose();
            _diskRead = null;

            _diskWrite?.Dispose();
            _diskWrite = null;

            if (_gpuCounters is not null)
            {
                foreach (var c in _gpuCounters)
                {
                    try { c.Dispose(); } catch { }
                }
                _gpuCounters = null;
            }
        }
    }
}

/// <summary>Snapshot of system performance metrics.</summary>
internal sealed record PerfReading(
    DateTime Timestamp,
    float CpuPercent,
    float GpuPercent,
    uint RamUsedPercent,
    ulong RamTotalMB,
    ulong RamAvailMB,
    float DiskReadBps,
    float DiskWriteBps);
