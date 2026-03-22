namespace PCGuardian;

internal static class Theme
{
    // Theme mode
    static bool _isDark = true;
    public static bool IsDark => _isDark;

    public static void SetDark(bool dark) => _isDark = dark;

    // Backgrounds
    public static Color BgPrimary => _isDark ? Color.FromArgb(9, 9, 11) : Color.FromArgb(250, 250, 250);
    public static Color BgCard => _isDark ? Color.FromArgb(24, 24, 27) : Color.White;
    public static Color BgCardHover => _isDark ? Color.FromArgb(35, 35, 40) : Color.FromArgb(245, 245, 245);
    public static readonly Color BgElevated = Color.FromArgb(35, 35, 40);   // L2 — hovered cards
    public static readonly Color BgActive = Color.FromArgb(45, 45, 50);     // L3 — pressed states
    public static Color Border => _isDark ? Color.FromArgb(39, 39, 42) : Color.FromArgb(229, 229, 229);
    public static Color BgFooter => _isDark ? Color.FromArgb(15, 15, 18) : Color.FromArgb(240, 240, 242);

    // Text
    public static Color TextPrimary => _isDark ? Color.FromArgb(250, 250, 250) : Color.FromArgb(23, 23, 23);
    public static Color TextSecondary => _isDark ? Color.FromArgb(161, 161, 170) : Color.FromArgb(100, 100, 110);
    public static Color TextMuted => _isDark ? Color.FromArgb(113, 113, 122) : Color.FromArgb(150, 150, 158);

    // Status (same in both themes)
    public static readonly Color Safe = Color.FromArgb(16, 185, 129);
    public static readonly Color Warning = Color.FromArgb(245, 158, 11);
    public static readonly Color Danger = Color.FromArgb(239, 68, 68);

    // Accent (same in both themes)
    public static readonly Color Accent = Color.FromArgb(99, 102, 241);
    public static readonly Color AccentHover = Color.FromArgb(120, 123, 255);
    public static readonly Color Violet = Color.FromArgb(168, 85, 247);     // secondary accent
    public static readonly Color Orange = Color.FromArgb(249, 115, 22);     // between warning and danger

    // Fonts (static — don't change with theme)
    public static readonly Font Title = new("Segoe UI", 20f, FontStyle.Bold);
    public static readonly Font Subtitle = new("Segoe UI", 11f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 10.5f);
    public static readonly Font CardBody = new("Segoe UI", 9f);
    public static readonly Font Badge = new("Segoe UI Semibold", 7.5f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Icon = new("Segoe UI Emoji", 18f);
    public static readonly Font BigIcon = new("Segoe UI Emoji", 48f);
    public static readonly Font ScoreNumber = new("Segoe UI", 36f, FontStyle.Bold);
    public static readonly Font GradeLabel = new("Segoe UI Semibold", 14f);
    public static readonly Font SectionHeader = new("Segoe UI Semibold", 11f);

    // Animation constants
    public const int AnimFps = 60;
    public const int AnimInterval = 16; // ms (1000/60)
    public const int RingAnimDuration = 800; // ms
    public const int GaugeAnimDuration = 300; // ms
    public const float PulseFrequency = 1f; // Hz for danger tile pulse

    // Dashboard layout constants
    public const int DashPadding = 16;
    public const int SectionGap = 8;
    public const int TileWidth = 72;
    public const int TileHeight = 40;
    public const int TileGapH = 4;
    public const int TileGapV = 6;
    public const int GaugeHeight = 32;
    public const int FeedRowHeight = 24;
    public const int RecCardHeight = 36;
    public const int RingDiameter = 160;
    public const int RingStroke = 10;

    // Score-to-color interpolation
    public static Color ScoreColor(float score) => score switch
    {
        >= 80 => LerpColor(Safe, Color.FromArgb(52, 211, 153), (score - 80) / 20f),
        >= 60 => LerpColor(Warning, Safe, (score - 60) / 20f),
        >= 40 => LerpColor(Orange, Warning, (score - 40) / 20f),
        _ => LerpColor(Danger, Orange, score / 40f),
    };

    public static Color LerpColor(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    // Alpha blending helper
    public static Color WithAlpha(Color c, int alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Color StatusColor(Status s) => s switch
    {
        Status.Safe => Safe,
        Status.Warning => Warning,
        Status.Danger => Danger,
        _ => TextSecondary,
    };

    public static Color StatusBg(Status s) => Color.FromArgb(25, StatusColor(s));

    public static string StatusLabel(Status s) => s switch
    {
        Status.Safe => "All Good",
        Status.Warning => "Worth Checking",
        Status.Danger => "Needs Attention",
        _ => "",
    };
}
