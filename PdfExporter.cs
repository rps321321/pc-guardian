using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PCGuardian;

internal static class PdfExporter
{
    static PdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static bool SaveAsPdf(Report report, string pdfPath, SystemMonitor? monitor = null)
    {
        try
        {
            Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Segoe UI"));

                    page.Header().Element(c => DrawHeader(c, report, monitor));
                    page.Content().Element(c => DrawContent(c, report));
                    page.Footer().Element(DrawFooter);
                });
            }).GeneratePdf(pdfPath);

            return File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 100;
        }
        catch { return false; }
    }

    static void DrawHeader(IContainer container, Report report, SystemMonitor? monitor = null)
    {
        var (statusText, statusHex) = report.Overall switch
        {
            Status.Safe => ("Your PC looks secure", "#10b981"),
            Status.Warning => ("A few things to review", "#f59e0b"),
            _ => ("Some things need your attention", "#ef4444"),
        };

        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.AutoItem().Text("\uD83D\uDEE1\uFE0F PC Guardian").FontSize(18).Bold();
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.Span(Environment.MachineName).FontSize(9).FontColor(Colors.Grey.Medium);
                    text.Span(" \u00B7 ").FontSize(9).FontColor(Colors.Grey.Medium);
                    text.Span(report.Timestamp.ToString("MMM d, yyyy h:mm tt")).FontSize(9).FontColor(Colors.Grey.Medium);
                });
            });

            // System info section (when monitor is available)
            if (monitor is not null)
            {
                try
                {
                    var info = monitor.GetSystemInfo() as SystemStaticInfo;
                    if (info is not null)
                    {
                        double ramGb = info.TotalRamBytes / (1024.0 * 1024 * 1024);
                        col.Item().PaddingTop(8).PaddingBottom(4)
                            .Background("#f8fafc").Border(0.5f).BorderColor("#e2e8f0").Padding(8)
                            .Column(sysCol =>
                            {
                                sysCol.Item().Text("System Information").FontSize(9).SemiBold().FontColor("#475569");
                                sysCol.Item().PaddingTop(4).Row(sysRow =>
                                {
                                    sysRow.RelativeItem().Column(left =>
                                    {
                                        left.Item().Text(text =>
                                        {
                                            text.Span("CPU: ").FontSize(8).SemiBold().FontColor("#64748b");
                                            text.Span(info.CpuName).FontSize(8).FontColor("#334155");
                                        });
                                        left.Item().Text(text =>
                                        {
                                            text.Span("RAM: ").FontSize(8).SemiBold().FontColor("#64748b");
                                            text.Span($"{ramGb:F1} GB").FontSize(8).FontColor("#334155");
                                        });
                                    });
                                    sysRow.RelativeItem().Column(right =>
                                    {
                                        right.Item().Text(text =>
                                        {
                                            text.Span("OS: ").FontSize(8).SemiBold().FontColor("#64748b");
                                            text.Span(info.OsCaption).FontSize(8).FontColor("#334155");
                                        });
                                        right.Item().Text(text =>
                                        {
                                            text.Span("GPU: ").FontSize(8).SemiBold().FontColor("#64748b");
                                            text.Span(string.IsNullOrWhiteSpace(info.GpuName) ? "N/A" : info.GpuName).FontSize(8).FontColor("#334155");
                                        });
                                    });
                                });
                            });
                    }
                }
                catch { /* monitor may not have info yet — skip gracefully */ }
            }

            col.Item().PaddingTop(10).PaddingBottom(6)
                .BorderLeft(3).BorderColor(statusHex).PaddingLeft(10)
                .Background(report.Overall == Status.Safe ? "#f0fdf4" : report.Overall == Status.Warning ? "#fffbeb" : "#fef2f2")
                .Padding(10)
                .Column(inner =>
                {
                    inner.Item().Text(statusText).FontSize(13).Bold().FontColor(statusHex);
                    var stats = $"{report.SafeCount} of {report.Categories.Count} checks passed";
                    if (report.WarningCount > 0) stats += $" \u00B7 {report.WarningCount} to review";
                    if (report.DangerCount > 0) stats += $" \u00B7 {report.DangerCount} need attention";
                    inner.Item().Text(stats).FontSize(8).FontColor(Colors.Grey.Medium);
                });

            // Risk score
            int score = RiskScore.Calculate(report);
            col.Item().PaddingTop(6).PaddingBottom(10).Row(row =>
            {
                row.AutoItem().Text($"Security Score: {score}/100 ({RiskScore.Grade(score)})").FontSize(10).SemiBold();
                row.RelativeItem().AlignRight().Text(RiskScore.FriendlyDescription(score)).FontSize(8).FontColor(Colors.Grey.Medium);
            });
        });
    }

    static void DrawContent(IContainer container, Report report)
    {
        container.Column(col =>
        {
            foreach (var cat in report.Categories)
            {
                var (barColor, badgeBg, badgeText) = cat.Status switch
                {
                    Status.Safe => ("#10b981", "#f0fdf4", "All Good"),
                    Status.Warning => ("#f59e0b", "#fffbeb", "Worth Checking"),
                    _ => ("#ef4444", "#fef2f2", "Needs Attention"),
                };

                col.Item().PaddingBottom(6).Row(row =>
                {
                    // Left color bar
                    row.ConstantItem(3).Background(barColor).ExtendVertical();

                    // Card body
                    row.RelativeItem().Border(0.5f).BorderColor("#e5e5e5").Padding(10).Column(card =>
                    {
                        // Header: icon + title + badge
                        card.Item().Row(header =>
                        {
                            header.AutoItem().Text($"{cat.Icon}  {cat.Title}").FontSize(11).SemiBold();
                            header.RelativeItem().AlignRight()
                                .Background(badgeBg).Padding(2).PaddingHorizontal(8)
                                .Text(badgeText).FontSize(7).SemiBold().FontColor(barColor);
                        });

                        // Summary
                        card.Item().PaddingTop(2).Text(cat.Summary).FontSize(8).FontColor(Colors.Grey.Medium);

                        // Findings
                        card.Item().PaddingTop(6).Column(findings =>
                        {
                            foreach (var f in cat.Findings)
                            {
                                var dotColor = f.Status switch
                                {
                                    Status.Safe => "#10b981",
                                    Status.Warning => "#f59e0b",
                                    _ => "#ef4444",
                                };

                                findings.Item().PaddingBottom(3).Row(fRow =>
                                {
                                    fRow.ConstantItem(10).PaddingTop(3).Text("\u25CF").FontSize(6).FontColor(dotColor);
                                    fRow.RelativeItem().Column(fCol =>
                                    {
                                        fCol.Item().Text(f.Label).FontSize(8).SemiBold();
                                        fCol.Item().Text(f.Detail).FontSize(7).FontColor(Colors.Grey.Medium);
                                    });
                                });
                            }
                        });

                        // Tip
                        card.Item().PaddingTop(6).BorderTop(0.5f).BorderColor("#eeeeee").PaddingTop(4)
                            .Text(text =>
                            {
                                text.Span("What to do: ").FontSize(7.5f).SemiBold().FontColor(Colors.Grey.Medium);
                                text.Span(cat.Tip).FontSize(7.5f).FontColor(Colors.Grey.Medium);
                            });
                    });
                });
            }
        });
    }

    static void DrawFooter(IContainer container)
    {
        container.AlignCenter().Text(text =>
        {
            text.Span($"Generated by PC Guardian on {DateTime.Now:MMMM d, yyyy h:mm tt}").FontSize(7).FontColor(Colors.Grey.Medium);
            text.Span(" \u00B7 This report is a snapshot \u2014 run a new scan for the latest results.").FontSize(7).FontColor(Colors.Grey.Lighten1);
        });
    }
}
