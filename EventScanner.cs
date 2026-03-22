using System.Diagnostics.Eventing.Reader;

namespace PCGuardian;

internal sealed class EventScanner
{
    const int MaxRecords = 5000;
    const int BatchSize = 100;

    // -------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------

    internal List<Finding> GetAllFindings(
        int securityDaysBack = 7, int systemDaysBack = 30)
    {
        var findings = new List<Finding>();

        findings.AddRange(ScanFailedLogins(securityDaysBack));
        findings.AddRange(ScanAccountLockouts(securityDaysBack));
        findings.AddRange(ScanBsodCrashes(systemDaysBack));
        findings.AddRange(ScanAppCrashes(systemDaysBack));
        findings.AddRange(ScanUnexpectedShutdowns(systemDaysBack));
        findings.AddRange(ScanPowerHistory(systemDaysBack));
        findings.AddRange(ScanNetworkChanges(systemDaysBack));

        return findings;
    }

    // -------------------------------------------------------------------
    // 1. Failed Logins  (Security 4625, admin required)
    // -------------------------------------------------------------------

    internal List<Finding> ScanFailedLogins(int daysBack = 7)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        List<(DateTime Time, int Id, string[] Props)> events;
        try
        {
            events = QueryLog("Security", 4625, since, propIndices: [5]);
        }
        catch (UnauthorizedAccessException)
        {
            findings.Add(new Finding(
                "Failed Logins",
                "Cannot read Security log — run as Administrator.",
                Status.Warning));
            return findings;
        }

        if (events.Count == 0)
        {
            findings.Add(new Finding(
                "Failed Logins",
                $"No failed login attempts in the last {daysBack} days.",
                Status.Safe));
            return findings;
        }

        var byUser = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            var username = evt.Props[0] ?? "(unknown)";
            byUser[username] = byUser.GetValueOrDefault(username) + 1;
        }

        var total = events.Count;
        var status = total > 10 ? Status.Danger
                   : total > 3  ? Status.Warning
                   : Status.Safe;

        findings.Add(new Finding(
            "Failed Logins",
            $"{total} failed login attempts in the last {daysBack} days.",
            status));

        foreach (var (user, count) in byUser.OrderByDescending(kv => kv.Value))
        {
            findings.Add(new Finding(
                "Failed Login User",
                $"{user}: {count} attempts",
                count > 10 ? Status.Danger
              : count > 3  ? Status.Warning
              : Status.Safe));
        }

        return findings;
    }

    // -------------------------------------------------------------------
    // 2. Account Lockouts  (Security 4740, admin required)
    // -------------------------------------------------------------------

    internal List<Finding> ScanAccountLockouts(int daysBack = 7)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        List<(DateTime Time, int Id, string[] Props)> events;
        try
        {
            events = QueryLog("Security", 4740, since, propIndices: [0, 4]);
        }
        catch (UnauthorizedAccessException)
        {
            findings.Add(new Finding(
                "Account Lockouts",
                "Cannot read Security log — run as Administrator.",
                Status.Warning));
            return findings;
        }

        if (events.Count == 0)
        {
            findings.Add(new Finding(
                "Account Lockouts",
                $"No account lockouts in the last {daysBack} days.",
                Status.Safe));
            return findings;
        }

        findings.Add(new Finding(
            "Account Lockouts",
            $"{events.Count} account lockouts in the last {daysBack} days.",
            Status.Danger));

        foreach (var evt in events)
        {
            var account = evt.Props[0] ?? "(unknown)";
            var caller = evt.Props[1] ?? "(unknown)";
            findings.Add(new Finding(
                "Locked Account",
                $"{account} locked out (caller: {caller})",
                Status.Danger));
        }

        return findings;
    }

    // -------------------------------------------------------------------
    // 3. BSODs / Crashes  (System log, no admin)
    // -------------------------------------------------------------------

    internal List<Finding> ScanBsodCrashes(int daysBack = 30)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        var kernelPower = QueryLog("System", 41, since,
            "Microsoft-Windows-Kernel-Power");
        var bugChecks = QueryLog("System", 1001, since);

        var total = kernelPower.Count + bugChecks.Count;

        if (total == 0)
        {
            findings.Add(new Finding(
                "System Crashes (BSOD)",
                $"No crash events in the last {daysBack} days.",
                Status.Safe));
            return findings;
        }

        var status = total > 3 ? Status.Danger : Status.Warning;

        findings.Add(new Finding(
            "System Crashes (BSOD)",
            $"{total} crash events in the last {daysBack} days " +
            $"({kernelPower.Count} unexpected shutdowns, " +
            $"{bugChecks.Count} bugchecks).",
            status));

        return findings;
    }

    // -------------------------------------------------------------------
    // 4. App Crashes  (Application log, no admin)
    // -------------------------------------------------------------------

    internal List<Finding> ScanAppCrashes(int daysBack = 30)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        var errors = QueryLog("Application", 1000, since, propIndices: [0]);
        var hangs = QueryLog("Application", 1002, since, propIndices: [0]);

        var all = errors.Concat(hangs).ToList();

        if (all.Count == 0)
        {
            findings.Add(new Finding(
                "Application Crashes",
                $"No application crashes in the last {daysBack} days.",
                Status.Safe));
            return findings;
        }

        var byApp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in all)
        {
            var appName = evt.Props[0] ?? "(unknown)";
            byApp[appName] = byApp.GetValueOrDefault(appName) + 1;
        }

        var total = all.Count;
        var status = total > 10 ? Status.Danger
                   : total > 3  ? Status.Warning
                   : Status.Safe;

        findings.Add(new Finding(
            "Application Crashes",
            $"{total} application crashes in the last {daysBack} days.",
            status));

        foreach (var (app, count) in byApp.OrderByDescending(kv => kv.Value))
        {
            findings.Add(new Finding(
                "Crashing App",
                $"{app}: {count} crashes",
                count > 5 ? Status.Danger
              : count > 1 ? Status.Warning
              : Status.Safe));
        }

        return findings;
    }

    // -------------------------------------------------------------------
    // 5. Unexpected Shutdowns  (System 6008, no admin)
    // -------------------------------------------------------------------

    internal List<Finding> ScanUnexpectedShutdowns(int daysBack = 30)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        var events = QueryLog("System", 6008, since);

        if (events.Count == 0)
        {
            findings.Add(new Finding(
                "Unexpected Shutdowns",
                $"No unexpected shutdowns in the last {daysBack} days.",
                Status.Safe));
            return findings;
        }

        var status = events.Count > 3 ? Status.Danger : Status.Warning;

        findings.Add(new Finding(
            "Unexpected Shutdowns",
            $"{events.Count} unexpected shutdowns in the last {daysBack} days.",
            status));

        return findings;
    }

    // -------------------------------------------------------------------
    // 6. Power History  (System log, no admin)
    // -------------------------------------------------------------------

    internal List<Finding> ScanPowerHistory(int daysBack = 30)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        var boots = QueryLog("System", 6005, since).Count;
        var cleanShutdowns = QueryLog("System", 6006, since).Count;
        var sleeps = QueryLog("System", 42, since).Count;

        var wakes1 = QueryLog("System", 1, since,
            "Microsoft-Windows-Power-Troubleshooter").Count;
        var wakes107 = QueryLog("System", 107, since,
            "Microsoft-Windows-Power-Troubleshooter").Count;
        var wakes = wakes1 + wakes107;

        findings.Add(new Finding(
            "Power History",
            $"Last {daysBack} days: {boots} boots, " +
            $"{cleanShutdowns} clean shutdowns, " +
            $"{sleeps} sleeps, {wakes} wakes.",
            Status.Safe));

        return findings;
    }

    // -------------------------------------------------------------------
    // 7. Network Changes  (NetworkProfile/Operational, no admin)
    // -------------------------------------------------------------------

    internal List<Finding> ScanNetworkChanges(int daysBack = 30)
    {
        var findings = new List<Finding>();
        var since = DateTime.UtcNow.AddDays(-daysBack);

        const string logName =
            "Microsoft-Windows-NetworkProfile/Operational";

        var connects = QueryLog(logName, 10000, since, propIndices: [0]);
        var disconnects = QueryLog(logName, 10001, since, propIndices: [0]);

        if (connects.Count == 0 && disconnects.Count == 0)
        {
            findings.Add(new Finding(
                "Network Changes",
                $"No network change events in the last {daysBack} days.",
                Status.Safe));
            return findings;
        }

        var networks = new Dictionary<string, (int Connected, int Disconnected)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var evt in connects)
        {
            var name = evt.Props[0] ?? "(unknown)";
            var cur = networks.GetValueOrDefault(name);
            networks[name] = (cur.Connected + 1, cur.Disconnected);
        }

        foreach (var evt in disconnects)
        {
            var name = evt.Props[0] ?? "(unknown)";
            var cur = networks.GetValueOrDefault(name);
            networks[name] = (cur.Connected, cur.Disconnected + 1);
        }

        findings.Add(new Finding(
            "Network Changes",
            $"{networks.Count} networks seen in the last {daysBack} days.",
            Status.Safe));

        foreach (var (net, (conn, disc)) in networks
            .OrderByDescending(kv => kv.Value.Connected + kv.Value.Disconnected))
        {
            findings.Add(new Finding(
                "Network",
                $"{net}: {conn} connects, {disc} disconnects",
                Status.Safe));
        }

        return findings;
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    static List<(DateTime Time, int Id, string[] Props)> QueryLog(
        string logName, int eventId, DateTime since,
        string? providerName = null, int[]? propIndices = null)
    {
        var timeFilter = since.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var xpath = providerName is not null
            ? $"*[System[Provider[@Name='{providerName}'] " +
              $"and (EventID={eventId}) " +
              $"and TimeCreated[@SystemTime>='{timeFilter}']]]"
            : $"*[System[(EventID={eventId}) " +
              $"and TimeCreated[@SystemTime>='{timeFilter}']]]";

        var query = new EventLogQuery(logName, PathType.LogName, xpath)
        {
            ReverseDirection = true
        };

        var results = new List<(DateTime Time, int Id, string[] Props)>();

        try
        {
            using var reader = new EventLogReader(query) { BatchSize = BatchSize };

            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    var time = record.TimeCreated ?? DateTime.MinValue;
                    var id = record.Id;
                    var props = ExtractProperties(record, propIndices);
                    results.Add((time, id, props));
                }

                if (results.Count >= MaxRecords) break;
            }
        }
        catch (EventLogNotFoundException)
        {
            // Log not present on this machine — return empty.
        }
        catch (EventLogReadingException)
        {
            // Corrupted or inaccessible log — return what we have.
        }

        return results;
    }

    static string[] ExtractProperties(EventRecord record, int[]? indices)
    {
        if (indices is null || indices.Length == 0)
            return [];

        var props = new string[indices.Length];
        try
        {
            var evtProps = ((EventLogRecord)record).Properties;
            for (int i = 0; i < indices.Length; i++)
            {
                int idx = indices[i];
                props[i] = idx < evtProps.Count
                    ? evtProps[idx].Value?.ToString() ?? "(unknown)"
                    : "(unknown)";
            }
        }
        catch
        {
            // Property extraction failed — fill with unknowns.
            for (int i = 0; i < props.Length; i++)
                props[i] ??= "(unknown)";
        }

        return props;
    }
}
