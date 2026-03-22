using System.Management;

namespace PCGuardian;

// ── Data records ────────────────────────────────────────────────────

internal sealed record SystemStaticInfo(
    string CpuName, int CpuCores, int CpuThreads,
    ulong TotalRamBytes, string OsCaption, string OsBuild,
    string BiosVersion, string BoardModel, string GpuName,
    DateTime BootTime);

internal sealed record DynamicHwInfo(
    DateTime Timestamp, float? ThermalZoneTempC,
    BatteryHealth? Battery, IReadOnlyList<DriveHealth> Drives);

internal sealed record BatteryHealth(
    float ChargePercent, uint DesignCapacityMWh,
    uint FullChargeCapacityMWh, float DegradationPercent,
    uint CycleCount, bool IsCharging, bool IsAcConnected);

internal sealed record DriveHealth(
    string Name, string Model, string MediaType, string BusType,
    long SizeBytes, long FreeBytes, string HealthStatus,
    bool? PredictFailure);

// ── WMI Service ─────────────────────────────────────────────────────

internal sealed class WmiService : IDisposable
{
    private readonly SystemStaticInfo _static;
    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();

    private DynamicHwInfo? _dynamic;
    private int _tickCount;
    private bool _disposed;

    public event Action? Updated;

    public WmiService()
    {
        _static = QueryStaticInfo();
        _timer = new System.Threading.Timer(OnTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    // ── Public API ──────────────────────────────────────────────────

    public SystemStaticInfo GetStaticInfo() => _static;

    public DynamicHwInfo? GetDynamic()
    {
        lock (_lock) return _dynamic;
    }

    // ── Static queries (run once) ───────────────────────────────────

    private static SystemStaticInfo QueryStaticInfo()
    {
        string cpuName = "Unknown";
        int cpuCores = 0, cpuThreads = 0;
        uint maxClock = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    cpuName = mo["Name"]?.ToString()?.Trim() ?? "Unknown";
                    cpuCores = Convert.ToInt32(mo["NumberOfCores"]);
                    cpuThreads = Convert.ToInt32(mo["NumberOfLogicalProcessors"]);
                    maxClock = Convert.ToUInt32(mo["MaxClockSpeed"]);
                }
                break; // first CPU only
            }
        }
        catch (ManagementException) { }

        ulong totalRam = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    totalRam = Convert.ToUInt64(mo["TotalVisibleMemorySize"]) * 1024; // KB → bytes
                }
                break;
            }
        }
        catch (ManagementException) { }

        string osCaption = "Unknown", osVersion = "", osBuild = "", osArch = "";
        DateTime bootTime = DateTime.MinValue;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    osCaption = mo["Caption"]?.ToString()?.Trim() ?? "Unknown";
                    osVersion = mo["Version"]?.ToString() ?? "";
                    osBuild = mo["BuildNumber"]?.ToString() ?? "";
                    osArch = mo["OSArchitecture"]?.ToString() ?? "";
                    var bootStr = mo["LastBootUpTime"]?.ToString();
                    if (bootStr is not null)
                        bootTime = ManagementDateTimeConverter.ToDateTime(bootStr);
                }
                break;
            }
        }
        catch (ManagementException) { }

        string biosVersion = "Unknown", biosMfr = "";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion, Manufacturer FROM Win32_BIOS");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    biosVersion = mo["SMBIOSBIOSVersion"]?.ToString() ?? "Unknown";
                    biosMfr = mo["Manufacturer"]?.ToString() ?? "";
                }
                break;
            }
        }
        catch (ManagementException) { }

        string boardMfr = "", boardProduct = "";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    boardMfr = mo["Manufacturer"]?.ToString() ?? "";
                    boardProduct = mo["Product"]?.ToString() ?? "";
                }
                break;
            }
        }
        catch (ManagementException) { }

        string gpuName = "Unknown";
        string gpuDriver = "";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    gpuName = mo["Name"]?.ToString()?.Trim() ?? "Unknown";
                    gpuDriver = mo["DriverVersion"]?.ToString() ?? "";
                }
                break; // primary GPU
            }
        }
        catch (ManagementException) { }

        string boardModel = string.IsNullOrWhiteSpace(boardProduct)
            ? boardMfr
            : $"{boardMfr} {boardProduct}".Trim();

        string bios = string.IsNullOrWhiteSpace(biosMfr)
            ? biosVersion
            : $"{biosMfr} {biosVersion}".Trim();

        return new SystemStaticInfo(
            CpuName: cpuName,
            CpuCores: cpuCores,
            CpuThreads: cpuThreads,
            TotalRamBytes: totalRam,
            OsCaption: $"{osCaption} {osArch}".Trim(),
            OsBuild: $"{osVersion} (Build {osBuild})",
            BiosVersion: bios,
            BoardModel: boardModel,
            GpuName: gpuName,
            BootTime: bootTime);
    }

    // ── Timer callback ──────────────────────────────────────────────

    private void OnTick(object? state)
    {
        if (_disposed) return;

        _tickCount++;

        bool isDiskSpaceTick = _tickCount % 2 == 0;  // every 60s
        bool isSmartTick = _tickCount % 10 == 0;      // every 5 min

        float? temp = QueryThermalZone();
        var battery = QueryBattery();
        var drives = QueryDrives(isDiskSpaceTick, isSmartTick);

        var snapshot = new DynamicHwInfo(DateTime.UtcNow, temp, battery, drives);

        lock (_lock) _dynamic = snapshot;

        Updated?.Invoke();
    }

    // ── Thermal ─────────────────────────────────────────────────────

    private static float? QueryThermalZone()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    uint raw = Convert.ToUInt32(mo["CurrentTemperature"]);
                    return (float)((raw / 10.0) - 273.15);
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { } // needs admin

        return null;
    }

    // ── Battery ─────────────────────────────────────────────────────

    private static BatteryHealth? QueryBattery()
    {
        uint designCap = 0, fullChargeCap = 0, remainingCap = 0;
        uint cycleCount = 0, voltage = 0;
        bool isCharging = false, isDischarging = false, isPowerOnline = false;
        string mfrName = "";
        bool hasBattery = false;

        // BatteryStaticData
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT DesignedCapacity, ManufactureName FROM BatteryStaticData");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    hasBattery = true;
                    designCap = Convert.ToUInt32(mo["DesignedCapacity"]);
                    mfrName = mo["ManufactureName"]?.ToString() ?? "";
                }
                break;
            }
        }
        catch (ManagementException) { }

        if (!hasBattery) return null;

        // BatteryFullChargedCapacity
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    fullChargeCap = Convert.ToUInt32(mo["FullChargedCapacity"]);
                }
                break;
            }
        }
        catch (ManagementException) { }

        // BatteryStatus
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT Charging, Discharging, PowerOnline, RemainingCapacity, Voltage FROM BatteryStatus");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    isCharging = Convert.ToBoolean(mo["Charging"]);
                    isDischarging = Convert.ToBoolean(mo["Discharging"]);
                    isPowerOnline = Convert.ToBoolean(mo["PowerOnline"]);
                    remainingCap = Convert.ToUInt32(mo["RemainingCapacity"]);
                    voltage = Convert.ToUInt32(mo["Voltage"]);
                }
                break;
            }
        }
        catch (ManagementException) { }

        // BatteryCycleCount
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CycleCount FROM BatteryCycleCount");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    cycleCount = Convert.ToUInt32(mo["CycleCount"]);
                }
                break;
            }
        }
        catch (ManagementException) { }

        float chargePercent = fullChargeCap > 0
            ? Math.Clamp((float)remainingCap / fullChargeCap * 100f, 0f, 100f)
            : 0f;

        float degradation = designCap > 0
            ? Math.Clamp((1f - (float)fullChargeCap / designCap) * 100f, 0f, 100f)
            : 0f;

        return new BatteryHealth(
            ChargePercent: chargePercent,
            DesignCapacityMWh: designCap,
            FullChargeCapacityMWh: fullChargeCap,
            DegradationPercent: degradation,
            CycleCount: cycleCount,
            IsCharging: isCharging,
            IsAcConnected: isPowerOnline);
    }

    // ── Drives ──────────────────────────────────────────────────────

    private List<DriveHealth> _cachedDrives = new();
    private Dictionary<string, (long Free, long Total)> _cachedDiskSpace = new();
    private Dictionary<string, bool?> _cachedSmart = new();

    private IReadOnlyList<DriveHealth> QueryDrives(bool refreshSpace, bool refreshSmart)
    {
        // Physical disks (always refreshed on the 30s tick)
        var physicalDisks = QueryPhysicalDisks();

        // Disk space (every 60s)
        if (refreshSpace || _cachedDiskSpace.Count == 0)
            _cachedDiskSpace = QueryDiskSpace();

        // S.M.A.R.T. (every 5 min)
        if (refreshSmart || _cachedSmart.Count == 0)
            _cachedSmart = QuerySmartStatus();

        var results = new List<DriveHealth>();

        foreach (var disk in physicalDisks)
        {
            _cachedDiskSpace.TryGetValue(disk.DriveLetter, out var space);
            _cachedSmart.TryGetValue(disk.Model, out var predictFailure);

            results.Add(new DriveHealth(
                Name: disk.DriveLetter.Length > 0 ? disk.DriveLetter : disk.FriendlyName,
                Model: disk.FriendlyName,
                MediaType: disk.MediaType,
                BusType: disk.BusType,
                SizeBytes: disk.SizeBytes,
                FreeBytes: space.Free,
                HealthStatus: disk.HealthStatus,
                PredictFailure: predictFailure));
        }

        _cachedDrives = results;
        return results;
    }

    private record PhysicalDisk(
        string FriendlyName, string Model, string HealthStatus, string MediaType,
        string BusType, long SizeBytes, string DriveLetter);

    private static List<PhysicalDisk> QueryPhysicalDisks()
    {
        var disks = new List<PhysicalDisk>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, HealthStatus, MediaType, BusType, Size FROM MSFT_PhysicalDisk");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string name = mo["FriendlyName"]?.ToString() ?? "Unknown";
                    ushort health = Convert.ToUInt16(mo["HealthStatus"]);
                    ushort media = Convert.ToUInt16(mo["MediaType"]);
                    ushort bus = Convert.ToUInt16(mo["BusType"]);
                    long size = Convert.ToInt64(mo["Size"]);

                    string healthStr = health switch
                    {
                        0 => "Healthy",
                        1 => "Warning",
                        2 => "Unhealthy",
                        _ => $"Unknown ({health})"
                    };

                    string mediaStr = media switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        _ => $"Unknown ({media})"
                    };

                    string busStr = bus switch
                    {
                        11 => "SATA",
                        17 => "NVMe",
                        _ => $"Other ({bus})"
                    };

                    disks.Add(new PhysicalDisk(name, name, healthStr, mediaStr, busStr, size, ""));
                }
            }
        }
        catch (ManagementException) { }

        return disks;
    }

    private static Dictionary<string, (long Free, long Total)> QueryDiskSpace()
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                result[drive.Name] = (drive.AvailableFreeSpace, drive.TotalSize);
            }
        }
        catch (Exception) { }

        return result;
    }

    // ── S.M.A.R.T. ─────────────────────────────────────────────────

    private static Dictionary<string, bool?> QuerySmartStatus()
    {
        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string instance = mo["InstanceName"]?.ToString() ?? "";
                    bool predict = Convert.ToBoolean(mo["PredictFailure"]);
                    if (instance.Length > 0)
                        result[instance] = predict;
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { } // needs admin

        return result;
    }

    // ── Dispose ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
