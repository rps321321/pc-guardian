using Microsoft.Data.Sqlite;

namespace PCGuardian;

internal sealed record ProgramRow(
    int Id, string Name, string? Path, string? Company,
    string Category, string FirstSeen, string LastSeen);

internal sealed record SessionRow(
    int Id, int ProgramId, int Pid, string Started, string? Ended, double? PeakMemoryMb);

internal sealed record ActiveSessionRow(
    int SessionId, int ProgramId, int Pid, string Started,
    string ProgramName, string? ProgramPath, string? Company, string Category);

internal sealed record TimelineEvent(
    string Timestamp, string EventType, string ProgramName,
    string? ProgramPath, int Pid, double? PeakMemoryMb);

internal sealed record ScanHistoryRow(
    int Id, string Timestamp, string Overall,
    int SafeCount, int WarningCount, int DangerCount, string? DetailsJson);

internal sealed record HardwareMetricRow(
    int Id, string Timestamp, double? CpuTemp, double? CpuLoad,
    double? GpuTemp, double? GpuLoad, double? FanRpm,
    double? CpuPower, double? BatteryLevel, double? BatteryHealth,
    string? TopProcesses);

internal sealed record SecurityEventRow(int Id, string Timestamp, string Source, string EventType, string Severity, string? Detail, string? RawData);

internal sealed record DiskSpaceRow(int Id, string Timestamp, string Drive, double TotalGb, double FreeGb);

internal sealed class Database : IDisposable
{
    private readonly string _connStr;
    private bool _disposed;

    public Database()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PCGuardian");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "activity.db");
        _connStr = $"Data Source={dbPath}";
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        using var pragma1 = c.CreateCommand();
        pragma1.CommandText = "PRAGMA journal_mode=WAL;";
        pragma1.ExecuteNonQuery();
        using var pragma2 = c.CreateCommand();
        pragma2.CommandText = "PRAGMA synchronous=NORMAL;";
        pragma2.ExecuteNonQuery();
        return c;
    }

    public void Initialize()
    {
        using var db = Open();
        string[] statements =
        [
            """
            CREATE TABLE IF NOT EXISTS programs (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                name      TEXT NOT NULL,
                path      TEXT,
                company   TEXT,
                category  TEXT NOT NULL DEFAULT 'Other',
                first_seen TEXT NOT NULL,
                last_seen  TEXT NOT NULL,
                UNIQUE(name, path)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS sessions (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                program_id     INTEGER NOT NULL REFERENCES programs(id),
                pid            INTEGER NOT NULL,
                started        TEXT NOT NULL,
                ended          TEXT,
                peak_memory_mb REAL
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS scan_history (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp      TEXT NOT NULL,
                overall        TEXT NOT NULL,
                safe_count     INTEGER,
                warning_count  INTEGER,
                danger_count   INTEGER,
                details_json   TEXT
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS connections_log (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp      TEXT NOT NULL,
                process_name   TEXT,
                remote_address TEXT,
                remote_port    INTEGER
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS hardware_metrics (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp      TEXT NOT NULL,
                cpu_temp       REAL,
                cpu_load       REAL,
                gpu_temp       REAL,
                gpu_load       REAL,
                fan_rpm        REAL,
                cpu_power      REAL,
                battery_level  REAL,
                battery_health REAL,
                top_processes  TEXT
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS security_events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp  TEXT NOT NULL,
                source     TEXT NOT NULL,
                event_type TEXT NOT NULL,
                severity   TEXT NOT NULL,
                detail     TEXT,
                raw_data   TEXT
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS disk_space_log (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp  TEXT NOT NULL,
                drive      TEXT NOT NULL,
                total_gb   REAL,
                free_gb    REAL
            )
            """,
            "CREATE INDEX IF NOT EXISTS idx_sessions_pid_ended ON sessions(pid, ended)",
            "CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started)",
            "CREATE INDEX IF NOT EXISTS idx_scan_history_ts ON scan_history(timestamp)",
            "CREATE INDEX IF NOT EXISTS idx_hw_ts ON hardware_metrics(timestamp)",
            "CREATE INDEX IF NOT EXISTS idx_secevt_ts ON security_events(timestamp)",
            "CREATE INDEX IF NOT EXISTS idx_diskspace_ts ON disk_space_log(timestamp)",
        ];

        foreach (var sql in statements)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    // ── Programs ────────────────────────────────────────────────────

    public int UpsertProgram(string name, string? path, string? company, string category = "Other")
    {
        var now = DateTime.UtcNow.ToString("o");
        using var db = Open();

        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO programs (name, path, company, category, first_seen, last_seen)
            VALUES (@name, @path, @company, @cat, @now, @now)
            ON CONFLICT(name, path) DO UPDATE SET
                company   = COALESCE(@company, company),
                category  = @cat,
                last_seen = @now;
            """;
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
        insert.Parameters.AddWithValue("@company", (object?)company ?? DBNull.Value);
        insert.Parameters.AddWithValue("@cat", category);
        insert.Parameters.AddWithValue("@now", now);
        insert.ExecuteNonQuery();

        using var select = db.CreateCommand();
        select.CommandText = "SELECT id FROM programs WHERE name = @name AND path IS @path;";
        select.Parameters.AddWithValue("@name", name);
        select.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
        return Convert.ToInt32(select.ExecuteScalar());
    }

    public List<ProgramRow> GetAllPrograms()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, path, company, category, first_seen, last_seen FROM programs ORDER BY last_seen DESC;";
        using var r = cmd.ExecuteReader();
        var list = new List<ProgramRow>();
        while (r.Read())
            list.Add(new ProgramRow(
                r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6)));
        return list;
    }

    // ── Sessions ────────────────────────────────────────────────────

    public void StartSession(int programId, int pid, DateTime startTime)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (program_id, pid, started) VALUES (@pid, @procPid, @t);";
        cmd.Parameters.AddWithValue("@pid", programId);
        cmd.Parameters.AddWithValue("@procPid", pid);
        cmd.Parameters.AddWithValue("@t", startTime.ToUniversalTime().ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void EndSession(int pid, DateTime endTime, double peakMemoryMb)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET ended = @t, peak_memory_mb = MAX(COALESCE(peak_memory_mb, 0), @mem)
            WHERE id = (
                SELECT id FROM sessions WHERE pid = @pid AND ended IS NULL
                ORDER BY started DESC LIMIT 1
            );
            """;
        cmd.Parameters.AddWithValue("@pid", pid);
        cmd.Parameters.AddWithValue("@t", endTime.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@mem", peakMemoryMb);
        cmd.ExecuteNonQuery();
    }

    public List<ActiveSessionRow> GetActiveSessionsWithPrograms()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.program_id, s.pid, s.started,
                   p.name, p.path, p.company, p.category
            FROM sessions s JOIN programs p ON s.program_id = p.id
            WHERE s.ended IS NULL
            ORDER BY s.started DESC;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<ActiveSessionRow>();
        while (r.Read())
            list.Add(new ActiveSessionRow(
                r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3),
                r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7)));
        return list;
    }

    public List<TimelineEvent> GetRecentTimeline(int limit = 200)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT ts, event_type, name, path, pid, peak_memory_mb FROM (
                SELECT s.started AS ts, 'start' AS event_type, p.name, p.path, s.pid, NULL AS peak_memory_mb
                FROM sessions s JOIN programs p ON s.program_id = p.id
                UNION ALL
                SELECT s.ended AS ts, 'end' AS event_type, p.name, p.path, s.pid, s.peak_memory_mb
                FROM sessions s JOIN programs p ON s.program_id = p.id
                WHERE s.ended IS NOT NULL
            ) ORDER BY ts DESC LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<TimelineEvent>();
        while (r.Read())
            list.Add(new TimelineEvent(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetDouble(5)));
        return list;
    }

    // ── Scan History ────────────────────────────────────────────────

    public void SaveScanResult(DateTime timestamp, string overall,
        int safeCount, int warningCount, int dangerCount, string? detailsJson)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scan_history (timestamp, overall, safe_count, warning_count, danger_count, details_json)
            VALUES (@ts, @ov, @s, @w, @d, @j);
            """;
        cmd.Parameters.AddWithValue("@ts", timestamp.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@ov", overall);
        cmd.Parameters.AddWithValue("@s", safeCount);
        cmd.Parameters.AddWithValue("@w", warningCount);
        cmd.Parameters.AddWithValue("@d", dangerCount);
        cmd.Parameters.AddWithValue("@j", (object?)detailsJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<ScanHistoryRow> GetScanHistory(int limit = 50)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, overall, safe_count, warning_count, danger_count, details_json FROM scan_history ORDER BY timestamp DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ScanHistoryRow>();
        while (r.Read())
            list.Add(new ScanHistoryRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // ── Connections ─────────────────────────────────────────────────

    public void LogConnections(List<(string ProcessName, string RemoteAddress, int RemotePort)> entries)
    {
        if (entries.Count == 0) return;
        var now = DateTime.UtcNow.ToString("o");
        using var db = Open();
        using var tx = db.BeginTransaction();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO connections_log (timestamp, process_name, remote_address, remote_port) VALUES (@ts, @pn, @ra, @rp);";
            var pTs = cmd.Parameters.Add("@ts", SqliteType.Text);
            var pPn = cmd.Parameters.Add("@pn", SqliteType.Text);
            var pRa = cmd.Parameters.Add("@ra", SqliteType.Text);
            var pRp = cmd.Parameters.Add("@rp", SqliteType.Integer);

            foreach (var (processName, remoteAddr, remotePort) in entries)
            {
                pTs.Value = now;
                pPn.Value = processName;
                pRa.Value = remoteAddr;
                pRp.Value = remotePort;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ── Convenience overloads for ProcessMonitor ────────────────────

    public void StartSession(string programName, int pid, DateTime startTime, double memoryMb)
    {
        // Look up program ID by name, then start session
        using var db = Open();
        using var lookup = db.CreateCommand();
        lookup.CommandText = "SELECT id FROM programs WHERE name = @name ORDER BY last_seen DESC LIMIT 1;";
        lookup.Parameters.AddWithValue("@name", programName);
        var result = lookup.ExecuteScalar();
        if (result == null) return;
        int programId = Convert.ToInt32(result);

        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (program_id, pid, started, peak_memory_mb) VALUES (@pgid, @pid, @t, @mem);";
        cmd.Parameters.AddWithValue("@pgid", programId);
        cmd.Parameters.AddWithValue("@pid", pid);
        cmd.Parameters.AddWithValue("@t", startTime.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@mem", memoryMb);
        cmd.ExecuteNonQuery();
    }

    public void EndSession(string programName, int pid, DateTime endTime)
    {
        EndSession(pid, endTime, 0);
    }

    // ── Query methods for ActivityForm (return string arrays) ─────

    public List<string[]> QueryActiveNow()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.name, p.category, s.pid, s.peak_memory_mb, s.started, COALESCE(p.path, '')
            FROM sessions s JOIN programs p ON s.program_id = p.id
            WHERE s.ended IS NULL
            ORDER BY s.started DESC;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<string[]>();
        while (r.Read())
        {
            var started = DateTime.TryParse(r.GetString(4), out var dt) ? dt.ToLocalTime().ToString("h:mm tt") : r.GetString(4);
            var mem = r.IsDBNull(3) ? "-" : $"{r.GetDouble(3):F0} MB";
            list.Add([r.GetString(0), r.GetString(1), r.GetInt32(2).ToString(), mem, started, r.GetString(5)]);
        }
        return list;
    }

    public List<string[]> QueryTimeline(int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT ts, event_type, name, category, pid, peak_memory_mb FROM (
                SELECT s.started AS ts, 'START' AS event_type, p.name, p.category, s.pid, s.peak_memory_mb
                FROM sessions s JOIN programs p ON s.program_id = p.id
                UNION ALL
                SELECT s.ended AS ts, 'END' AS event_type, p.name, p.category, s.pid, s.peak_memory_mb
                FROM sessions s JOIN programs p ON s.program_id = p.id
                WHERE s.ended IS NOT NULL
            ) ORDER BY ts DESC LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<string[]>();
        while (r.Read())
        {
            var ts = DateTime.TryParse(r.GetString(0), out var dt) ? dt.ToLocalTime().ToString("h:mm:ss tt") : r.GetString(0);
            var mem = r.IsDBNull(5) ? "-" : $"{r.GetDouble(5):F0} MB";
            list.Add([ts, r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4).ToString(), mem]);
        }
        return list;
    }

    public List<string[]> QueryPrograms()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.name, p.category, COALESCE(p.company, ''), p.first_seen, p.last_seen,
                   (SELECT COUNT(*) FROM sessions WHERE program_id = p.id) AS session_count
            FROM programs p ORDER BY p.last_seen DESC;
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<string[]>();
        while (r.Read())
        {
            var first = DateTime.TryParse(r.GetString(3), out var d1) ? d1.ToLocalTime().ToString("MMM d, h:mm tt") : r.GetString(3);
            var last = DateTime.TryParse(r.GetString(4), out var d2) ? d2.ToLocalTime().ToString("MMM d, h:mm tt") : r.GetString(4);
            list.Add([r.GetString(0), r.GetString(1), r.GetString(2), first, last, r.GetInt32(5).ToString()]);
        }
        return list;
    }

    public List<string[]> QueryScanHistory(int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT timestamp, overall, safe_count, warning_count, danger_count FROM scan_history ORDER BY timestamp DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<string[]>();
        while (r.Read())
        {
            var ts = DateTime.TryParse(r.GetString(0), out var dt) ? dt.ToLocalTime().ToString("MMM d, h:mm tt") : r.GetString(0);
            list.Add([ts, r.GetString(1), r.GetInt32(2).ToString(), r.GetInt32(3).ToString(), r.GetInt32(4).ToString()]);
        }
        return list;
    }

    // ── Hardware Metrics ──────────────────────────────────────────────

    public void LogHardwareMetrics(DateTime timestamp, float? cpuTemp, float? cpuLoad,
        float? gpuTemp, float? gpuLoad, float? fanRpm, float? cpuPower,
        float? batteryLevel, float? batteryHealth, string? topProcesses)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hardware_metrics (timestamp, cpu_temp, cpu_load, gpu_temp, gpu_load, fan_rpm, cpu_power, battery_level, battery_health, top_processes)
            VALUES (@ts, @ct, @cl, @gt, @gl, @fr, @cp, @bl, @bh, @tp);
            """;
        cmd.Parameters.AddWithValue("@ts", timestamp.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@ct", (object?)cpuTemp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cl", (object?)cpuLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gt", (object?)gpuTemp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gl", (object?)gpuLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fr", (object?)fanRpm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cp", (object?)cpuPower ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bl", (object?)batteryLevel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bh", (object?)batteryHealth ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tp", (object?)topProcesses ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<HardwareMetricRow> GetHardwareHistory(int hours = 24)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, cpu_temp, cpu_load, gpu_temp, gpu_load, fan_rpm, cpu_power, battery_level, battery_health, top_processes FROM hardware_metrics WHERE timestamp > datetime('now', '-' || @hours || ' hours') ORDER BY timestamp ASC;";
        cmd.Parameters.AddWithValue("@hours", hours);
        using var r = cmd.ExecuteReader();
        var list = new List<HardwareMetricRow>();
        while (r.Read())
            list.Add(new HardwareMetricRow(
                r.GetInt32(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                r.IsDBNull(5) ? null : r.GetDouble(5),
                r.IsDBNull(6) ? null : r.GetDouble(6),
                r.IsDBNull(7) ? null : r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetDouble(8),
                r.IsDBNull(9) ? null : r.GetDouble(9),
                r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }

    public List<HardwareMetricRow> GetCorrelatedHistory(int hours = 24)
    {
        return GetHardwareHistory(hours);
    }

    // ── Security Events ────────────────────────────────────────────

    public void LogSecurityEvent(DateTime timestamp, string source, string eventType,
        string severity, string? detail, string? rawData)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO security_events (timestamp, source, event_type, severity, detail, raw_data)
            VALUES (@ts, @src, @et, @sev, @det, @raw);
            """;
        cmd.Parameters.AddWithValue("@ts", timestamp.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@src", source);
        cmd.Parameters.AddWithValue("@et", eventType);
        cmd.Parameters.AddWithValue("@sev", severity);
        cmd.Parameters.AddWithValue("@det", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@raw", (object?)rawData ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<SecurityEventRow> GetSecurityEvents(int hours = 24)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, source, event_type, severity, detail, raw_data FROM security_events WHERE timestamp > datetime('now', '-' || @hours || ' hours') ORDER BY timestamp DESC;";
        cmd.Parameters.AddWithValue("@hours", hours);
        using var r = cmd.ExecuteReader();
        var list = new List<SecurityEventRow>();
        while (r.Read())
            list.Add(new SecurityEventRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    // ── Disk Space ──────────────────────────────────────────────────

    public void LogDiskSpace(DateTime timestamp, string drive, double totalGb, double freeGb)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO disk_space_log (timestamp, drive, total_gb, free_gb)
            VALUES (@ts, @drv, @total, @free);
            """;
        cmd.Parameters.AddWithValue("@ts", timestamp.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@drv", drive);
        cmd.Parameters.AddWithValue("@total", totalGb);
        cmd.Parameters.AddWithValue("@free", freeGb);
        cmd.ExecuteNonQuery();
    }

    public List<DiskSpaceRow> GetDiskSpaceHistory(int hours = 168)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, timestamp, drive, total_gb, free_gb FROM disk_space_log WHERE timestamp > datetime('now', '-' || @hours || ' hours') ORDER BY timestamp DESC;";
        cmd.Parameters.AddWithValue("@hours", hours);
        using var r = cmd.ExecuteReader();
        var list = new List<DiskSpaceRow>();
        while (r.Read())
            list.Add(new DiskSpaceRow(
                r.GetInt32(0), r.GetString(1), r.GetString(2),
                r.GetDouble(3), r.GetDouble(4)));
        return list;
    }

    // ── Maintenance ─────────────────────────────────────────────────

    /// <summary>User-triggered only. Deletes all logged data and reclaims space.</summary>
    public void PurgeAllData()
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        try
        {
            string[] tables =
            [
                "sessions",
                "programs",
                "scan_history",
                "connections_log",
                "hardware_metrics",
                "security_events",
                "disk_space_log",
            ];
            foreach (var table in tables)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        using var vacuum = db.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
    }

    public long GetDatabaseSizeBytes()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCGuardian");
        var dbPath = Path.Combine(dir, "activity.db");
        return File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
