using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using DWFontWeight = Vortice.DirectWrite.FontWeight;

namespace PCGuardian
{
    internal static class ScoreRingRenderer
    {
        private const int RingWidth = 10;
        private const int GlowWidth = 20;

        // ── GPU-accelerated Direct2D path ────────────────────────────────

        public static void DrawD2D(GpuRenderer gpu, Rectangle bounds, float score, string grade, float animProgress)
        {
            var innerBounds = new RectangleF(
                bounds.X + RingWidth / 2f,
                bounds.Y + RingWidth / 2f,
                bounds.Width - RingWidth,
                bounds.Height - RingWidth);

            float sweepAngle = (score / 100f) * 360f * animProgress;
            Color scoreColor = ScoreColor(score);

            // 1. Background track -- full circle
            gpu.DrawArc(innerBounds, 0, 360, Theme.Border, RingWidth);

            // 2. Glow effect -- drawn before main arc
            if (sweepAngle > 0.1f)
                gpu.DrawArc(innerBounds, -90, sweepAngle, Color.FromArgb(38, scoreColor), GlowWidth);

            // 3. Score arc -- sweeps from 12 o'clock
            if (sweepAngle > 0.1f)
                gpu.DrawArc(innerBounds, -90, sweepAngle, scoreColor, RingWidth);

            // 4. Center text -- score number + grade letter
            float cx = bounds.X + bounds.Width / 2f;
            float cy = bounds.Y + bounds.Height / 2f;

            string scoreText = ((int)(score * animProgress)).ToString();
            var numberSize = gpu.MeasureText(scoreText, "Segoe UI", 42f, DWFontWeight.Bold);
            float numberX = cx - numberSize.Width / 2f;
            float numberY = cy - numberSize.Height / 2f - 12f;
            gpu.DrawTextSimple(scoreText, "Segoe UI", 42f, Color.White, numberX, numberY, DWFontWeight.Bold);

            var gradeSize = gpu.MeasureText(grade, "Segoe UI Semibold", 18f, DWFontWeight.Normal);
            float gradeX = cx - gradeSize.Width / 2f;
            float gradeY = cy - 12f + numberSize.Height / 2f + 8f;
            gpu.DrawTextSimple(grade, "Segoe UI Semibold", 18f, scoreColor, gradeX, gradeY, DWFontWeight.Normal);

            // 5. Friendly label below the ring
            string label = score switch
            {
                >= 95 => "Excellent",
                >= 80 => "Good",
                >= 60 => "Fair",
                >= 40 => "Poor",
                _ => "Critical"
            };

            var labelSize = gpu.MeasureText(label, "Segoe UI", 13f, DWFontWeight.Normal);
            float labelX = bounds.X + (bounds.Width - labelSize.Width) / 2f;
            float labelY = bounds.Bottom + 8f;
            gpu.DrawTextSimple(label, "Segoe UI", 13f, Theme.TextSecondary, labelX, labelY, DWFontWeight.Normal);
        }

        // ── GDI+ path (unchanged) ────────────────────────────────────────

        public static void Draw(Graphics g, Rectangle bounds, float score, string grade, float animProgress)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var innerBounds = Rectangle.Inflate(bounds, -RingWidth / 2, -RingWidth / 2);
            float easedProgress = EaseOutCubic(animProgress);
            float sweepAngle = (score / 100f) * 360f * easedProgress;
            Color scoreColor = ScoreColor(score);

            // 1. Background track — full circle
            using (var trackPen = new Pen(Theme.Border, RingWidth))
            {
                trackPen.StartCap = LineCap.Round;
                trackPen.EndCap = LineCap.Round;
                g.DrawArc(trackPen, innerBounds, 0, 360);
            }

            // 3. Glow effect — drawn before main arc
            if (sweepAngle > 0.1f)
            {
                using var glowPen = new Pen(Color.FromArgb(38, scoreColor), GlowWidth);
                glowPen.StartCap = LineCap.Round;
                glowPen.EndCap = LineCap.Round;
                g.DrawArc(glowPen, innerBounds, -90, sweepAngle);
            }

            // 2. Score arc — sweeps from 12 o'clock
            if (sweepAngle > 0.1f)
            {
                using var scorePen = new Pen(scoreColor, RingWidth);
                scorePen.StartCap = LineCap.Round;
                scorePen.EndCap = LineCap.Round;
                g.DrawArc(scorePen, innerBounds, -90, sweepAngle);
            }

            // 4. Center text — score number + grade letter
            float cx = bounds.X + bounds.Width / 2f;
            float cy = bounds.Y + bounds.Height / 2f;

            float scoreNumberHeight;
            using (var scoreFontLarge = new Font("Segoe UI", 36f, FontStyle.Bold, GraphicsUnit.Point))
            using (var whiteBrush = new SolidBrush(Color.White))
            {
                string scoreText = ((int)(score * easedProgress)).ToString();
                var numberSize = g.MeasureString(scoreText, scoreFontLarge);
                scoreNumberHeight = numberSize.Height;
                float numberX = cx - numberSize.Width / 2f;
                float numberY = cy - numberSize.Height / 2f - 12f;
                g.DrawString(scoreText, scoreFontLarge, whiteBrush, numberX, numberY);
            }

            using (var gradeFont = new Font("Segoe UI Semibold", 14f, FontStyle.Regular, GraphicsUnit.Point))
            using (var gradeBrush = new SolidBrush(scoreColor))
            {
                var gradeSize = g.MeasureString(grade, gradeFont);
                float gradeX = cx - gradeSize.Width / 2f;
                float gradeY = cy - 12f + scoreNumberHeight / 2f + 8f;
                g.DrawString(grade, gradeFont, gradeBrush, gradeX, gradeY);
            }

            // 5. Friendly label below the ring
            string label = score switch
            {
                >= 95 => "Excellent",
                >= 80 => "Good",
                >= 60 => "Fair",
                >= 40 => "Poor",
                _ => "Critical"
            };

            using (var labelFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var labelBrush = new SolidBrush(Theme.TextSecondary))
            {
                var labelSize = g.MeasureString(label, labelFont);
                float labelX = bounds.X + (bounds.Width - labelSize.Width) / 2f;
                float labelY = bounds.Bottom + 8f;
                g.DrawString(label, labelFont, labelBrush, labelX, labelY);
            }
        }

        private static Color ScoreColor(float score) => score switch
        {
            >= 80 => Color.FromArgb(16, 185, 129),
            >= 60 => Color.FromArgb(245, 158, 11),
            >= 40 => Color.FromArgb(249, 115, 22),
            _ => Color.FromArgb(239, 68, 68),
        };

        private static Color LerpColor(Color a, Color b, float t) =>
            Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));

        private static float EaseOutCubic(float t)
        {
            float t1 = t - 1f;
            return t1 * t1 * t1 + 1f;
        }
    }
}
