using System.Drawing;
using System.Drawing.Drawing2D;

namespace PCGuardian
{
    internal static class SecurityTileRenderer
    {
        private const int TileW = 72;
        private const int TileH = 40;
        private const int GapX = 4;
        private const int GapY = 6;
        private const int Cols = 8;
        private const int Rows = 2;
        private const int CornerRadius = 6;
        private const int AccentBarWidth = 3;
        private const int AccentBarPadY = 4;

        private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rdp"] = "RDP",
            ["remote-apps"] = "Apps",
            ["ports"] = "Ports",
            ["connections"] = "Conn",
            ["shares"] = "Share",
            ["services"] = "Svc",
            ["firewall"] = "FW",
            ["users"] = "Users",
            ["startup"] = "Start",
            ["tasks"] = "Tasks",
            ["antivirus"] = "AV",
            ["dns"] = "DNS",
            ["usb"] = "USB",
            ["hardware"] = "HW",
            ["security-posture"] = "Sec",
            ["security-events"] = "Evts",
        };

        public static void Draw(
            Graphics g,
            Rectangle bounds,
            IReadOnlyList<CategoryTileData> tiles,
            int hoveredIndex,
            float pulseAlpha)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var labelFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Regular, GraphicsUnit.Point);

            int count = Math.Min(tiles.Count, Cols * Rows);

            for (int i = 0; i < count; i++)
            {
                int col = i % Cols;
                int row = i / Cols;

                int x = bounds.X + col * (TileW + GapX);
                int y = bounds.Y + row * (TileH + GapY);
                var tileRect = new Rectangle(x, y, TileW, TileH);

                var tile = tiles[i];
                bool isHovered = i == hoveredIndex;
                Color statusColor = Theme.StatusColor(tile.Status);

                // 1. Background — rounded rect with status-tinted fill
                int bgAlpha = ComputeBgAlpha(tile.Status, isHovered, pulseAlpha);
                Color bgColor = Color.FromArgb(bgAlpha, statusColor);

                using (var bgBrush = new SolidBrush(bgColor))
                using (var path = RoundedRect(tileRect, CornerRadius))
                {
                    g.FillPath(bgBrush, path);
                }

                // 2. Left accent bar — 3px wide, full status color, 4px padding top/bottom
                var barRect = new Rectangle(
                    x + 2,
                    y + AccentBarPadY,
                    AccentBarWidth,
                    TileH - AccentBarPadY * 2);

                using (var barBrush = new SolidBrush(statusColor))
                {
                    g.FillRectangle(barBrush, barRect);
                }

                // 3. Status icon + abbreviated label — centered in tile
                string icon = tile.Status switch
                {
                    Status.Safe => "\u2713",
                    Status.Warning => "!",
                    Status.Danger => "\u2715",
                    _ => "",
                };

                string abbr = Abbreviations.TryGetValue(tile.Id, out var a) ? a : tile.Id;
                string text = $"{icon} {abbr}";

                using (var textBrush = new SolidBrush(statusColor))
                {
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                    };
                    g.DrawString(text, labelFont, textBrush, tileRect, sf);
                }

                // 5. Hovered tile border — 1px border at 50% alpha
                if (isHovered)
                {
                    Color borderColor = Color.FromArgb(128, statusColor);
                    using var borderPen = new Pen(borderColor, 1f);
                    using var borderPath = RoundedRect(
                        Rectangle.Inflate(tileRect, -1, -1), CornerRadius);
                    g.DrawPath(borderPen, borderPath);
                }
            }
        }

        public static int HitTest(Rectangle gridBounds, Point mousePos, int tileCount)
        {
            int relX = mousePos.X - gridBounds.X;
            int relY = mousePos.Y - gridBounds.Y;

            if (relX < 0 || relY < 0)
                return -1;

            int col = relX / (TileW + GapX);
            int row = relY / (TileH + GapY);

            if (col >= Cols || row >= Rows)
                return -1;

            // Verify click is within the tile area, not in the gap
            int localX = relX - col * (TileW + GapX);
            int localY = relY - row * (TileH + GapY);

            if (localX > TileW || localY > TileH)
                return -1;

            int index = row * Cols + col;
            return index < tileCount ? index : -1;
        }

        private static int ComputeBgAlpha(Status status, bool isHovered, float pulseAlpha)
        {
            if (isHovered)
                return (int)(0.30f * 255);

            return status switch
            {
                Status.Danger => (int)(pulseAlpha * 255),
                _ => (int)(0.15f * 255), // Safe + Warning both 15%
            };
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();

            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }
    }
}
