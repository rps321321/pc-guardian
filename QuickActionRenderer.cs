using System.Drawing.Drawing2D;

namespace PCGuardian;

internal static class QuickActionRenderer
{
    private static readonly (string Label, string Icon)[] Buttons =
    [
        ("Scan Now", "\uD83D\uDD0D"),    // magnifying glass
        ("Activity", "\uD83D\uDCCA"),    // chart
        ("Network",  "\uD83C\uDF10"),    // globe
        ("Settings", "\u2699\uFE0F"),    // gear
    ];

    private const int ButtonHeight = 28;
    private const int Gap = 8;
    private const int CornerRadius = 6;

    private static readonly Font ButtonFont = new("Segoe UI Semibold", 9f);

    public static void DisposeResources()
    {
        ButtonFont.Dispose();
    }

    public static void Draw(Graphics g, Rectangle bounds, int hoveredIndex)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int buttonWidth = (bounds.Width - 5 * Gap) / 4;
        int y = bounds.Y + (bounds.Height - ButtonHeight) / 2;

        for (int i = 0; i < Buttons.Length; i++)
        {
            int x = bounds.X + Gap + i * (buttonWidth + Gap);
            var btnRect = new Rectangle(x, y, buttonWidth, ButtonHeight);
            bool isHovered = i == hoveredIndex;
            bool isScan = i == 0;

            // --- Background ---
            Color bgColor;
            if (isScan)
                bgColor = isHovered
                    ? Color.FromArgb(89, Theme.Accent)   // 35% of 255 ~ 89
                    : Color.FromArgb(51, Theme.Accent);   // 20% of 255 ~ 51
            else
                bgColor = isHovered ? Theme.BgElevated : Theme.BgCard;

            using (var bgBrush = new SolidBrush(bgColor))
                DrawRoundedRect(g, btnRect, CornerRadius, bgBrush);

            // --- 1px border ---
            using (var borderPen = new Pen(Theme.Border, 1f))
                DrawRoundedRectOutline(g, btnRect, CornerRadius, borderPen);

            // --- Text ---
            Color textColor = isScan ? Theme.Accent : Theme.TextPrimary;
            string text = $"{Buttons[i].Icon} {Buttons[i].Label}";

            TextRenderer.DrawText(
                g, text, ButtonFont, btnRect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ── GPU-accelerated Direct2D path ────────────────────────────────

    public static void DrawD2D(GpuRenderer gpu, Rectangle bounds, int hoveredIndex)
    {
        int buttonWidth = (bounds.Width - 5 * Gap) / 4;
        int y = bounds.Y + (bounds.Height - ButtonHeight) / 2;

        for (int i = 0; i < Buttons.Length; i++)
        {
            int x = bounds.X + Gap + i * (buttonWidth + Gap);
            var btnRect = new RectangleF(x, y, buttonWidth, ButtonHeight);
            bool isHovered = i == hoveredIndex;
            bool isScan = i == 0;

            // Background
            Color bgColor;
            if (isScan)
                bgColor = isHovered
                    ? Color.FromArgb(89, Theme.Accent)
                    : Color.FromArgb(51, Theme.Accent);
            else
                bgColor = isHovered ? Theme.BgElevated : Theme.BgCard;

            gpu.FillRoundedRect(btnRect, CornerRadius, bgColor);

            // 1px border
            gpu.DrawRoundedRect(btnRect, CornerRadius, Theme.Border, 1f);

            // Text — centered via MeasureText
            Color textColor = isScan ? Theme.Accent : Theme.TextPrimary;
            string text = $"{Buttons[i].Icon} {Buttons[i].Label}";
            var textSize = gpu.MeasureText(text, "Segoe UI Semibold", 9f, Vortice.DirectWrite.FontWeight.SemiBold);
            float tx = x + (buttonWidth - textSize.Width) / 2f;
            float ty = y + (ButtonHeight - textSize.Height) / 2f;
            gpu.DrawTextSimple(text, "Segoe UI Semibold", 9f, textColor, tx, ty, Vortice.DirectWrite.FontWeight.SemiBold);
        }
    }

    public static int HitTest(Rectangle bounds, Point mousePos)
    {
        if (!bounds.Contains(mousePos))
            return -1;

        int buttonWidth = (bounds.Width - 5 * Gap) / 4;
        int y = bounds.Y + (bounds.Height - ButtonHeight) / 2;

        for (int i = 0; i < Buttons.Length; i++)
        {
            int x = bounds.X + Gap + i * (buttonWidth + Gap);
            var btnRect = new Rectangle(x, y, buttonWidth, ButtonHeight);

            if (btnRect.Contains(mousePos))
                return i;
        }

        return -1;
    }

    private static void DrawRoundedRect(Graphics g, Rectangle rect, int radius, Brush brush)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        int d = radius * 2;
        d = Math.Min(d, Math.Min(rect.Width, rect.Height));
        radius = d / 2;

        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRectOutline(Graphics g, Rectangle rect, int radius, Pen pen)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        int d = radius * 2;
        d = Math.Min(d, Math.Min(rect.Width, rect.Height));
        radius = d / 2;

        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
