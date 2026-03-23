using System.Drawing.Drawing2D;

namespace PCGuardian;

internal static class RecommendationRenderer
{
    private static readonly Color RedAccent = Color.FromArgb(239, 68, 68);
    private static readonly Color AmberAccent = Color.FromArgb(245, 158, 11);
    private static readonly Color IndigoAccent = Color.FromArgb(99, 102, 241);
    private static readonly Color Emerald = Color.FromArgb(16, 185, 129);

    private static readonly Font DescFont = new("Segoe UI", 9f);
    private static readonly Font FixFont = new("Segoe UI Semibold", 8f);
    private static readonly Font IconFont = new("Segoe UI", 10f);
    private static readonly Font EmptyFont = new("Segoe UI", 9f);

    public static void DisposeResources()
    {
        DescFont.Dispose();
        FixFont.Dispose();
        IconFont.Dispose();
        EmptyFont.Dispose();
    }

    private const int MaxCards = 3;
    private const int CardHeight = 36;
    private const int CardGap = 4;
    private const int AccentBarWidth = 4;
    private const int IconX = 12;
    private const int DescX = 36;
    private const int FixWidth = 56;
    private const int FixHeight = 24;
    private const int FixMargin = 8;
    private const int CardRadius = 6;
    private const int FixRadius = 4;

    public static void Draw(
        Graphics g,
        Rectangle bounds,
        IReadOnlyList<Recommendation> recs,
        int hoveredIndex,
        int hoveredButton)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (recs.Count == 0)
        {
            DrawEmptyState(g, bounds);
            return;
        }

        int count = Math.Min(recs.Count, MaxCards);
        for (int i = 0; i < count; i++)
        {
            var cardRect = GetCardRect(bounds, i);
            DrawCard(g, cardRect, recs[i], isHovered: i == hoveredIndex, isFixHovered: i == hoveredButton);
        }
    }

    public static int HitTestCard(Rectangle bounds, Point mousePos)
    {
        for (int i = 0; i < MaxCards; i++)
        {
            var cardRect = GetCardRect(bounds, i);
            if (cardRect.Contains(mousePos))
                return i;
        }
        return -1;
    }

    public static int HitTestFixButton(Rectangle bounds, Point mousePos)
    {
        for (int i = 0; i < MaxCards; i++)
        {
            var fixRect = GetFixButtonRect(GetCardRect(bounds, i));
            if (fixRect.Contains(mousePos))
                return i;
        }
        return -1;
    }

    private static Rectangle GetCardRect(Rectangle bounds, int index)
    {
        int y = bounds.Y + index * (CardHeight + CardGap);
        return new Rectangle(bounds.X, y, bounds.Width, CardHeight);
    }

    private static Rectangle GetFixButtonRect(Rectangle cardRect)
    {
        int x = cardRect.Right - FixMargin - FixWidth;
        int y = cardRect.Y + (CardHeight - FixHeight) / 2;
        return new Rectangle(x, y, FixWidth, FixHeight);
    }

    private static void DrawCard(Graphics g, Rectangle card, Recommendation rec, bool isHovered, bool isFixHovered)
    {
        var accentColor = GetAccentColor(rec.Impact);

        // Card background
        using (var bgBrush = new SolidBrush(isHovered ? Theme.BgCardHover : Theme.BgCard))
            FillRoundedRect(g, card, CardRadius, bgBrush);

        // Left accent bar
        var barRect = new Rectangle(card.X, card.Y, AccentBarWidth, card.Height);
        using (var barBrush = new SolidBrush(accentColor))
            FillRoundedRect(g, barRect, CardRadius, barBrush);

        // Icon
        string icon = rec.Impact > 7 ? "\u26A0" : rec.Impact >= 4 ? "\u26A0" : "\u2139";
        int iconY = card.Y + (card.Height - 16) / 2;
        var iconRect = new Rectangle(card.X + IconX, iconY, 20, 16);
        TextRenderer.DrawText(g, icon, IconFont, iconRect, accentColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Description text — compute available width
        int descLeft = card.X + DescX;
        int rightEdge = rec.HasFix ? card.Right - FixMargin - FixWidth - 8 : card.Right - FixMargin;
        int descWidth = rightEdge - descLeft;
        var descRect = new Rectangle(descLeft, card.Y, descWidth, card.Height);
        TextRenderer.DrawText(g, rec.Title, DescFont, descRect, Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Fix button — always uses Theme.Accent (indigo), not severity color
        if (rec.HasFix)
            DrawFixButton(g, card, isFixHovered);
    }

    private static void DrawFixButton(Graphics g, Rectangle card, bool isHovered)
    {
        var fixRect = GetFixButtonRect(card);
        int alpha = isHovered ? 89 : 51; // 35% or 20% of 255
        using (var btnBrush = new SolidBrush(Color.FromArgb(alpha, Theme.Accent)))
            FillRoundedRect(g, fixRect, FixRadius, btnBrush);

        TextRenderer.DrawText(g, "Fix", FixFont, fixRect, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void DrawEmptyState(Graphics g, Rectangle bounds)
    {
        var cardRect = new Rectangle(bounds.X, bounds.Y, bounds.Width, CardHeight);

        // Background
        using (var bgBrush = new SolidBrush(Theme.BgCard))
            FillRoundedRect(g, cardRect, CardRadius, bgBrush);

        // Checkmark icon
        int iconY = cardRect.Y + (cardRect.Height - 16) / 2;
        var iconRect = new Rectangle(cardRect.X + IconX, iconY, 20, 16);
        TextRenderer.DrawText(g, "\u2713", IconFont, iconRect, Emerald,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Message
        var msgRect = new Rectangle(cardRect.X + DescX, cardRect.Y, cardRect.Width - DescX - FixMargin, cardRect.Height);
        TextRenderer.DrawText(g, "No issues \u2014 your PC is well configured", EmptyFont, msgRect, Emerald,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    // ── GPU-accelerated Direct2D path ────────────────────────────────

    public static void DrawD2D(
        GpuRenderer gpu,
        Rectangle bounds,
        IReadOnlyList<Recommendation> recs,
        int hoveredCard,
        int hoveredFix)
    {
        if (recs.Count == 0)
        {
            DrawEmptyStateD2D(gpu, bounds);
            return;
        }

        int count = Math.Min(recs.Count, MaxCards);
        for (int i = 0; i < count; i++)
        {
            var cardRect = GetCardRect(bounds, i);
            DrawCardD2D(gpu, cardRect, recs[i], isHovered: i == hoveredCard, isFixHovered: i == hoveredFix);
        }
    }

    private static void DrawCardD2D(GpuRenderer gpu, Rectangle card, Recommendation rec, bool isHovered, bool isFixHovered)
    {
        var accentColor = GetAccentColor(rec.Impact);

        // Card background
        gpu.FillRoundedRect(
            new RectangleF(card.X, card.Y, card.Width, card.Height),
            CardRadius,
            isHovered ? Theme.BgCardHover : Theme.BgCard);

        // Left accent bar
        gpu.FillRect(
            new RectangleF(card.X, card.Y + 2, AccentBarWidth, card.Height - 4),
            accentColor);

        // Icon
        string icon = rec.Impact > 7 ? "\u26A0" : rec.Impact >= 4 ? "\u26A0" : "\u2139";
        int iconY = card.Y + (card.Height - 16) / 2;
        gpu.DrawTextSimple(icon, "Segoe UI", 10f, accentColor, card.X + IconX, iconY);

        // Description text — clip to available width
        int descLeft = card.X + DescX;
        int rightEdge = rec.HasFix ? card.Right - FixMargin - FixWidth - 8 : card.Right - FixMargin;
        int descWidth = rightEdge - descLeft;
        int descY = card.Y + (card.Height - 14) / 2;
        var clipRect = new RectangleF(descLeft, card.Y, descWidth, card.Height);
        gpu.PushClip(clipRect);
        gpu.DrawTextSimple(rec.Title, "Segoe UI", 9f, Theme.TextPrimary, descLeft, descY);
        gpu.PopClip();

        // Fix button
        if (rec.HasFix)
            DrawFixButtonD2D(gpu, card, isFixHovered);
    }

    private static void DrawFixButtonD2D(GpuRenderer gpu, Rectangle card, bool isHovered)
    {
        var fixRect = GetFixButtonRect(card);
        int alpha = isHovered ? 89 : 51;
        gpu.FillRoundedRect(
            new RectangleF(fixRect.X, fixRect.Y, fixRect.Width, fixRect.Height),
            FixRadius,
            Color.FromArgb(alpha, Theme.Accent));

        var textSize = gpu.MeasureText("Fix", "Segoe UI Semibold", 8f, Vortice.DirectWrite.FontWeight.SemiBold);
        float tx = fixRect.X + (fixRect.Width - textSize.Width) / 2f;
        float ty = fixRect.Y + (fixRect.Height - textSize.Height) / 2f;
        gpu.DrawTextSimple("Fix", "Segoe UI Semibold", 8f, Theme.Accent, tx, ty, Vortice.DirectWrite.FontWeight.SemiBold);
    }

    private static void DrawEmptyStateD2D(GpuRenderer gpu, Rectangle bounds)
    {
        var cardRect = new RectangleF(bounds.X, bounds.Y, bounds.Width, CardHeight);

        // Background
        gpu.FillRoundedRect(cardRect, CardRadius, Theme.BgCard);

        // Checkmark icon
        int iconY = bounds.Y + (CardHeight - 16) / 2;
        gpu.DrawTextSimple("\u2713", "Segoe UI", 10f, Emerald, bounds.X + IconX, iconY);

        // Message
        gpu.DrawTextSimple(
            "No issues \u2014 your PC is well configured",
            "Segoe UI", 9f, Emerald,
            bounds.X + DescX, bounds.Y + (CardHeight - 14) / 2);
    }

    private static Color GetAccentColor(float impact)
    {
        if (impact > 7) return RedAccent;
        if (impact >= 4) return AmberAccent;
        return IndigoAccent;
    }

    private static void FillRoundedRect(Graphics g, Rectangle rect, int radius, Brush brush)
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
}
