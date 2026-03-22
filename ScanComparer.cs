using System.Text;

namespace PCGuardian;

internal enum ChangeType { New, Removed, Improved, Worsened, Unchanged }

internal sealed record ScanChange(
    string CategoryTitle,
    string CategoryIcon,
    ChangeType Change,
    string Description,
    Status OldStatus,
    Status NewStatus);

internal sealed record ScanComparison(
    Report OldReport,
    Report NewReport,
    List<ScanChange> Changes,
    int Improved,
    int Worsened,
    int NewIssues,
    int ResolvedIssues,
    string Summary);

internal static class ScanComparer
{
    public static ScanComparison Compare(Report older, Report newer)
    {
        var oldMap = older.Categories.ToDictionary(c => c.Id);
        var newMap = newer.Categories.ToDictionary(c => c.Id);
        var changes = new List<ScanChange>();

        // Matched + removed categories
        foreach (var old in older.Categories)
        {
            if (newMap.TryGetValue(old.Id, out var cur))
            {
                var change = Classify(old.Status, cur.Status);
                var desc = change == ChangeType.Unchanged
                    ? $"{cur.Title}: unchanged ({StatusLabel(cur.Status)})"
                    : $"{cur.Title}: was {StatusLabel(old.Status)}, now {StatusLabel(cur.Status)}";
                changes.Add(new(cur.Title, cur.Icon, change, desc, old.Status, cur.Status));
            }
            else
            {
                changes.Add(new(old.Title, old.Icon, ChangeType.Removed,
                    $"{old.Title}: no longer scanned", old.Status, old.Status));
            }
        }

        // New categories
        foreach (var cur in newer.Categories)
        {
            if (!oldMap.ContainsKey(cur.Id))
            {
                changes.Add(new(cur.Title, cur.Icon, ChangeType.New,
                    $"{cur.Title}: new check ({StatusLabel(cur.Status)})", Status.Safe, cur.Status));
            }
        }

        int improved = changes.Count(c => c.Change == ChangeType.Improved);
        int worsened = changes.Count(c => c.Change == ChangeType.Worsened);
        int newIssues = changes.Count(c => c.Change == ChangeType.New && c.NewStatus != Status.Safe);
        int resolved = changes.Count(c => c.Change == ChangeType.Removed && c.OldStatus != Status.Safe);

        var parts = new List<string>();
        if (improved > 0) parts.Add($"{improved} improved");
        if (worsened > 0) parts.Add($"{worsened} worsened");
        if (newIssues > 0) parts.Add($"{newIssues} new issue{(newIssues > 1 ? "s" : "")}");
        if (resolved > 0) parts.Add($"{resolved} resolved");
        var datePart = older.Timestamp.ToString("MMM d");
        var summary = parts.Count > 0
            ? $"{string.Join(", ", parts)} since {datePart}"
            : $"No changes since {datePart}";

        return new(older, newer, changes, improved, worsened, newIssues, resolved, summary);
    }

    public static string ToHtml(ScanComparison comparison)
    {
        var sb = new StringBuilder();
        var host = Environment.MachineName;
        var oldDate = comparison.OldReport.Timestamp.ToString("MMM d, yyyy h:mm tt");
        var newDate = comparison.NewReport.Timestamp.ToString("MMM d, yyyy h:mm tt");

        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>PC Guardian \u2014 Scan Comparison</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style></head><body>");

        // Header
        sb.AppendLine("<div class=\"header\">");
        sb.AppendLine($"<div class=\"logo\">\ud83d\udee1\ufe0f PC Guardian \u2014 Comparison</div>");
        sb.AppendLine($"<div class=\"meta\">{host} &middot; {oldDate} &rarr; {newDate}</div>");
        sb.AppendLine("</div>");

        // Summary banner
        var bannerColor = comparison.Worsened > 0 ? "#ef4444" : comparison.Improved > 0 ? "#10b981" : "#6b7280";
        var bannerBg = comparison.Worsened > 0 ? "#fef2f2" : comparison.Improved > 0 ? "#ecfdf5" : "#f3f4f6";
        sb.AppendLine($"<div class=\"banner\" style=\"background:{bannerBg};border-left:4px solid {bannerColor}\">");
        sb.AppendLine($"<div class=\"banner-text\" style=\"color:{bannerColor}\">{comparison.Summary}</div>");
        sb.AppendLine("</div>");

        // Changes
        foreach (var c in comparison.Changes.OrderBy(c => c.Change == ChangeType.Unchanged))
        {
            var (color, bg, label) = c.Change switch
            {
                ChangeType.Improved => ("#10b981", "#ecfdf5", "Improved"),
                ChangeType.Worsened => ("#ef4444", "#fef2f2", "Worsened"),
                ChangeType.New      => ("#3b82f6", "#eff6ff", "New"),
                ChangeType.Removed  => ("#6b7280", "#f3f4f6", "Removed"),
                _                   => ("#9ca3af", "#f9fafb", "Unchanged"),
            };

            sb.AppendLine("<div class=\"card\">");
            sb.AppendLine($"<div class=\"card-bar\" style=\"background:{color}\"></div>");
            sb.AppendLine("<div class=\"card-body\">");
            sb.AppendLine($"<div class=\"card-header\">");
            sb.AppendLine($"<span class=\"card-icon\">{c.CategoryIcon}</span>");
            sb.AppendLine($"<span class=\"card-title\">{c.CategoryTitle}</span>");
            sb.AppendLine($"<span class=\"badge\" style=\"background:{bg};color:{color}\">{label}</span>");
            sb.AppendLine("</div>");
            sb.AppendLine($"<div class=\"card-desc\">{c.Description}</div>");
            sb.AppendLine("</div></div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    static ChangeType Classify(Status old, Status cur) => (old, cur) switch
    {
        _ when old == cur => ChangeType.Unchanged,
        (Status.Danger, Status.Warning) or (Status.Danger, Status.Safe) or (Status.Warning, Status.Safe)
            => ChangeType.Improved,
        _ => ChangeType.Worsened,
    };

    static string StatusLabel(Status s) => s switch
    {
        Status.Safe    => "All Good",
        Status.Warning => "Needs Attention",
        _              => "At Risk",
    };

    const string Css = """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
            background: #fafafa; color: #1a1a1a; padding: 32px;
            max-width: 800px; margin: 0 auto; line-height: 1.5;
        }
        .header { margin-bottom: 24px; }
        .logo { font-size: 24px; font-weight: 700; }
        .meta { font-size: 13px; color: #666; margin-top: 4px; }
        .banner {
            padding: 14px 20px; border-radius: 10px; margin-bottom: 24px;
        }
        .banner-text { font-size: 16px; font-weight: 600; }
        .card {
            display: flex; border: 1px solid #e5e5e5; border-radius: 10px;
            overflow: hidden; margin-bottom: 12px; background: #fff;
        }
        .card-bar { width: 4px; flex-shrink: 0; }
        .card-body { padding: 16px 20px; flex: 1; }
        .card-header { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
        .card-icon { font-size: 20px; }
        .card-title { font-size: 15px; font-weight: 600; }
        .badge {
            font-size: 11px; font-weight: 600; padding: 2px 10px;
            border-radius: 99px; margin-left: auto;
        }
        .card-desc { font-size: 13px; color: #555; }
        """;
}
