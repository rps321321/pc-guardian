using System.Security.Principal;

namespace PCGuardian;

// ── Data records (previously in HardwareMonitor.cs) ──────────────────────────

internal sealed record HardwareSnapshot(
    DateTime Timestamp, bool IsAdmin, bool HasGpu,
    float? CpuTemp, float? CpuLoad, float? CpuPower,
    float? GpuTemp, float? GpuLoad,
    float? CpuFanRpm,
    IReadOnlyList<StorageHealth> Drives,
    BatteryInfo? Battery,
    IReadOnlyList<HardwareSensorReading> AllSensors);

internal sealed record StorageHealth(
    string Name, float? Temperature, float? RemainingLife, float? TotalWritten);

internal sealed record BatteryInfo(
    float? ChargeLevel, float? DesignedCapacity,
    float? FullChargedCapacity, float? DegradationPercent);

internal sealed record HardwareSensorReading(
    string HardwareName, string HardwareType,
    string SensorName, string SensorType,
    float? Value, float? Min, float? Max);

// ── Facade ───────────────────────────────────────────────────────────────────

/// <summary>
/// Drop-in replacement for the deleted HardwareMonitor.
/// Owns PerfService + WmiService + SecurityService and exposes a unified
/// <see cref="HardwareSnapshot"/> via the <see cref="Updated"/> event.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    const int RingBufferSize = 12;
    const int DbLogEveryNTicks = 30; // ~60 s at 2 s interval

    readonly object _lock = new();
    readonly PerfService? _perf;
    readonly WmiService? _wmi;
    readonly SecurityService? _security;
    readonly Database? _db;
    readonly bool _isAdmin;

    readonly HardwareSnapshot?[] _ring = new HardwareSnapshot?[RingBufferSize];
    int _ringIndex;
    int _ringCount;

    HardwareSnapshot? _current;
    int _perfTicksSinceDbLog;

    volatile bool _disposed;

    /// <summary>Fires on the thread-pool when a new snapshot is ready.</summary>
    public event Action? Updated;

    public SystemMonitor(Database? db = null)
    {
        _db = db;
        _isAdmin = IsRunningAsAdmin();

        // Bug 3 fix: wrap each service construction individually so one failure
        // doesn't prevent the others from initializing.
        try { _perf = new PerfService(); }
        catch { _perf = null; }

        try { _wmi = new WmiService(); }
        catch { _wmi = null; }

        try { _security = new SecurityService(); }
        catch { _security = null; }

        if (_perf is not null) _perf.Updated += OnSubServiceUpdated;
        if (_wmi is not null) _wmi.Updated += OnSubServiceUpdated;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>Returns the most recent snapshot, or null if none yet.</summary>
    public HardwareSnapshot? GetSnapshot()
    {
        lock (_lock) return _current;
    }

    /// <summary>
    /// Checks for sustained high CPU + GPU usage that may indicate crypto mining.
    /// </summary>
    public (bool IsSuspicious, string Reason) CheckForMining()
    {
        lock (_lock)
        {
            int count = Math.Min(_ringCount, RingBufferSize);
            if (count < 6) // need ~12 s of data minimum
                return (false, string.Empty);

            int highTicks = 0;
            for (int i = 0; i < count; i++)
            {
                var snap = _ring[i];
                if (snap is null) continue;

                float cpu = snap.CpuLoad ?? 0;
                float gpu = snap.GpuLoad ?? 0;
                if (cpu > 80 || gpu > 80)
                    highTicks++;
            }

            // Sustained > 80% on CPU or GPU for most of the buffer (~1 minute)
            if (highTicks >= count * 0.8)
            {
                return (true,
                    $"Sustained high utilization detected ({highTicks}/{count} samples above 80%) — possible crypto miner");
            }

            return (false, string.Empty);
        }
    }

    /// <summary>Delegates to SecurityService.</summary>
    public SecurityPosture? GetSecurityPosture() => _security?.GetPosture();

    /// <summary>Delegates to WmiService static system info.</summary>
    public SystemStaticInfo? GetSystemInfo() => _wmi?.GetStaticInfo();

    /// <summary>Returns the latest perf-counter reading, or null if none yet.</summary>
    public PerfReading? GetLatestPerf() => _perf?.GetLatest();

    /// <summary>Returns WMI static hardware info.</summary>
    public SystemStaticInfo? GetStaticInfo() => _wmi?.GetStaticInfo();

    /// <summary>Returns WMI dynamic hardware info (drives, battery, thermal), or null.</summary>
    public DynamicHwInfo? GetDynamic() => _wmi?.GetDynamic();

    // ── Event handler ────────────────────────────────────────────────────

    void OnSubServiceUpdated()
    {
        if (_disposed) return;

        try
        {
            var snapshot = ComposeSnapshot();

            lock (_lock)
            {
                _current = snapshot;
                _ring[_ringIndex] = snapshot;
                _ringIndex = (_ringIndex + 1) % RingBufferSize;
                if (_ringCount < RingBufferSize) _ringCount++;
            }

            // DB logging every 30th perf tick (~60 s)
            if (Interlocked.Increment(ref _perfTicksSinceDbLog) >= DbLogEveryNTicks)
            {
                Interlocked.Exchange(ref _perfTicksSinceDbLog, 0);
                LogToDatabase(snapshot);
            }

            Updated?.Invoke();
        }
        catch
        {
            // Never crash the caller's thread-pool callback
        }
    }

    // ── Snapshot composition ─────────────────────────────────────────────

    HardwareSnapshot ComposeSnapshot()
    {
        var perf = _perf?.GetLatest();
        var wmiDynamic = _wmi?.GetDynamic();

        float? cpuLoad = perf?.CpuPercent;
        float? gpuLoad = perf?.GpuPercent;
        float? cpuTemp = wmiDynamic?.ThermalZoneTempC;

        var staticInfo = _wmi?.GetStaticInfo();
        bool hasGpu = (gpuLoad.HasValue && gpuLoad > 0)
            || (staticInfo is not null && !string.IsNullOrEmpty(staticInfo.GpuName));

        // Map WMI drive data to StorageHealth records
        var drives = MapDrives(wmiDynamic?.Drives);

        // Map WMI battery data to BatteryInfo
        var battery = MapBattery(wmiDynamic?.Battery);

        // Build AllSensors from all available readings
        var sensors = BuildSensorList(cpuLoad, gpuLoad, cpuTemp, drives, battery, perf);

        return new HardwareSnapshot(
            Timestamp: DateTime.UtcNow,
            IsAdmin: _isAdmin,
            HasGpu: hasGpu,
            CpuTemp: cpuTemp,
            CpuLoad: cpuLoad,
            CpuPower: null,   // not available without LHM
            GpuTemp: null,    // not available without LHM
            GpuLoad: gpuLoad,
            CpuFanRpm: null,  // not available without LHM
            Drives: drives,
            Battery: battery,
            AllSensors: sensors);
    }

    static IReadOnlyList<StorageHealth> MapDrives(IReadOnlyList<DriveHealth>? wmiDrives)
    {
        if (wmiDrives is null || wmiDrives.Count == 0)
            return Array.Empty<StorageHealth>();

        var result = new StorageHealth[wmiDrives.Count];
        for (int i = 0; i < wmiDrives.Count; i++)
        {
            var d = wmiDrives[i];
            // Map DriveHealth (WMI) to StorageHealth (HardwareSnapshot compat)
            // StorageHealth expects: Name, Temperature?, RemainingLife?, TotalWritten?
            // HealthStatus "Healthy"=100%, "Warning"=50%, "Unhealthy"=5% as rough remaining life
            float? life = d.HealthStatus switch
            {
                "Healthy" => 100f,
                "Warning" => 50f,
                "Unhealthy" => 5f,
                _ => null,
            };
            result[i] = new StorageHealth(d.Name, null, life, null);
        }
        return result;
    }

    static BatteryInfo? MapBattery(BatteryHealth? wmiBat)
    {
        if (wmiBat is null) return null;

        float degradation = wmiBat.DegradationPercent;
        if (degradation < 0) degradation = 0;

        return new BatteryInfo(
            wmiBat.ChargePercent,
            wmiBat.DesignCapacityMWh,
            wmiBat.FullChargeCapacityMWh,
            degradation);
    }

    static List<HardwareSensorReading> BuildSensorList(
        float? cpuLoad, float? gpuLoad, float? cpuTemp,
        IReadOnlyList<StorageHealth> drives, BatteryInfo? battery,
        PerfReading? perf)
    {
        var sensors = new List<HardwareSensorReading>();

        if (cpuLoad.HasValue)
            sensors.Add(new("CPU", "Processor", "CPU Total", "Load", cpuLoad, null, null));

        if (gpuLoad.HasValue && gpuLoad > 0)
            sensors.Add(new("GPU", "Graphics", "GPU Core", "Load", gpuLoad, null, null));

        if (cpuTemp.HasValue)
            sensors.Add(new("CPU", "Processor", "CPU Package", "Temperature", cpuTemp, null, null));

        foreach (var d in drives)
        {
            if (d.Temperature.HasValue)
                sensors.Add(new(d.Name, "Storage", "Temperature", "Temperature", d.Temperature, null, null));
            if (d.RemainingLife.HasValue)
                sensors.Add(new(d.Name, "Storage", "Remaining Life", "Level", d.RemainingLife, null, null));
        }

        if (battery is not null)
        {
            if (battery.ChargeLevel.HasValue)
                sensors.Add(new("Battery", "Battery", "Charge Level", "Level", battery.ChargeLevel, null, null));
            if (battery.DegradationPercent.HasValue)
                sensors.Add(new("Battery", "Battery", "Degradation", "Level", battery.DegradationPercent, null, null));
        }

        if (perf is not null)
        {
            sensors.Add(new("RAM", "Memory", "Used", "Load", perf.RamUsedPercent, null, null));
            if (perf.DiskReadBps > 0)
                sensors.Add(new("Disk", "Storage", "Read", "Throughput", perf.DiskReadBps, null, null));
            if (perf.DiskWriteBps > 0)
                sensors.Add(new("Disk", "Storage", "Write", "Throughput", perf.DiskWriteBps, null, null));
        }

        return sensors;
    }

    // ── DB logging ───────────────────────────────────────────────────────

    void LogToDatabase(HardwareSnapshot snap)
    {
        if (_db is null) return;

        try
        {
            _db.LogHardwareMetrics(
                snap.Timestamp,
                snap.CpuTemp,
                snap.CpuLoad,
                snap.GpuTemp,
                snap.GpuLoad,
                snap.CpuFanRpm,
                snap.CpuPower,
                snap.Battery?.ChargeLevel,
                snap.Battery?.DegradationPercent,
                topProcesses: null);
        }
        catch
        {
            // DB errors should not crash the monitor
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_perf is not null) _perf.Updated -= OnSubServiceUpdated;
        if (_wmi is not null) _wmi.Updated -= OnSubServiceUpdated;

        _perf?.Dispose();
        _wmi?.Dispose();
    }
}

// ── Backward-compatible alias so existing call sites compile unchanged ────────

/// <summary>
/// Alias for <see cref="SystemMonitor"/> — keeps existing code (MainForm,
/// ScanEngine) compiling without any changes to call sites.
/// </summary>
internal sealed class HardwareMonitor : IDisposable
{
    readonly SystemMonitor _inner;

    public HardwareMonitor(Database? db = null) => _inner = new SystemMonitor(db);

    public event Action? Updated
    {
        add => _inner.Updated += value;
        remove => _inner.Updated -= value;
    }

    public HardwareSnapshot? GetSnapshot() => _inner.GetSnapshot();
    public (bool IsSuspicious, string Reason) CheckForMining() => _inner.CheckForMining();
    public SecurityPosture GetSecurityPosture() => _inner.GetSecurityPosture();
    public SystemStaticInfo GetSystemInfo() => _inner.GetSystemInfo();
    public void Dispose() => _inner.Dispose();
}
