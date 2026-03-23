using System.Drawing;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;
using SizeF = System.Drawing.SizeF;
using PointF = System.Drawing.PointF;
using RectangleF = System.Drawing.RectangleF;

namespace PCGuardian;

internal sealed class GpuRenderer : IDisposable
{
    private readonly ID2D1Factory1 _factory;
    private readonly IDWriteFactory _writeFactory;
    private readonly ID2D1HwndRenderTarget _rt;
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushCache = new();
    private readonly Dictionary<string, IDWriteTextFormat> _textFormatCache = new();

    public bool IsAvailable { get; }

    private GpuRenderer(IntPtr hwnd, Size clientSize)
    {
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();

        var renderProps = new RenderTargetProperties
        {
            Type = RenderTargetType.Default,
            PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)
        };

        var hwndProps = new HwndRenderTargetProperties
        {
            Hwnd = hwnd,
            PixelSize = new SizeI(clientSize.Width, clientSize.Height),
            PresentOptions = PresentOptions.None
        };

        _rt = _factory.CreateHwndRenderTarget(renderProps, hwndProps);
        _rt.AntialiasMode = AntialiasMode.PerPrimitive;
        _rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Cleartype;

        IsAvailable = true;
    }

    public static GpuRenderer? TryCreate(IntPtr hwnd, Size clientSize)
    {
        try { return new GpuRenderer(hwnd, clientSize); }
        catch { return null; }
    }

    // ── Brush cache ──────────────────────────────────────────────────

    public ID2D1SolidColorBrush GetBrush(Color color)
    {
        uint key = (uint)color.ToArgb();
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = _rt.CreateSolidColorBrush(
                new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
            _brushCache[key] = brush;
        }
        return brush;
    }

    // ── Text format cache ────────────────────────────────────────────

    public IDWriteTextFormat GetTextFormat(string fontFamily, float size, FontWeight weight = FontWeight.Normal)
    {
        string key = $"{fontFamily}|{size}|{weight}";
        if (!_textFormatCache.TryGetValue(key, out var fmt))
        {
            fmt = _writeFactory.CreateTextFormat(
                fontFamily, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            _textFormatCache[key] = fmt;
        }
        return fmt;
    }

    // ── Drawing helpers ──────────────────────────────────────────────

    public void BeginDraw() => _rt.BeginDraw();

    public void EndDraw() => _rt.EndDraw();

    public void Clear(Color color) =>
        _rt.Clear(new Color4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));

    public void FillRoundedRect(RectangleF rect, float radius, Color color)
    {
        var rr = new RoundedRectangle(rect, radius, radius);
        _rt.FillRoundedRectangle(rr, GetBrush(color));
    }

    public void DrawRoundedRect(RectangleF rect, float radius, Color color, float strokeWidth = 1f)
    {
        var rr = new RoundedRectangle(rect, radius, radius);
        _rt.DrawRoundedRectangle(rr, GetBrush(color), strokeWidth);
    }

    public void FillRect(RectangleF rect, Color color) =>
        _rt.FillRectangle(rect, GetBrush(color));

    public void DrawLine(PointF p1, PointF p2, Color color, float strokeWidth = 1.5f) =>
        _rt.DrawLine(new Vector2(p1.X, p1.Y), new Vector2(p2.X, p2.Y), GetBrush(color), strokeWidth);

    public void DrawArc(RectangleF bounds, float startAngleDeg, float sweepAngleDeg, Color color, float strokeWidth)
    {
        if (MathF.Abs(sweepAngleDeg) < 0.1f) return;

        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float rx = bounds.Width / 2f;
        float ry = bounds.Height / 2f;

        // Full circle: ArcSegment can't handle identical start/end points
        if (MathF.Abs(sweepAngleDeg) >= 359.9f)
        {
            _rt.DrawEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), GetBrush(color), strokeWidth);
            return;
        }

        float startRad = startAngleDeg * MathF.PI / 180f;
        float endRad = (startAngleDeg + sweepAngleDeg) * MathF.PI / 180f;

        var startPt = new Vector2(cx + rx * MathF.Cos(startRad), cy + ry * MathF.Sin(startRad));
        var endPt = new Vector2(cx + rx * MathF.Cos(endRad), cy + ry * MathF.Sin(endRad));

        using var geo = _factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(startPt, FigureBegin.Hollow);
        sink.AddArc(new ArcSegment
        {
            Point = endPt,
            Size = new Vortice.Mathematics.Size(rx, ry),
            RotationAngle = 0,
            SweepDirection = sweepAngleDeg > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
            ArcSize = MathF.Abs(sweepAngleDeg) > 180 ? ArcSize.Large : ArcSize.Small
        });
        sink.EndFigure(FigureEnd.Open);
        sink.Close();

        using var strokeStyle = _factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round
        });
        _rt.DrawGeometry(geo, GetBrush(color), strokeWidth, strokeStyle);
    }

    public void FillEllipse(PointF center, float rx, float ry, Color color) =>
        _rt.FillEllipse(
            new Ellipse(new Vector2(center.X, center.Y), rx, ry),
            GetBrush(color));

    public void DrawText(
        string text, string fontFamily, float fontSize, Color color, RectangleF rect,
        FontWeight weight = FontWeight.Normal,
        TextAlignment hAlign = TextAlignment.Leading,
        ParagraphAlignment vAlign = ParagraphAlignment.Near)
    {
        var fmt = GetTextFormat(fontFamily, fontSize, weight);
        fmt.TextAlignment = hAlign;
        fmt.ParagraphAlignment = vAlign;
        _rt.DrawText(
            text,
            fmt,
            new Rect(rect),
            GetBrush(color));
    }

    public void DrawTextSimple(
        string text, string fontFamily, float fontSize, Color color, float x, float y,
        FontWeight weight = FontWeight.Normal)
    {
        var fmt = GetTextFormat(fontFamily, fontSize, weight);
        fmt.TextAlignment = TextAlignment.Leading;
        fmt.ParagraphAlignment = ParagraphAlignment.Near;
        _rt.DrawText(
            text,
            fmt,
            new Rect(x, y, x + 4096f, y + 4096f),
            GetBrush(color));
    }

    public SizeF MeasureText(string text, string fontFamily, float fontSize, FontWeight weight = FontWeight.Normal)
    {
        var fmt = GetTextFormat(fontFamily, fontSize, weight);
        using var layout = _writeFactory.CreateTextLayout(text, fmt, float.MaxValue, float.MaxValue);
        var metrics = layout.Metrics;
        return new SizeF(metrics.Width, metrics.Height);
    }

    public void PushClip(RectangleF rect) =>
        _rt.PushAxisAlignedClip(rect, AntialiasMode.PerPrimitive);

    public void PopClip() => _rt.PopAxisAlignedClip();

    // ── Resize ───────────────────────────────────────────────────────

    public void Resize(Size newSize) =>
        _rt.Resize(new SizeI(newSize.Width, newSize.Height));

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var brush in _brushCache.Values)
            brush.Dispose();
        _brushCache.Clear();

        foreach (var fmt in _textFormatCache.Values)
            fmt.Dispose();
        _textFormatCache.Clear();

        _rt.Dispose();
        _writeFactory.Dispose();
        _factory.Dispose();
    }
}
