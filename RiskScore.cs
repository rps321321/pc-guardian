using System.Drawing;

namespace PCGuardian;

internal static class RiskScore
{
    private static readonly Dictionary<string, (int Danger, int Warning)> Penalties = new()
    {
        ["rdp"]         = (20, 10),
        ["remote-apps"] = (15,  5),
        ["ports"]       = (15,  5),
        ["connections"] = ( 5,  3),
        ["shares"]      = (10,  5),
        ["services"]    = (15,  5),
        ["firewall"]    = (20,  5),
        ["users"]       = (20,  5),
        ["startup"]     = ( 5,  3),
        ["tasks"]       = (15,  5),
        ["antivirus"]   = (20, 10),
        ["dns"]         = (10,  5),
        ["usb"]         = ( 3,  2),
        ["hardware"]          = (15,  5),
        ["security-posture"]  = (20,  8),
        ["security-events"]   = (15,  5),
    };

    public static int Calculate(Report report)
    {
        var score = 100;

        foreach (var cat in report.Categories)
        {
            if (cat.Status == Status.Safe || !Penalties.TryGetValue(cat.Id, out var pen))
                continue;

            score -= cat.Status == Status.Danger ? pen.Danger : pen.Warning;
        }

        return Math.Clamp(score, 0, 100);
    }

    public static string Grade(int score) => score switch
    {
        >= 95 => "A+",
        >= 85 => "A",
        >= 70 => "B",
        >= 55 => "C",
        >= 40 => "D",
        _     => "F",
    };

    public static Color GradeColor(int score) => score switch
    {
        >= 80 => Color.FromArgb(16, 185, 129),   // green
        >= 60 => Color.FromArgb(245, 158, 11),    // amber
        _     => Color.FromArgb(239, 68, 68),     // red
    };

    public static string FriendlyDescription(int score) => score switch
    {
        >= 90 => "Excellent! Your PC is well protected.",
        >= 75 => "Good shape, but a few things could be better.",
        >= 60 => "Some areas need attention.",
        >= 40 => "Your PC has several security gaps.",
        _     => "Your PC is at serious risk. Take action now.",
    };
}
