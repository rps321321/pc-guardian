using System.Drawing;
using System.Drawing.Drawing2D;

namespace PCGuardian
{
    internal static class SparklineRenderer
    {
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

                    Color topColor = Color.FromArgb(77, lineColor);   // ~30% alpha
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
            using (var pen = new Pen(lineColor, 1.5f))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, points);
            }
        }

        private static void DrawDot(Graphics g, PointF center, Color color)
        {
            const float diameter = 4f;
            float radius = diameter / 2f;

            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, center.X - radius, center.Y - radius, diameter, diameter);
            }
        }
    }
}
