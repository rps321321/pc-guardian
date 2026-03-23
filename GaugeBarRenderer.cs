using System.Drawing.Drawing2D;
using Vortice.DirectWrite;

namespace PCGuardian;

internal static class GaugeBarRenderer
{
    private static readonly Color Emerald = Color.FromArgb(16, 185, 129);
    private static readonly Color Amber = Color.FromArgb(245, 158, 11);
    private static readonly Color Orange = Color.FromArgb(249, 115, 22);
    private static readonly Color Red = Color.FromArgb(239, 68, 68);
    private static readonly Color Violet = Color.FromArgb(168, 85, 247);

    private static readonly Font LabelFont = new("Segoe UI", 9f);
    private static readonly Font ValueFont = new("Segoe UI Semibold", 9f);

    public static void DisposeResources()
    {
        LabelFont.Dispose();
        ValueFont.Dispose();
    }

    private const int LabelWidth = 48;
    private const int DefaultValueWidth = 72;
    private const int TrackHeight = 12;
    private const int CornerRadius = 6;

    public static void Draw(
        Graphics g,
        Rectangle bounds,
        string label,
        float value,
        float displayValue,
        string valueText,
        bool isDisk = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // --- Measure value text to allocate enough space ---
        int valueWidth = Math.Max(
            DefaultValueWidth,
            (int)Math.Ceiling(g.MeasureString(valueText, ValueFont).Width) + 4);

        // --- Label (left) ---
        var labelRect = new Rectangle(bounds.X, bounds.Y, LabelWidth, bounds.Height);
        TextRenderer.DrawText(
            g, label, LabelFont, labelRect, Color.FromArgb(161, 161, 170),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // --- Track (center) ---
        int trackWidth = bounds.Width - LabelWidth - valueWidth;
        int trackX = bounds.X + LabelWidth;
        int trackY = bounds.Y + (bounds.Height - TrackHeight) / 2;
        var trackRect = new Rectangle(trackX, trackY, trackWidth, TrackHeight);

        using (var trackBrush = new SolidBrush(Theme.Border))
            DrawRoundedRect(g, trackRect, CornerRadius, trackBrush);

        // --- Fill bar ---
        float clampedDisplay = Math.Clamp(displayValue, 0f, 100f);
        int fillWidth = (int)(clampedDisplay / 100f * trackWidth);

        if (fillWidth > 0)
        {
            var fillRect = new Rectangle(trackX, trackY, fillWidth, TrackHeight);

            if (isDisk)
            {
                // Dual-colored bar: left half indigo (read), right half violet (write)
                int halfWidth = fillWidth / 2;
                if (halfWidth > 0)
                {
                    var readRect = new Rectangle(trackX, trackY, halfWidth, TrackHeight);
                    using var readBrush = new SolidBrush(Theme.Accent);
                    DrawRoundedRect(g, readRect, CornerRadius, readBrush);
                }

                int writeWidth = fillWidth - halfWidth;
                if (writeWidth > 0)
                {
                    var writeRect = new Rectangle(trackX + halfWidth, trackY, writeWidth, TrackHeight);
                    using var writeBrush = new SolidBrush(Violet);
                    DrawRoundedRect(g, writeRect, CornerRadius, writeBrush);
                }
            }
            else
            {
                using var fillBrush = new SolidBrush(GetBarColor(value));
                DrawRoundedRect(g, fillRect, CornerRadius, fillBrush);
            }
        }

        // --- Value text (right) ---
        var valueRect = new Rectangle(bounds.X + LabelWidth + trackWidth, bounds.Y, valueWidth, bounds.Height);
        TextRenderer.DrawText(
            g, valueText, ValueFont, valueRect, Color.White,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    public static void DrawD2D(
        GpuRenderer gpu,
        Rectangle bounds,
        string label,
        float value,
        float displayValue,
        string valueText,
        bool isDisk = false)
    {
        // --- Measure value text to allocate enough space ---
        var valueSize = gpu.MeasureText(valueText, "Segoe UI Semibold", 13f, FontWeight.SemiBold);
        int valueWidth = Math.Max(DefaultValueWidth, (int)Math.Ceiling(valueSize.Width) + 4);

        // --- Label (left) ---
        float labelY = bounds.Y + (bounds.Height - gpu.MeasureText(label, "Segoe UI", 13f).Height) / 2f;
        gpu.DrawTextSimple(label, "Segoe UI", 13f, Theme.TextSecondary, bounds.X, labelY);

        // --- Track (center) ---
        int trackWidth = bounds.Width - LabelWidth - valueWidth;
        int trackX = bounds.X + LabelWidth;
        int trackY = bounds.Y + (bounds.Height - TrackHeight) / 2;
        var trackRect = new RectangleF(trackX, trackY, trackWidth, TrackHeight);

        gpu.FillRoundedRect(trackRect, 6f, Theme.Border);

        // --- Fill bar ---
        float clampedDisplay = Math.Clamp(displayValue, 0f, 100f);
        int fillWidth = (int)(clampedDisplay / 100f * trackWidth);

        if (fillWidth > 0)
        {
            if (isDisk)
            {
                int halfWidth = fillWidth / 2;
                if (halfWidth > 0)
                {
                    var readRect = new RectangleF(trackX, trackY, halfWidth, TrackHeight);
                    gpu.FillRoundedRect(readRect, 6f, Theme.Accent);
                }

                int writeWidth = fillWidth - halfWidth;
                if (writeWidth > 0)
                {
                    var writeRect = new RectangleF(trackX + halfWidth, trackY, writeWidth, TrackHeight);
                    gpu.FillRoundedRect(writeRect, 6f, Violet);
                }
            }
            else
            {
                Color gaugeColor = GetBarColor(value);
                var fillRect = new RectangleF(trackX, trackY, fillWidth, TrackHeight);
                gpu.FillRoundedRect(fillRect, 6f, gaugeColor);
            }
        }

        // --- Value text (right) ---
        float valTextY = bounds.Y + (bounds.Height - valueSize.Height) / 2f;
        float valTextX = bounds.X + LabelWidth + trackWidth + valueWidth - valueSize.Width;
        gpu.DrawTextSimple(valueText, "Segoe UI Semibold", 13f, Theme.TextPrimary, valTextX, valTextY, FontWeight.SemiBold);
    }

    private static Color GetBarColor(float value)
    {
        return value switch
        {
            < 50f => LerpColor(Emerald, Amber, value / 50f),
            < 75f => LerpColor(Amber, Orange, (value - 50f) / 25f),
            < 90f => LerpColor(Orange, Red, (value - 75f) / 15f),
            _ => Red
        };
    }

    private static Color LerpColor(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
    }

    private static void DrawRoundedRect(Graphics g, Rectangle rect, int radius, Brush brush)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        int d = radius * 2;
        // Clamp diameter so it doesn't exceed the rect dimensions
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
