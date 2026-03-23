namespace PCGuardian;

internal enum Status { Safe, Warning, Danger }

internal sealed record Finding(string Label, string Detail, Status Status);

internal sealed record Category(
    string Id,
    string Icon,
    string Title,
    string Question,
    Status Status,
    string Summary,
    IReadOnlyList<Finding> Findings,
    string Tip);

internal sealed record Report(
    DateTime Timestamp,
    Status Overall,
    IReadOnlyList<Category> Categories,
    int SafeCount,
    int WarningCount,
    int DangerCount);

internal sealed class AppSettings
{
    // Scanning
    public int ScanIntervalMinutes { get; set; } = 30;
    public bool ScanOnStartup { get; set; } = true;

    // Behavior
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;

    // Process Monitor
    public bool ProcessMonitorEnabled { get; set; } = true;
    public int ProcessSnapshotIntervalSec { get; set; } = 30;

    // Data
    public int DataRetentionDays { get; set; } = 0; // 0 = forever

    // IT Sharing
    public bool ITSharingEnabled { get; set; }
    public int ITSharingPort { get; set; } = 7777;
    public string ITSharingPin { get; set; } = "";
    public bool TunnelEnabled { get; set; }
    public string TrustLevel { get; set; } = "standard";
    public string CompanyName { get; set; } = "PC Guardian";
    public string? ContactUrl { get; set; }
    public string? ContactPhone { get; set; }

    // Appearance & UX
    public bool DarkMode { get; set; } = true;
    public bool SoundsEnabled { get; set; } = true;
    public bool OnboardingCompleted { get; set; }
}
