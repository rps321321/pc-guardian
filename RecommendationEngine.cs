namespace PCGuardian;

internal static class RecommendationEngine
{
    /// <summary>
    /// Generates prioritized, actionable recommendations from scan results and security posture.
    /// Returns top 3 with category diversity (max 2 per category).
    /// </summary>
    public static List<Recommendation> Generate(Report? lastScan, SecurityPosture? posture)
    {
        var all = new List<Recommendation>();

        if (lastScan is not null)
            AddScanRecommendations(all, lastScan);

        if (posture is not null)
            AddPostureRecommendations(all, posture);

        return ApplyCategoryDiversity(all, maxTotal: 3, maxPerCategory: 2);
    }

    // -----------------------------------------------------------------------
    // Scan-based recommendations
    // -----------------------------------------------------------------------

    static void AddScanRecommendations(List<Recommendation> list, Report scan)
    {
        var troubled = scan.Categories
            .Where(c => c.Status is Status.Warning or Status.Danger)
            .ToList();

        foreach (var cat in troubled)
        {
            var riskyCount = cat.Findings.Count(f => f.Status is Status.Warning or Status.Danger);
            bool isDanger = cat.Status == Status.Danger;

            switch (cat.Id)
            {
                case "rdp":
                    list.Add(new("disable-rdp", "Disable Remote Desktop if not needed",
                        "Remote Access", Impact: 9, Effort: 2, Urgency: 8, DismissCount: 0, HasFix: true));
                    break;

                case "firewall":
                    list.Add(new("enable-firewall", "Enable Windows Firewall",
                        "Firewall", Impact: 9, Effort: 1, Urgency: 9, DismissCount: 0, HasFix: true));
                    break;

                case "remote-apps":
                    list.Add(new("stop-remote-apps", "Close unnecessary remote access apps",
                        "Remote Access", Impact: 8, Effort: 3, Urgency: 7, DismissCount: 0, HasFix: true));
                    break;

                case "ports":
                    list.Add(new("close-ports", $"Close {riskyCount} unnecessary open port(s)",
                        "Network", Impact: 7, Effort: 4, Urgency: 6, DismissCount: 0, HasFix: true));
                    break;

                case "connections":
                    list.Add(new("review-connections", $"Investigate {riskyCount} suspicious network connection(s)",
                        "Network", Impact: 7, Effort: 5, Urgency: isDanger ? 8f : 5f, DismissCount: 0, HasFix: false));
                    break;

                case "shares":
                    list.Add(new("review-shares", "Remove unnecessary shared folders",
                        "Network", Impact: 7, Effort: 3, Urgency: 6, DismissCount: 0, HasFix: false));
                    break;

                case "services":
                    list.Add(new("disable-remote-services", "Disable unused remote services",
                        "Services", Impact: 7, Effort: 4, Urgency: 6, DismissCount: 0, HasFix: false));
                    break;

                case "antivirus":
                    list.Add(new("update-av", "Update antivirus definitions",
                        "Antivirus", Impact: 8, Effort: 1, Urgency: 8, DismissCount: 0, HasFix: false));
                    break;

                case "startup":
                    list.Add(new("review-startup", "Review suspicious startup programs",
                        "Startup", Impact: 6, Effort: 5, Urgency: 4, DismissCount: 0, HasFix: false));
                    break;

                case "users":
                    list.Add(new("review-users", "Review logged-in user accounts",
                        "Security", Impact: 6, Effort: 4, Urgency: 5, DismissCount: 0, HasFix: false));
                    break;

                case "tasks":
                    list.Add(new("review-tasks", "Audit suspicious scheduled tasks",
                        "Security", Impact: 7, Effort: 5, Urgency: 5, DismissCount: 0, HasFix: false));
                    break;

                case "dns":
                    list.Add(new("fix-dns", "Restore DNS to trusted servers",
                        "Network", Impact: 7, Effort: 2, Urgency: 7, DismissCount: 0, HasFix: true));
                    break;

                case "usb":
                    list.Add(new("review-usb", "Review connected USB devices",
                        "Hardware", Impact: 5, Effort: 2, Urgency: 4, DismissCount: 0, HasFix: false));
                    break;

                case "hardware":
                    list.Add(new("check-hardware", "Investigate hardware health warnings",
                        "Hardware", Impact: 6, Effort: 6, Urgency: isDanger ? 8f : 4f, DismissCount: 0, HasFix: false));
                    break;

                case "security-posture":
                    list.Add(new("improve-posture", "Strengthen device security settings",
                        "Security", Impact: 8, Effort: 5, Urgency: 5, DismissCount: 0, HasFix: false));
                    break;

                case "security-events":
                    list.Add(new("review-events", "Investigate recent security events",
                        "Security", Impact: 8, Effort: 4, Urgency: isDanger ? 9f : 5f, DismissCount: 0, HasFix: false));
                    break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Security-posture-based recommendations
    // -----------------------------------------------------------------------

    static void AddPostureRecommendations(List<Recommendation> list, SecurityPosture posture)
    {
        if (posture.BitLockerEnabled == false)
            list.Add(new("enable-bitlocker", "Encrypt your drive with BitLocker",
                "Security", Impact: 8, Effort: 6, Urgency: 3, DismissCount: 0, HasFix: false));

        if (posture.SecureBootEnabled == false)
            list.Add(new("enable-secureboot", "Enable Secure Boot in BIOS",
                "Security", Impact: 7, Effort: 7, Urgency: 2, DismissCount: 0, HasFix: false));

        if (posture.AutoLoginEnabled)
            list.Add(new("disable-autologin", "Disable auto-login",
                "Security", Impact: 8, Effort: 2, Urgency: 7, DismissCount: 0, HasFix: true));

        if (posture.GuestAccountEnabled == true)
            list.Add(new("disable-guest", "Disable guest account",
                "Security", Impact: 6, Effort: 2, Urgency: 5, DismissCount: 0, HasFix: true));

        if (posture.ScreenLockTimeoutSec is null)
            list.Add(new("enable-screenlock", "Set up screen lock timeout",
                "Security", Impact: 5, Effort: 3, Urgency: 4, DismissCount: 0, HasFix: false));

        if (posture.RebootPending)
            list.Add(new("install-updates", "Install pending Windows updates",
                "Updates", Impact: 7, Effort: 3, Urgency: 6, DismissCount: 0, HasFix: false));

        if (posture.PasswordMinLength < 8)
            list.Add(new("strengthen-password", "Set minimum password length",
                "Security", Impact: 6, Effort: 4, Urgency: 3, DismissCount: 0, HasFix: false));
    }

    // -----------------------------------------------------------------------
    // Category-diverse top-N selection
    // -----------------------------------------------------------------------

    static float ComputePriority(Recommendation r)
    {
        float fatigueFactor = MathF.Max(MathF.Pow(0.5f, r.DismissCount), 0.1f);
        return (r.Impact * 0.4f + r.Urgency * 0.4f) * ((10f - r.Effort) / 10f) * fatigueFactor;
    }

    static List<Recommendation> ApplyCategoryDiversity(
        List<Recommendation> all, int maxTotal, int maxPerCategory)
    {
        var sorted = all.OrderByDescending(r => ComputePriority(r)).ToList();
        var result = new List<Recommendation>();
        var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // First pass: pick top items respecting category cap
        foreach (var rec in sorted)
        {
            if (result.Count >= maxTotal)
                break;

            categoryCounts.TryGetValue(rec.Category, out var count);
            if (count >= maxPerCategory)
                continue;

            result.Add(rec);
            categoryCounts[rec.Category] = count + 1;
        }

        // Second pass: fill remaining slots if diversity filter was too strict
        if (result.Count < maxTotal)
        {
            foreach (var rec in sorted)
            {
                if (result.Count >= maxTotal)
                    break;

                if (!result.Contains(rec))
                    result.Add(rec);
            }
        }

        return result;
    }
}
