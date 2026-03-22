using LibreHardwareMonitor.Hardware;

namespace PCGuardian;

// --- Data records ---

internal sealed record HardwareSnapshot(
    DateTime Timestamp, bool IsAdmin, bool HasGpu,
    float? CpuTemp, float? CpuLoad, float? CpuPower,
    float? GpuTemp, float? GpuLoad,
    float? CpuFanRpm,
    IReadOnlyList<StorageHealth> Drives,
    BatteryInfo? Battery,
    IReadOnlyList<HardwareSensorReading> AllSensors);

internal sealed record StorageHealth(string Name, float? Temperature, float? RemainingLife, float? TotalWritten);
internal sealed record BatteryInfo(float? ChargeLevel, float? DesignedCapacity, float? FullChargedCapacity, float? DegradationPercent);
internal sealed record HardwareSensorReading(string HardwareName, string HardwareType, string SensorName, string SensorType, float? Value, float? Min, float? Max);

// --- Monitor service ---

/// <summary>
/// Background service wrapping LibreHardwareMonitor. Polls hardware sensors every 5 seconds,
/// builds snapshots, maintains a ring buffer for crypto miner detection, and logs metrics
/// to the database once per minute.
/// </summary>
internal sealed class HardwareMonitor : IDisposable
{
    readonly Computer _computer;
    readonly System.Threading.Timer _timer;
    readonly object _lock = new();
    readonly Database? _db;

    volatile bool _disposed;
    HardwareSnapshot? _current;
    int _tickCount;

    /// <summary>Ring buffer of last 12 snapshots (~60 seconds at 5s intervals).</summary>
    readonly HardwareSnapshot?[] _ring = new HardwareSnapshot?[12];
    int _ringIndex;

    /// <summary>Fires on the thread-pool when new data is ready. Marshal to UI thread.</summary>
    public event Action? Updated;

    public HardwareMonitor(Database? db = null)
    {
        _db = db;

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsBatteryEnabled = true,
        };

        try
        {
            _computer.Open();
        }
        catch
        {
            // Non-admin may fail partially — continue with whatever hardware is accessible
        }

        _timer = new System.Threading.Timer(Tick, null, 0, 5000);
    }

    // --- Public API ---

    /// <summary>Returns the latest snapshot, or null if no data yet.</summary>
    public HardwareSnapshot? GetSnapshot()
    {
        lock (_lock) return _current;
    }

    /// <summary>
    /// Checks for sustained high CPU+GPU load and temperature indicative of crypto mining.
    /// Examines the ring buffer (~last 60 seconds of snapshots).
    /// </summary>
    public (bool IsSuspicious, string Reason) CheckForMining()
    {
        lock (_lock)
        {
            var snapshots = _ring.Where(s => s is not null).ToList();

            // Need at least 10 snapshots (~50 seconds) to make a determination
            if (snapshots.Count < 10)
                return (false, "Insufficient data");

            int highLoadCount = 0;
            int highTempCount = 0;

            foreach (var snap in snapshots)
            {
                bool highCpu = snap!.CpuLoad is > 80f;
                bool highGpu = snap.HasGpu && snap.GpuLoad is > 80f;

                if (highCpu || highGpu)
                    highLoadCount++;

                bool hotCpu = snap.CpuTemp is > 80f;
                bool hotGpu = snap.HasGpu && snap.GpuTemp is > 85f;

                if (hotCpu || hotGpu)
                    highTempCount++;
            }

            // Sustained = majority of snapshots show high values
            bool sustainedLoad = highLoadCount >= snapshots.Count * 0.8;
            bool sustainedTemp = highTempCount >= snapshots.Count * 0.6;

            if (sustainedLoad && sustainedTemp)
            {
                var reasons = new List<string>();

                if (snapshots.Any(s => s!.CpuLoad is > 80f))
                    reasons.Add($"CPU load sustained >80%");
                if (snapshots.Any(s => s!.HasGpu && s.GpuLoad is > 80f))
                    reasons.Add($"GPU load sustained >80%");
                if (snapshots.Any(s => s!.CpuTemp is > 80f))
                    reasons.Add($"CPU temp >80\u00b0C");
                if (snapshots.Any(s => s!.HasGpu && s.GpuTemp is > 85f))
                    reasons.Add($"GPU temp >85\u00b0C");

                return (true, string.Join("; ", reasons));
            }

            return (false, "Normal");
        }
    }

    // --- Core tick ---

    void Tick(object? state)
    {
        if (_disposed) return;

        try
        {
            var allSensors = new List<HardwareSensorReading>();
            float? cpuTemp = null, cpuLoad = null, cpuPower = null;
            float? gpuTemp = null, gpuLoad = null;
            float? cpuFanRpm = null;
            bool hasGpu = false;

            var drives = new List<StorageHealth>();
            BatteryInfo? battery = null;

            foreach (var hardware in _computer.Hardware)
            {
                try
                {
                    hardware.Update();

                    foreach (var sub in hardware.SubHardware)
                    {
                        try { sub.Update(); }
                        catch { /* sensor read failure */ }
                    }

                    CollectSensors(hardware, allSensors);

                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            cpuTemp ??= FindSensorValue(hardware, SensorType.Temperature, "Package", "Core (Tctl/Tdie)", "Core");
                            cpuLoad ??= FindSensorValue(hardware, SensorType.Load, "Total");
                            cpuPower ??= FindSensorValue(hardware, SensorType.Power, "Package", "CPU Package");
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                        case HardwareType.GpuIntel:
                            hasGpu = true;
                            gpuTemp ??= FindSensorValue(hardware, SensorType.Temperature, "GPU Core", "Core");
                            gpuLoad ??= FindSensorValue(hardware, SensorType.Load, "GPU Core", "Core");
                            break;

                        case HardwareType.Motherboard:
                            // Fan sensors are typically on motherboard sub-hardware (SuperIO chip)
                            foreach (var sub in hardware.SubHardware)
                            {
                                cpuFanRpm ??= FindSensorValue(sub, SensorType.Fan, "CPU", "Fan #1", "Fan #2");
                                CollectSensors(sub, allSensors);
                            }
                            break;

                        case HardwareType.Storage:
                            var driveName = hardware.Name ?? "Unknown Drive";
                            var driveTemp = FindSensorValue(hardware, SensorType.Temperature);
                            var remainingLife = FindSensorValue(hardware, SensorType.Level, "Remaining Life", "Percentage Used");
                            var totalWritten = FindSensorValue(hardware, SensorType.Data, "Total Bytes Written", "Host Writes");
                            drives.Add(new StorageHealth(driveName, driveTemp, remainingLife, totalWritten));
                            break;

                        case HardwareType.Battery:
                            var chargeLevel = FindSensorValue(hardware, SensorType.Level, "Charge Level", "Level");
                            var designedCap = FindSensorValue(hardware, SensorType.Energy, "Designed Capacity");
                            var fullCap = FindSensorValue(hardware, SensorType.Energy, "Full Charged Capacity");

                            float? degradation = null;
                            if (designedCap is > 0 && fullCap.HasValue)
                                degradation = (1f - fullCap.Value / designedCap.Value) * 100f;

                            battery = new BatteryInfo(chargeLevel, designedCap, fullCap, degradation);
                            break;
                    }
                }
                catch { /* skip hardware that fails entirely */ }
            }

            var snapshot = new HardwareSnapshot(
                DateTime.UtcNow,
                AdminHelper.IsAdmin(),
                hasGpu,
                cpuTemp, cpuLoad, cpuPower,
                gpuTemp, gpuLoad,
                cpuFanRpm,
                drives.AsReadOnly(),
                battery,
                allSensors.AsReadOnly());

            lock (_lock)
            {
                _current = snapshot;
                _ring[_ringIndex] = snapshot;
                _ringIndex = (_ringIndex + 1) % _ring.Length;
            }

            _tickCount++;

            // Log to database every 12th tick (~once per minute)
            if (_tickCount % 12 == 0)
            {
                try { _db?.LogHardwareMetrics(snapshot.Timestamp, snapshot.CpuTemp, snapshot.CpuLoad, snapshot.GpuTemp, snapshot.GpuLoad, snapshot.CpuFanRpm, snapshot.CpuPower, snapshot.Battery?.ChargeLevel, snapshot.Battery != null ? 100f - (snapshot.Battery.DegradationPercent ?? 0f) : null, null); }
                catch { /* don't crash if DB fails */ }
            }

            Updated?.Invoke();
        }
        catch { /* never crash the timer */ }
    }

    // --- Sensor helpers ---

    /// <summary>
    /// Finds the first sensor matching the given type whose name contains any of the search terms.
    /// If no search terms provided, returns the first sensor of that type.
    /// </summary>
    static float? FindSensorValue(IHardware hardware, SensorType type, params string[] nameHints)
    {
        var sensors = hardware.Sensors.Where(s => s.SensorType == type).ToArray();
        if (sensors.Length == 0) return null;

        if (nameHints.Length > 0)
        {
            foreach (var hint in nameHints)
            {
                var match = sensors.FirstOrDefault(s =>
                    s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
                if (match?.Value is not null)
                    return match.Value;
            }
        }

        // Fallback: first sensor of that type with a value
        return sensors.FirstOrDefault(s => s.Value.HasValue)?.Value;
    }

    /// <summary>Collects all sensor readings from a hardware item into the list.</summary>
    static void CollectSensors(IHardware hardware, List<HardwareSensorReading> list)
    {
        foreach (var sensor in hardware.Sensors)
        {
            try
            {
                list.Add(new HardwareSensorReading(
                    hardware.Name ?? "Unknown",
                    hardware.HardwareType.ToString(),
                    sensor.Name ?? "Unknown",
                    sensor.SensorType.ToString(),
                    sensor.Value,
                    sensor.Min,
                    sensor.Max));
            }
            catch { /* skip unreadable sensor */ }
        }
    }

    // --- Dispose ---

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();

        try { _computer.Close(); }
        catch { /* best-effort cleanup */ }
    }
}
