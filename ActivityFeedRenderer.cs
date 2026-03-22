using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PCGuardian
{
    internal static class ActivityFeedRenderer
    {
        private const int RowHeight = 24;
        private const int MaxVisibleRows = 5;
        private const int DotDiameter = 6;
        private const int DotX = 3;
        private const int DotY = 9;
        private const int TimestampX = 14;
        private const int MessageX = 70;
        private const int TimestampWidth = 56;
        private const int ScrollbarWidth = 4;

        private static readonly Color ColorInfo = Color.FromArgb(99, 102, 241);
        private static readonly Color ColorSuccess = Color.FromArgb(16, 185, 129);
        private static readonly Color ColorWarning = Color.FromArgb(245, 158, 11);
        private static readonly Color ColorDanger = Color.FromArgb(239, 68, 68);
        private static readonly Color ColorTimestamp = Color.FromArgb(113, 113, 122);
        private static readonly Color ColorMessage = Color.FromArgb(200, 200, 210);
        private static readonly Color ColorHoverBg = Color.FromArgb(30, 30, 35);
        private static readonly Color ColorScrollThumb = Color.FromArgb(63, 63, 70);

        public static void Draw(
            Graphics g,
            Rectangle bounds,
            IReadOnlyList<ActivityEvent> events,
            int scrollOffset,
            int hoveredRow)
        {
            g.SetClip(bounds);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var fontTimestamp = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            using var fontMessage = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            using var brushTimestamp = new SolidBrush(ColorTimestamp);
            using var brushMessage = new SolidBrush(ColorMessage);
            using var brushHover = new SolidBrush(ColorHoverBg);

            int visibleCount = Math.Min(MaxVisibleRows, events.Count - scrollOffset);

            for (int i = 0; i < visibleCount; i++)
            {
                int eventIndex = scrollOffset + i;
                if (eventIndex >= events.Count) break;

                var evt = events[eventIndex];
                int rowY = bounds.Y + i * RowHeight;
                var rowRect = new Rectangle(bounds.X, rowY, bounds.Width, RowHeight);

                // Hover background
                if (eventIndex == hoveredRow)
                {
                    g.FillRectangle(brushHover, rowRect);
                }

                // Status dot
                using var dotBrush = new SolidBrush(GetSeverityColor(evt.Severity));
                g.FillEllipse(dotBrush, bounds.X + DotX, rowY + DotY, DotDiameter, DotDiameter);

                // Timestamp
                string timestamp = evt.Time.ToString("HH:mm");
                g.DrawString(timestamp, fontTimestamp, brushTimestamp, bounds.X + TimestampX, rowY + 5);

                // Message with ellipsis trimming
                int messageAreaWidth = bounds.Width - MessageX - (events.Count > MaxVisibleRows ? ScrollbarWidth + 2 : 0);
                var messageRect = new RectangleF(bounds.X + MessageX, rowY + 4, messageAreaWidth, RowHeight - 4);

                using var sf = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(evt.Message, fontMessage, brushMessage, messageRect, sf);
            }

            // Scrollbar
            if (events.Count > MaxVisibleRows)
            {
                DrawScrollbar(g, bounds, events.Count, scrollOffset);
            }

            g.ResetClip();
        }

        public static int HitTestRow(Rectangle bounds, Point mousePos, int scrollOffset)
        {
            if (!bounds.Contains(mousePos))
                return -1;

            int relativeY = mousePos.Y - bounds.Y;
            int row = relativeY / RowHeight;

            if (row < 0 || row >= MaxVisibleRows)
                return -1;

            return scrollOffset + row;
        }

        private static void DrawScrollbar(Graphics g, Rectangle bounds, int totalRows, int scrollOffset)
        {
            int trackHeight = MaxVisibleRows * RowHeight;
            int trackX = bounds.Right - ScrollbarWidth;
            int trackY = bounds.Y;

            float thumbRatio = (float)MaxVisibleRows / totalRows;
            int thumbHeight = Math.Max(8, (int)(trackHeight * thumbRatio));
            float scrollRatio = totalRows - MaxVisibleRows > 0
                ? (float)scrollOffset / (totalRows - MaxVisibleRows)
                : 0f;
            int thumbY = trackY + (int)((trackHeight - thumbHeight) * scrollRatio);

            using var thumbBrush = new SolidBrush(ColorScrollThumb);
            g.FillRectangle(thumbBrush, trackX, thumbY, ScrollbarWidth, thumbHeight);
        }

        private static Color GetSeverityColor(EventSeverity severity) => severity switch
        {
            EventSeverity.Info => ColorInfo,
            EventSeverity.Success => ColorSuccess,
            EventSeverity.Warning => ColorWarning,
            EventSeverity.Danger => ColorDanger,
            _ => ColorInfo
        };
    }
}
