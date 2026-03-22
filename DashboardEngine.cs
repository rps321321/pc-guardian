namespace PCGuardian;

// ── Supporting types ────────────────────────────────────────────────────────

internal enum EventSeverity { Info, Success, Warning, Danger }
internal enum TrendDirection { Improving, Stable, Degrading }

internal sealed record ActivityEvent(DateTime Time, string Message, EventSeverity Severity);

internal sealed record Recommendation(
    string Id, string Title, string Category,
    float Impact, float Effort, float Urgency,
    int DismissCount, bool HasFix);

internal sealed record CategoryTileData(
    string Id, string Label, string Icon, Status Status, string Summary);

internal sealed record DashboardState(
    float ThreatScore,
    string Grade,
    TrendDirection Trend,
    float TrendDelta,
    float CpuPercent, float GpuPercent, uint RamPercent, float DiskMBps,
    int SecurityPassed, int SecurityWarnings, int SecurityDangers, int SecurityTotal,
    IReadOnlyList<CategoryTileData> Tiles,
    IReadOnlyList<ActivityEvent> RecentActivity,
    IReadOnlyList<Recommendation> TopRecommendations,
    string SystemSummary,
    DateTime? LastScanTime);

// ── Engine ──────────────────────────────────────────────────────────────────

internal sealed class DashboardEngine : IDisposable
{
    const int MaxActivityEvents = 50;
    const float DefaultUncertainScore = 65f;
    const float TimeDecayHalfLifeHours = 168f; // 7 days

    static readonly Dictionary<string, float> CategoryBasePenalties = new()
    {
        ["rdp"]               = 15f,
        ["remote-apps"]       = 15f,
        ["ports"]             = 12f,
        ["connections"]       = 7f,
        ["shares"]            = 10f,
        ["services"]          = 10f,
        ["firewall"]          = 9f,
        ["users"]             = 10f,
        ["startup"]           = 6f,
        ["tasks"]             = 10f,
        ["antivirus"]         = 8f,
        ["dns"]               = 7f,
        ["usb"]               = 5f,
        ["hardware"]          = 6f,
        ["security-posture"]  = 10f,
        ["security-events"]   = 8f,
    };

    readonly object _lock = new();
    readonly SystemMonitor? _monitor;
    readonly Database? _db;
    readonly ProcessMonitor? _procMonitor;

    readonly List<ActivityEvent> _activity = new();
    readonly Dictionary<string, int> _dismissCounts = new();

    Report? _lastReport;
    DateTime? _lastScanTime;
    DateTime _highCpuStart = DateTime.MinValue;
    bool _disposed;

    /// <summary>Fires when a new <see cref="DashboardState"/> is computed.</summary>
    public event Action<DashboardState>? StateChanged;

    // ── Constructor ─────────────────────────────────────────────────────

    public DashboardEngine(SystemMonitor? monitor, Database? db, ProcessMonitor? procMonitor)
    {
        _monitor = monitor;
        _db = db;
        _procMonitor = procMonitor;

        if (_monitor is not null)
            _monitor.Updated += OnMonitorUpdated;
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>Compute and return the current dashboard state.</summary>
    public DashboardState GetState()
    {
        lock (_lock)
            return BuildState();
    }

    /// <summary>Ingest a completed scan report and refresh state.</summary>
    public void IngestScanResult(Report report)
    {
        lock (_lock)
        {
            _lastReport = report;
            _lastScanTime = report.Timestamp;
        }

        AddActivity("Security scan completed", EventSeverity.Success);
        NotifyStateChanged();
    }

    /// <summary>Add an event to the activity feed.</summary>
    public void AddActivity(string message, EventSeverity severity)
    {
        lock (_lock)
        {
            _activity.Insert(0, new ActivityEvent(DateTime.UtcNow, message, severity));
            if (_activity.Count > MaxActivityEvents)
                _activity.RemoveAt(_activity.Count - 1);
        }
    }

    /// <summary>Dismiss a recommendation, reducing its future priority.</summary>
    public void DismissRecommendation(string id)
    {
        lock (_lock)
        {
            _dismissCounts[id] = _dismissCounts.GetValueOrDefault(id) + 1;
        }
    }

    // ── Event handler ───────────────────────────────────────────────────

    void OnMonitorUpdated()
    {
        if (_disposed) return;
        NotifyStateChanged();
    }

    void NotifyStateChanged()
    {
        DashboardState state;
        lock (_lock)
            state = BuildState();

        try { StateChanged?.Invoke(state); }
        catch { /* subscriber errors must not crash the engine */ }
    }

    // ── Core state builder ──────────────────────────────────────────────

    DashboardState BuildState()
    {
        // Performance metrics
        var perf = _monitor?.GetLatestPerf();
        float cpuPercent = perf?.CpuPercent ?? 0f;
        float gpuPercent = perf?.GpuPercent ?? 0f;
        uint ramPercent = perf?.RamUsedPercent ?? 0;
        float diskMBps = perf is not null
            ? (perf.DiskReadBps + perf.DiskWriteBps) / (1024f * 1024f)
            : 0f;

        // Threat score
        float cts = ComputeCompositeThreatScore();
        cts = ApplyLiveAdjustments(cts, cpuPercent, gpuPercent);
        cts = Math.Clamp(cts, 0f, 100f);

        string grade = ScoreToGrade(cts);

        // Trend
        var (trend, trendDelta) = ComputeTrend(cts);

        // Security posture
        var (passed, warnings, dangers, total) = ComputeSecurityPosture();

        // Tiles
        var tiles = BuildTiles();

        // Activity (snapshot)
        var recentActivity = _activity.ToList().AsReadOnly();

        // Recommendations
        var recommendations = BuildRecommendations();

        // System summary
        string summary = BuildSystemSummary();

        return new DashboardState(
            ThreatScore: cts,
            Grade: grade,
            Trend: trend,
            TrendDelta: trendDelta,
            CpuPercent: cpuPercent,
            GpuPercent: gpuPercent,
            RamPercent: ramPercent,
            DiskMBps: diskMBps,
            SecurityPassed: passed,
            SecurityWarnings: warnings,
            SecurityDangers: dangers,
            SecurityTotal: total,
            Tiles: tiles,
            RecentActivity: recentActivity,
            TopRecommendations: recommendations,
            SystemSummary: summary,
            LastScanTime: _lastScanTime);
    }

    // ── Composite Threat Score ──────────────────────────────────────────

    float ComputeCompositeThreatScore()
    {
        if (_lastReport is null)
            return DefaultUncertainScore;

        float penalty = 0f;
        double hoursSinceScan = (DateTime.UtcNow - _lastReport.Timestamp).TotalHours;
        float timeDecay = (float)Math.Pow(2.0, -hoursSinceScan / TimeDecayHalfLifeHours);

        foreach (var cat in _lastReport.Categories)
        {
            if (cat.Status == Status.Safe)
                continue;

            if (!CategoryBasePenalties.TryGetValue(cat.Id, out float basePenalty))
                basePenalty = 5f; // unknown category fallback

            // Danger gets full penalty, Warning gets half
            float effectivePenalty = cat.Status == Status.Danger
                ? basePenalty
                : basePenalty * 0.5f;

            penalty += effectivePenalty * timeDecay;
        }

        return 100f - penalty;
    }

    float ApplyLiveAdjustments(float score, float cpu, float gpu)
    {
        // CPU sustained >80% for >5 min
        if (cpu > 80f)
        {
            if (_highCpuStart == DateTime.MinValue)
                _highCpuStart = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _highCpuStart).TotalMinutes > 5)
                score -= 5f;
        }
        else
        {
            _highCpuStart = DateTime.MinValue;
        }

        // Unknown process with high GPU
        var snapshot = _monitor?.GetSnapshot();
        if (snapshot is not null && gpu > 70f)
        {
            var (isSuspicious, _) = _monitor!.CheckForMining();
            if (isSuspicious)
                score -= 10f;
        }

        // Failed logins in last hour (from security events in DB)
        if (_db is not null)
        {
            try
            {
                var recentEvents = _db.GetSecurityEvents(hours: 1);
                bool hasFailedLogin = recentEvents.Exists(
                    e => e.EventType.Contains("FailedLogin", StringComparison.OrdinalIgnoreCase)
                      || e.EventType.Contains("4625", StringComparison.Ordinal));
                if (hasFailedLogin)
                    score -= 10f;
            }
            catch { /* DB access failure should not break scoring */ }
        }

        // Pending Windows updates — check via security posture
        var posture = _monitor?.GetSecurityPosture();
        if (posture is not null && posture.RebootPending)
            score -= 5f;

        // Fan stopped (admin only)
        if (AdminHelper.IsAdmin() && snapshot is not null)
        {
            if (snapshot.CpuFanRpm is not null && snapshot.CpuFanRpm <= 0f)
                score -= 3f;
        }

        return score;
    }

    // ── Grade ───────────────────────────────────────────────────────────

    static string ScoreToGrade(float score) => score switch
    {
        >= 95f => "A+",
        >= 85f => "A",
        >= 75f => "B+",
        >= 70f => "B",
        >= 60f => "C",
        >= 50f => "D",
        _      => "F",
    };

    // ── Trend ───────────────────────────────────────────────────────────

    (TrendDirection Direction, float Delta) ComputeTrend(float currentScore)
    {
        if (_db is null)
            return (TrendDirection.Stable, 0f);

        try
        {
            var history = _db.GetScanHistory(limit: 10);
            if (history.Count < 2)
                return (TrendDirection.Stable, 0f);

            // Find a scan from ~24h ago
            var cutoff = DateTime.UtcNow.AddHours(-24);
            ScanHistoryRow? oldScan = null;
            foreach (var row in history)
            {
                if (DateTime.TryParse(row.Timestamp, out var ts) && ts < cutoff)
                {
                    oldScan = row;
                    break;
                }
            }

            if (oldScan is null)
                return (TrendDirection.Stable, 0f);

            // Reconstruct old score from safe/warning/danger counts
            int oldScore = 100 - (oldScan.DangerCount * 15) - (oldScan.WarningCount * 5);
            oldScore = Math.Clamp(oldScore, 0, 100);

            float delta = currentScore - oldScore;

            var direction = delta >= 3f  ? TrendDirection.Improving
                         : delta <= -3f ? TrendDirection.Degrading
                         : TrendDirection.Stable;

            return (direction, delta);
        }
        catch
        {
            return (TrendDirection.Stable, 0f);
        }
    }

    // ── Security Posture ────────────────────────────────────────────────

    (int Passed, int Warnings, int Dangers, int Total) ComputeSecurityPosture()
    {
        if (_lastReport is null)
            return (0, 0, 0, 0);

        int passed = 0, warnings = 0, dangers = 0;
        foreach (var cat in _lastReport.Categories)
        {
            switch (cat.Status)
            {
                case Status.Safe:    passed++;  break;
                case Status.Warning: warnings++; break;
                case Status.Danger:  dangers++;  break;
            }
        }

        return (passed, warnings, dangers, passed + warnings + dangers);
    }

    // ── Tiles ───────────────────────────────────────────────────────────

    IReadOnlyList<CategoryTileData> BuildTiles()
    {
        if (_lastReport is null)
            return Array.Empty<CategoryTileData>();

        var tiles = new List<CategoryTileData>(_lastReport.Categories.Count);
        foreach (var cat in _lastReport.Categories)
        {
            tiles.Add(new CategoryTileData(
                cat.Id, cat.Title, cat.Icon, cat.Status, cat.Summary));
        }
        return tiles;
    }

    // ── Recommendations ─────────────────────────────────────────────────

    IReadOnlyList<Recommendation> BuildRecommendations()
    {
        if (_lastReport is null)
            return Array.Empty<Recommendation>();

        var candidates = new List<Recommendation>();

        foreach (var cat in _lastReport.Categories)
        {
            if (cat.Status == Status.Safe)
                continue;

            foreach (var finding in cat.Findings)
            {
                if (finding.Status == Status.Safe)
                    continue;

                string recId = $"{cat.Id}:{finding.Label}";
                int dismissCount = _dismissCounts.GetValueOrDefault(recId);

                float impact = finding.Status == Status.Danger ? 9f : 5f;
                float urgency = finding.Status == Status.Danger ? 9f : 4f;
                float effort = EstimateEffort(cat.Id);
                bool hasFix = HasAutomatedFix(cat.Id);

                candidates.Add(new Recommendation(
                    Id: recId,
                    Title: finding.Label,
                    Category: cat.Title,
                    Impact: impact,
                    Effort: effort,
                    Urgency: urgency,
                    DismissCount: dismissCount,
                    HasFix: hasFix));
            }
        }

        // Score and sort
        var scored = candidates
            .Select(r => (Rec: r, Priority: ComputeRecommendationPriority(r)))
            .OrderByDescending(x => x.Priority)
            .ToList();

        // Category diversity: max 2 per category, pick top 3
        var result = new List<Recommendation>();
        var categoryCounts = new Dictionary<string, int>();

        foreach (var (rec, _) in scored)
        {
            int catCount = categoryCounts.GetValueOrDefault(rec.Category);
            if (catCount >= 2)
                continue;

            categoryCounts[rec.Category] = catCount + 1;
            result.Add(rec);

            if (result.Count >= 3)
                break;
        }

        return result;
    }

    static float ComputeRecommendationPriority(Recommendation r)
    {
        float fatigueFactor = MathF.Max(MathF.Pow(0.5f, r.DismissCount), 0.1f);
        return (r.Impact * 0.4f + r.Urgency * 0.4f) * ((10f - r.Effort) / 10f) * fatigueFactor;
    }

    static float EstimateEffort(string categoryId) => categoryId switch
    {
        "firewall"          => 3f,
        "antivirus"         => 2f,
        "rdp"               => 4f,
        "remote-apps"       => 5f,
        "security-posture"  => 6f,
        "users"             => 5f,
        _                   => 4f,
    };

    static bool HasAutomatedFix(string categoryId) => categoryId is
        "firewall" or "rdp" or "antivirus" or "startup" or "dns";

    // ── System Summary ──────────────────────────────────────────────────

    string BuildSystemSummary()
    {
        if (_monitor is null)
            return "System info unavailable";

        try
        {
            var info = _monitor.GetStaticInfo();
            string cpuShort = ShortenCpuName(info.CpuName);
            string ram = FormatRam(info.TotalRamBytes);
            string os = ShortenOsName(info.OsCaption);
            string uptime = FormatUptime(info.BootTime);

            return $"{cpuShort} \u00b7 {ram} \u00b7 {os} \u00b7 Up {uptime}";
        }
        catch
        {
            return "System info unavailable";
        }
    }

    static string ShortenCpuName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Unknown CPU";

        // Strip common fluff: "(R)", "(TM)", "CPU @", "Processor"
        var short_ = name
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("CPU", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", "", StringComparison.OrdinalIgnoreCase);

        // Collapse whitespace
        short_ = System.Text.RegularExpressions.Regex.Replace(short_.Trim(), @"\s+", " ");

        // Truncate if still long
        if (short_.Length > 25)
            short_ = short_[..25].TrimEnd();

        return short_;
    }

    static string FormatRam(ulong totalBytes)
    {
        double gb = totalBytes / (1024.0 * 1024 * 1024);
        return gb >= 1 ? $"{gb:F0} GB" : $"{totalBytes / (1024.0 * 1024):F0} MB";
    }

    static string ShortenOsName(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return "Windows";

        // "Microsoft Windows 11 Pro" => "Windows 11"
        var cleaned = caption
            .Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        // Drop edition (Pro, Home, Enterprise, etc.) for brevity
        int editionIdx = cleaned.IndexOf(" Pro", StringComparison.OrdinalIgnoreCase);
        if (editionIdx < 0) editionIdx = cleaned.IndexOf(" Home", StringComparison.OrdinalIgnoreCase);
        if (editionIdx < 0) editionIdx = cleaned.IndexOf(" Enterprise", StringComparison.OrdinalIgnoreCase);
        if (editionIdx > 0) cleaned = cleaned[..editionIdx];

        return cleaned;
    }

    static string FormatUptime(DateTime bootTime)
    {
        var span = DateTime.UtcNow - bootTime;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalMinutes}m";
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_monitor is not null)
            _monitor.Updated -= OnMonitorUpdated;
    }
}
