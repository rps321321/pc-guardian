using System.Drawing;
using System.Drawing.Drawing2D;

namespace PCGuardian
{
    internal static class SparklineRenderer
    {
        // ── GPU-accelerated Direct2D path ────────────────────────────────

        public static void DrawD2D(GpuRenderer gpu, Rectangle bounds, float[] samples, Color lineColor, bool fillArea = true)
        {
            if (samples == null || samples.Length < 2)
                return;

            // Normalize Y values
            float min = samples[0];
            float max = samples[0];
            for (int i = 1; i < samples.Length; i++)
            {
                if (samples[i] < min) min = samples[i];
                if (samples[i] > max) max = samples[i];
            }

            float range = max - min;

            // Handle flat line: all values identical
            if (range == 0f)
            {
                float centerY = bounds.Top + bounds.Height / 2f;
                float stepX = samples.Length > 1
                    ? (float)bounds.Width / (samples.Length - 1)
                    : 0f;

                var pts = new PointF[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                    pts[i] = new PointF(bounds.Left + i * stepX, centerY);

                DrawD2DLines(gpu, pts, lineColor);
                gpu.FillEllipse(pts[pts.Length - 1], 2.5f, 2.5f, lineColor);
                return;
            }

            // Apply 10% padding to min/max
            float padding = range * 0.1f;
            min -= padding;
            max += padding;
            range = max - min;

            // Build point array
            float xStep = (float)bounds.Width / (samples.Length - 1);
            var points = new PointF[samples.Length];

            for (int i = 0; i < samples.Length; i++)
            {
                float x = bounds.Left + i * xStep;
                float y = bounds.Bottom - (samples[i] - min) / range * bounds.Height;
                points[i] = new PointF(x, y);
            }

            // Simplified area fill under the curve (30% alpha)
            if (fillArea)
            {
                Color fillColor = Color.FromArgb(77, lineColor); // ~30% alpha
                for (int i = 0; i < points.Length - 1; i++)
                {
                    // Fill vertical strips from line segment down to bottom
                    float left = points[i].X;
                    float right = points[i + 1].X;
                    float topY = Math.Min(points[i].Y, points[i + 1].Y);
                    gpu.FillRect(
                        new RectangleF(left, topY, right - left, bounds.Bottom - topY),
                        fillColor);
                }
            }

            // Draw the line segments
            DrawD2DLines(gpu, points, lineColor);

            // Current value dot at rightmost point
            gpu.FillEllipse(points[points.Length - 1], 2.5f, 2.5f, lineColor);
        }

        private static void DrawD2DLines(GpuRenderer gpu, PointF[] points, Color color)
        {
            for (int i = 0; i < points.Length - 1; i++)
                gpu.DrawLine(points[i], points[i + 1], color, 1.5f);
        }

        // ── GDI+ path (unchanged) ───────────────────────────────────────

        public static void Draw(Graphics g, Rectangle bounds, float[] samples, Color lineColor, bool fillGradient = true)
        {
            if (samples == null || samples.Length < 2)
                return;

            // Normalize Y values
            float min = samples[0];
            float max = samples[0];
            for (int i = 1; i < samples.Length; i++)
            {
                if (samples[i] < min) min = samples[i];
                if (samples[i] > max) max = samples[i];
            }

            float range = max - min;

            // Handle flat line: all values identical
            if (range == 0f)
            {
                float centerY = bounds.Top + bounds.Height / 2f;
                var points = new PointF[samples.Length];
                float stepX = samples.Length > 1
                    ? (float)bounds.Width / (samples.Length - 1)
                    : 0f;

                for (int i = 0; i < samples.Length; i++)
                    points[i] = new PointF(bounds.Left + i * stepX, centerY);

                DrawLine(g, points, lineColor);
                DrawDot(g, points[points.Length - 1], lineColor);
                return;
            }

            // Apply 10% padding to min/max
            float padding = range * 0.1f;
            min -= padding;
            max += padding;
            range = max - min;

            // Build point array
            float xStep = (float)bounds.Width / (samples.Length - 1);
            var pts = new PointF[samples.Length];

            for (int i = 0; i < samples.Length; i++)
            {
                float x = bounds.Left + i * xStep;
                float y = bounds.Bottom - (samples[i] - min) / range * bounds.Height;
                pts[i] = new PointF(x, y);
            }

            // Fill gradient under the line
            if (fillGradient)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddLines(pts);
                    path.AddLine(pts[pts.Length - 1].X, bounds.Bottom,
                                 pts[0].X, bounds.Bottom);
                    path.CloseFigure();

                    Color topColor = Color.FromArgb(100, lineColor);  // ~40% alpha
                    Color bottomColor = Color.FromArgb(0, lineColor); // 0% alpha

                    using (var brush = new LinearGradientBrush(
                        new Point(bounds.Left, bounds.Top),
                        new Point(bounds.Left, bounds.Bottom),
                        topColor,
                        bottomColor))
                    {
                        g.FillPath(brush, path);
                    }
                }
            }

            // Draw the line
            DrawLine(g, pts, lineColor);

            // Current value dot at rightmost point
            DrawDot(g, pts[pts.Length - 1], lineColor);
        }

        private static void DrawLine(Graphics g, PointF[] points, Color lineColor)
        {
            using (var pen = new Pen(lineColor, 2.0f))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, points);
            }
        }

        private static void DrawDot(Graphics g, PointF center, Color color)
        {
            const float diameter = 5f;
            float radius = diameter / 2f;

            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, center.X - radius, center.Y - radius, diameter, diameter);
            }
        }
    }
}
