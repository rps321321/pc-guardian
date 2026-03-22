namespace PCGuardian;

/// <summary>
/// Creates a dark-themed ToolTip that properly measures multi-line text
/// so nothing gets clipped. Reuse via DarkTooltip.Create() per form.
/// </summary>
internal static class DarkTooltip
{
    static readonly Color BgColor = Color.FromArgb(28, 28, 33);
    static readonly Color BorderColor = Color.FromArgb(55, 55, 62);
    static readonly Color TextColor = Color.FromArgb(225, 225, 230);
    static readonly Font TipFont = new("Segoe UI", 9f);

    const int PadX = 12;
    const int PadY = 8;
    const int MaxWidth = 340;

    public static ToolTip Create()
    {
        var tip = new ToolTip
        {
            AutoPopDelay = 10000,
            InitialDelay = 350,
            ReshowDelay = 150,
            OwnerDraw = true,
            UseAnimation = false,
            UseFading = false,
        };

        tip.Popup += OnPopup;
        tip.Draw += OnDraw;

        return tip;
    }

    /// <summary>
    /// Measures the text and sets the tooltip window size before it shows.
    /// This is the key fix — without this, multi-line tooltips get clipped.
    /// </summary>
    static void OnPopup(object? sender, PopupEventArgs e)
    {
        if (sender is not ToolTip tt) return;
        var text = tt.GetToolTip(e.AssociatedControl);
        if (string.IsNullOrEmpty(text)) return;

        using var gfx = Graphics.FromHwnd(IntPtr.Zero);
        var measured = gfx.MeasureString(text, TipFont, MaxWidth);

        int w = (int)measured.Width + PadX * 2 + 2;
        int h = (int)measured.Height + PadY * 2 + 2;

        e.ToolTipSize = new Size(w, h);
    }

    static void OnDraw(object? sender, DrawToolTipEventArgs e)
    {
        // Background
        using var bgBrush = new SolidBrush(BgColor);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        // Border
        using var borderPen = new Pen(BorderColor);
        e.Graphics.DrawRectangle(borderPen,
            e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);

        // Text — draw within padded area, wrapping at MaxWidth
        using var textBrush = new SolidBrush(TextColor);
        var textRect = new RectangleF(
            e.Bounds.X + PadX,
            e.Bounds.Y + PadY,
            e.Bounds.Width - PadX * 2,
            e.Bounds.Height - PadY * 2);

        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        e.Graphics.DrawString(e.ToolTipText, TipFont, textBrush, textRect);
    }
}
