namespace PCGuardian;

// ── Event types ─────────────────────────────────────────────────────────────

internal enum SecurityEventType
{
    ProcessStart,
    ProcessEnd,
    NetworkConnect,
    CpuSpike,
    GpuSpike,
    FileWrite,
    RegistryModify,
    ScheduledTaskCreate,
    UsbInsert,
    FailedLogin,
    ServiceInstall
}

internal sealed record SecurityEvent(
    DateTime Timestamp,
    SecurityEventType Type,
    string ProcessName,
    int Pid,
    Dictionary<string, object> Details);

// ── Alert record ────────────────────────────────────────────────────────────

internal sealed record CorrelationAlert(
    string RuleName,
    float Severity,
    string Description,
    List<SecurityEvent> EventChain,
    DateTime FirstEvent,
    DateTime LastEvent);

// ── Correlation rule ────────────────────────────────────────────────────────

internal sealed class CorrelationRule
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required float Severity { get; init; }
    public required TimeSpan Window { get; init; }
    public required List<Func<SecurityEvent, bool>> Predicates { get; init; }
}

// ── Engine ───────────────────────────────────────────────────────────────────

internal sealed class CorrelationEngine
{
    private const int MaxBufferSize = 10_000;

    private static readonly int[] MiningPorts = [3333, 4444, 5555, 8333, 9999, 14444, 45700];

    private readonly LinkedList<SecurityEvent> _buffer = new();
    private readonly List<CorrelationAlert> _activeAlerts = [];
    private readonly List<CorrelationRule> _rules;
    private readonly object _lock = new();

    public event Action<CorrelationAlert>? AlertFired;

    public CorrelationEngine()
    {
        _rules = BuildDefaultRules();
    }

    // ── Public API ───────────────────────────────────────────────────────

    public void Ingest(SecurityEvent evt)
    {
        lock (_lock)
        {
            _buffer.AddLast(evt);
            TrimBuffer();
            EvaluateRules(evt);
        }
    }

    public List<CorrelationAlert> GetActiveAlerts()
    {
        lock (_lock)
        {
            return [.. _activeAlerts];
        }
    }

    // ── Rule evaluation ──────────────────────────────────────────────────

    private void EvaluateRules(SecurityEvent latestEvent)
    {
        foreach (var rule in _rules)
        {
            var windowStart = latestEvent.Timestamp - rule.Window;

            var windowEvents = new List<SecurityEvent>();
            for (var node = _buffer.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Timestamp < windowStart) break;
                windowEvents.Add(node.Value);
            }

            windowEvents.Reverse();

            var chain = TryMatchGreedy(rule.Predicates, windowEvents);
            if (chain is null) continue;

            // avoid duplicate alerts for the exact same event chain
            if (IsDuplicateAlert(rule.Name, chain)) continue;

            var alert = new CorrelationAlert(
                RuleName: rule.Name,
                Severity: rule.Severity,
                Description: rule.Description,
                EventChain: chain,
                FirstEvent: chain[0].Timestamp,
                LastEvent: chain[^1].Timestamp);

            _activeAlerts.Add(alert);
            AlertFired?.Invoke(alert);
        }
    }

    /// <summary>
    /// Greedy forward match: scan events in order, consuming one distinct
    /// event per predicate. Returns the matched chain or null.
    /// </summary>
    private static List<SecurityEvent>? TryMatchGreedy(
        List<Func<SecurityEvent, bool>> predicates,
        List<SecurityEvent> events)
    {
        var chain = new List<SecurityEvent>(predicates.Count);
        var predicateIndex = 0;

        foreach (var evt in events)
        {
            if (predicateIndex >= predicates.Count) break;

            if (predicates[predicateIndex](evt))
            {
                chain.Add(evt);
                predicateIndex++;
            }
        }

        return predicateIndex == predicates.Count ? chain : null;
    }

    private bool IsDuplicateAlert(string ruleName, List<SecurityEvent> chain)
    {
        foreach (var existing in _activeAlerts)
        {
            if (existing.RuleName != ruleName) continue;
            if (existing.EventChain.Count != chain.Count) continue;

            var allSame = true;
            for (var i = 0; i < chain.Count; i++)
            {
                if (!ReferenceEquals(existing.EventChain[i], chain[i]))
                {
                    allSame = false;
                    break;
                }
            }

            if (allSame) return true;
        }

        return false;
    }

    private void TrimBuffer()
    {
        while (_buffer.Count > MaxBufferSize)
            _buffer.RemoveFirst();
    }

    // ── Default rules ────────────────────────────────────────────────────

    private static List<CorrelationRule> BuildDefaultRules() =>
    [
        // 1. Crypto Miner (severity 9)
        new CorrelationRule
        {
            Name = "Crypto Miner",
            Description = "Unsigned process start followed by CPU spike >70% and connection to a known mining port within 5 minutes.",
            Severity = 9f,
            Window = TimeSpan.FromMinutes(5),
            Predicates =
            [
                evt => evt.Type is SecurityEventType.ProcessStart
                       && IsUnsignedProcess(evt),

                evt => evt.Type is SecurityEventType.CpuSpike
                       && GetDetailFloat(evt, "CpuPercent") > 70f,

                evt => evt.Type is SecurityEventType.NetworkConnect
                       && IsMiningPort(evt)
            ]
        },

        // 2. Persistence (severity 7.5)
        new CorrelationRule
        {
            Name = "Persistence",
            Description = "Unsigned process launched from a temp folder followed by registry Run key modification within 10 minutes.",
            Severity = 7.5f,
            Window = TimeSpan.FromMinutes(10),
            Predicates =
            [
                evt => evt.Type is SecurityEventType.ProcessStart
                       && IsUnsignedProcess(evt)
                       && IsFromTempFolder(evt),

                evt => evt.Type is SecurityEventType.RegistryModify
                       && IsRunKeyModification(evt)
            ]
        },

        // 3. Lateral Movement (severity 8.5)
        new CorrelationRule
        {
            Name = "Lateral Movement",
            Description = "Multiple SMB connections (port 445) followed by PowerShell or cmd spawn within 15 minutes.",
            Severity = 8.5f,
            Window = TimeSpan.FromMinutes(15),
            Predicates =
            [
                evt => evt.Type is SecurityEventType.NetworkConnect
                       && GetDetailInt(evt, "Port") == 445,

                evt => evt.Type is SecurityEventType.NetworkConnect
                       && GetDetailInt(evt, "Port") == 445,

                evt => evt.Type is SecurityEventType.ProcessStart
                       && IsShellProcess(evt)
            ]
        },

        // 4. Credential Theft (severity 8)
        new CorrelationRule
        {
            Name = "Credential Theft",
            Description = "More than 5 failed logins followed by a new process start and network connection within 10 minutes.",
            Severity = 8f,
            Window = TimeSpan.FromMinutes(10),
            Predicates = BuildFailedLoginPredicates()
        }
    ];

    // ── Predicate helpers ────────────────────────────────────────────────

    private static bool IsUnsignedProcess(SecurityEvent evt) =>
        evt.Details.TryGetValue("IsSigned", out var val) && val is false;

    private static bool IsFromTempFolder(SecurityEvent evt)
    {
        if (!evt.Details.TryGetValue("Path", out var val) || val is not string path)
            return false;

        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Tmp\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\AppData\\Local\\Temp\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRunKeyModification(SecurityEvent evt)
    {
        if (!evt.Details.TryGetValue("KeyPath", out var val) || val is not string keyPath)
            return false;

        return keyPath.Contains(@"\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMiningPort(SecurityEvent evt)
    {
        var port = GetDetailInt(evt, "Port");
        return Array.IndexOf(MiningPorts, port) >= 0;
    }

    private static bool IsShellProcess(SecurityEvent evt)
    {
        var name = evt.ProcessName;
        return name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static float GetDetailFloat(SecurityEvent evt, string key)
    {
        if (!evt.Details.TryGetValue(key, out var val)) return 0f;
        return val switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            _ => float.TryParse(val.ToString(), out var parsed) ? parsed : 0f
        };
    }

    private static int GetDetailInt(SecurityEvent evt, string key)
    {
        if (!evt.Details.TryGetValue(key, out var val)) return 0;
        return val switch
        {
            int i => i,
            long l => (int)l,
            float f => (int)f,
            double d => (int)d,
            _ => int.TryParse(val.ToString(), out var parsed) ? parsed : 0
        };
    }

    /// <summary>
    /// Builds predicates for credential theft: 5x failed login + process start + network connect.
    /// </summary>
    private static List<Func<SecurityEvent, bool>> BuildFailedLoginPredicates()
    {
        var predicates = new List<Func<SecurityEvent, bool>>(7);

        // 5 distinct failed login events
        for (var i = 0; i < 5; i++)
            predicates.Add(evt => evt.Type is SecurityEventType.FailedLogin);

        // followed by a new process start
        predicates.Add(evt => evt.Type is SecurityEventType.ProcessStart);

        // followed by a network connection
        predicates.Add(evt => evt.Type is SecurityEventType.NetworkConnect);

        return predicates;
    }
}
