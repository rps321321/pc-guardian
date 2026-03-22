using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PCGuardian;

internal sealed class DashboardPanel : UserControl
{
    readonly DashboardEngine _engine;
    DashboardState _state;
    readonly System.Windows.Forms.Timer _animTimer;
    readonly List<HitRegion> _hitRegions = new();

    // Score history (circular buffer for sparkline)
    readonly float[] _scoreHistory = new float[60];
    int _scoreHistoryIndex;
    int _scoreHistoryCount;

    // Animation tracking
    DateTime _animStartTime = DateTime.UtcNow;
    DateTime _gaugeAnimStart = DateTime.UtcNow;
    DateTime _pulseStart = DateTime.UtcNow;
    bool _entranceComplete;
    bool _gaugeComplete;
    bool _hasDangerTiles;

    // Hover state
    string? _hoveredId;

    // Activity feed scroll offset
    int _activityScrollOffset;

    // Layout constants
    const int PanelWidth = 640;
    const int Margin = 16;
    const int ContentWidth = PanelWidth - Margin * 2; // 608

    // Fonts (use Theme statics where available, create extras as needed)
    static readonly Font _sectionFont = new("Segoe UI Semibold", 10f);
    static readonly Font _smallFont = Theme.Small;
    static readonly Font _tileFont = new("Segoe UI", 7.5f);
    static readonly Font _btnFont = new("Segoe UI Semibold", 9f);
    static readonly Font _feedFont = new("Segoe UI", 8.5f);
    static readonly Font _trendFont = new("Segoe UI Semibold", 9f);
    static readonly Font _recFont = new("Segoe UI", 9f);

    // Events
    public event Action? ScanRequested;
    public event Action? ActivityRequested;
    public event Action? NetworkRequested;
    public event Action? SettingsRequested;

    public DashboardPanel(DashboardEngine engine)
    {
        _engine = engine;
        _state = engine.GetState();

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        BackColor = Theme.BgPrimary;
        Dock = DockStyle.Fill;

        _engine.StateChanged += OnEngineStateChanged;

        _animTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();

        _animStartTime = DateTime.UtcNow;
        _gaugeAnimStart = DateTime.UtcNow;
        _pulseStart = DateTime.UtcNow;
    }

    void OnEngineStateChanged(DashboardState newState)
    {
        BeginInvoke(() =>
        {
            _state = newState;
            PushScoreHistory(newState.ThreatScore);
            _hasDangerTiles = newState.Tiles?.Any(t => t.Status == Status.Danger) == true;
            Invalidate();
        });
    }

    void PushScoreHistory(float score)
    {
        _scoreHistory[_scoreHistoryIndex] = score;
        _scoreHistoryIndex = (_scoreHistoryIndex + 1) % _scoreHistory.Length;
        if (_scoreHistoryCount < _scoreHistory.Length)
            _scoreHistoryCount++;
    }

    void OnAnimTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var entranceElapsed = (now - _animStartTime).TotalMilliseconds;
        var gaugeElapsed = (now - _gaugeAnimStart).TotalMilliseconds;

        bool needsRepaint = false;

        if (!_entranceComplete && entranceElapsed < 800)
            needsRepaint = true;
        else if (!_entranceComplete)
            _entranceComplete = true;

        if (!_gaugeComplete && gaugeElapsed < 300)
            needsRepaint = true;
        else if (!_gaugeComplete)
            _gaugeComplete = true;

        // Danger tile pulse runs continuously at 1Hz
        if (_hasDangerTiles)
            needsRepaint = true;

        if (needsRepaint)
            Invalidate();

        // Stop timer only when all animations done AND no danger tiles
        if (_entranceComplete && _gaugeComplete && !_hasDangerTiles)
            _animTimer.Stop();
    }

    public void UpdateState(DashboardState state)
    {
        _state = state;
        PushScoreHistory(state.ThreatScore);
        _hasDangerTiles = state.Tiles?.Any(t => t.Status == Status.Danger) == true;

        if (!_animTimer.Enabled && _hasDangerTiles)
            _animTimer.Start();

        Invalidate();
    }

    // ── Paint ────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        _hitRegions.Clear();

        DrawTitle(g);
        DrawScoreRing(g, _state.ThreatScore, _state.Grade);
        DrawSparkline(g, _scoreHistory);
        DrawTrendArrow(g, _state.Trend, _state.TrendDelta);
        DrawGauges(g, _state.CpuPercent, _state.GpuPercent, _state.RamPercent, _state.DiskMBps);
        DrawSectionHeader(g, 308, "Security Posture", $"{_state.SecurityPassed}/{_state.SecurityTotal} \u2713");
        DrawSecurityTiles(g, _state.Tiles);
        DrawSectionHeader(g, 422, "Recent Activity");
        DrawActivityFeed(g, _state.RecentActivity);
        DrawSectionHeader(g, 570, "Recommendations");
        DrawRecommendations(g, _state.TopRecommendations);
        DrawQuickActions(g);
    }

    // ── Title bar (Y=0..40) ─────────────────────────────────────────

    void DrawTitle(Graphics g)
    {
        using var brush = new SolidBrush(Theme.TextPrimary);
        g.DrawString("PC Guardian", Theme.Title, brush, Margin, 8);

        // Gear icon (settings) — right side
        var gearRect = new Rectangle(PanelWidth - 72, 8, 28, 28);
        using var iconBrush = new SolidBrush(Theme.TextSecondary);
        g.DrawString("\u2699", Theme.Icon, iconBrush, gearRect.X, gearRect.Y);
        _hitRegions.Add(new HitRegion(gearRect, "settings", () => SettingsRequested?.Invoke()));

        // Moon icon (theme toggle) — right of gear
        var moonRect = new Rectangle(PanelWidth - 40, 8, 28, 28);
        g.DrawString("\U0001F319", Theme.Icon, iconBrush, moonRect.X, moonRect.Y);
        _hitRegions.Add(new HitRegion(moonRect, "theme-toggle", () =>
        {
            Theme.SetDark(!Theme.IsDark);
            BackColor = Theme.BgPrimary;
            Invalidate();
        }));
    }

    // ── Score ring (Y=50..210) ──────────────────────────────────────

    void DrawScoreRing(Graphics g, float score, string grade)
    {
        float progress = _entranceComplete
            ? 1f
            : EaseOutCubic((float)Math.Min((DateTime.UtcNow - _animStartTime).TotalMilliseconds / 800.0, 1.0));

        float animatedScore = score * progress;

        ScoreRingRenderer.Draw(g, new Rectangle(120, 50, 160, 160), animatedScore, grade, progress);
    }

    // ── Sparkline (Y=50..210, right side) ───────────────────────────

    void DrawSparkline(Graphics g, float[] history)
    {
        // Build ordered samples from the circular buffer
        var samples = new float[_scoreHistoryCount];
        for (int i = 0; i < _scoreHistoryCount; i++)
        {
            int idx = (_scoreHistoryIndex - _scoreHistoryCount + i + _scoreHistory.Length) % _scoreHistory.Length;
            samples[i] = history[idx];
        }
        SparklineRenderer.Draw(g, new Rectangle(380, 80, 140, 48), samples, Theme.Accent);
    }

    // ── Trend arrow ─────────────────────────────────────────────────

    void DrawTrendArrow(Graphics g, TrendDirection trend, float delta)
    {
        int x = 380, y = 136;
        var color = trend switch
        {
            TrendDirection.Improving => Theme.Safe,
            TrendDirection.Degrading => Theme.Danger,
            _ => Theme.TextMuted,
        };

        string arrow = trend switch
        {
            TrendDirection.Improving => "\u2191",
            TrendDirection.Degrading => "\u2193",
            _ => "\u2192",
        };

        using var brush = new SolidBrush(color);
        g.DrawString($"{arrow} {delta:+0.0;-0.0;0}", _trendFont, brush, x, y);
    }

    // ── Gauge bars (Y=220..300, 2x2 grid) ──────────────────────────

    void DrawGauges(Graphics g, float cpu, float gpu, float ram, float disk)
    {
        float gaugeProgress = _gaugeComplete
            ? 1f
            : EaseOutCubic((float)Math.Min((DateTime.UtcNow - _gaugeAnimStart).TotalMilliseconds / 300.0, 1.0));

        int gaugeW = 296, gaugeH = 32, gapX = 16, gapY = 8;
        int x0 = Margin, x1 = Margin + gaugeW + gapX;
        int y0 = 220, y1 = y0 + gaugeH + gapY;

        GaugeBarRenderer.Draw(g, new Rectangle(x0, y0, gaugeW, gaugeH), "CPU", cpu, cpu * gaugeProgress, $"{cpu:F0}%");
        GaugeBarRenderer.Draw(g, new Rectangle(x1, y0, gaugeW, gaugeH), "GPU", gpu, gpu * gaugeProgress, $"{gpu:F0}%");
        GaugeBarRenderer.Draw(g, new Rectangle(x0, y1, gaugeW, gaugeH), "RAM", ram, ram * gaugeProgress, $"{ram}%");
        GaugeBarRenderer.Draw(g, new Rectangle(x1, y1, gaugeW, gaugeH), "DISK", disk, disk * gaugeProgress, $"{disk:F1} MB/s", isDisk: true);
    }

    // ── Section header ──────────────────────────────────────────────

    void DrawSectionHeader(Graphics g, int y, string text, string? rightText = null)
    {
        using var secondaryBrush = new SolidBrush(Theme.TextSecondary);
        using var mutedBrush = new SolidBrush(Theme.TextMuted);
        using var borderPen = new Pen(Theme.Border, 1);

        g.DrawString(text, _sectionFont, secondaryBrush, Margin, y);

        if (rightText != null)
        {
            var size = g.MeasureString(rightText, _smallFont);
            g.DrawString(rightText, _smallFont, mutedBrush, PanelWidth - Margin - size.Width, y + 2);
        }

        g.DrawLine(borderPen, Margin, y + 18, PanelWidth - Margin, y + 18);
    }

    // ── Security tiles (Y=328..414, 8 per row) ─────────────────────

    void DrawSecurityTiles(Graphics g, IReadOnlyList<CategoryTileData>? tiles)
    {
        if (tiles == null || tiles.Count == 0) return;

        float pulseAlpha = _hasDangerTiles
            ? 0.5f + 0.5f * (float)Math.Sin((DateTime.UtcNow - _pulseStart).TotalSeconds * Math.PI * 2) // 1Hz
            : 1f;

        var gridBounds = new Rectangle(Margin, 328, ContentWidth, 92);

        // Determine hovered tile index
        int hoveredIndex = -1;
        for (int i = 0; i < tiles.Count && i < 16; i++)
        {
            if (_hoveredId == $"tile-{i}") { hoveredIndex = i; break; }
        }

        SecurityTileRenderer.Draw(g, gridBounds, tiles, hoveredIndex, pulseAlpha);

        // Register hit regions for each tile
        int tileW = 72, tileH = 40, gap = 4;
        for (int i = 0; i < tiles.Count && i < 16; i++)
        {
            int col = i % 8;
            int row = i / 8;
            int x = Margin + col * (tileW + gap);
            int y = 328 + row * (tileH + 6);
            var rect = new Rectangle(x, y, tileW, tileH);
            _hitRegions.Add(new HitRegion(rect, $"tile-{i}", null));
        }
    }

    // ── Activity feed (Y=442..562, 5 rows at 24px) ─────────────────

    void DrawActivityFeed(Graphics g, IReadOnlyList<ActivityEvent>? items)
    {
        if (items == null || items.Count == 0) return;

        int startY = 442, rowH = 24, visibleRows = 5;
        var bounds = new Rectangle(Margin, startY, ContentWidth, rowH * visibleRows);

        int maxOffset = Math.Max(0, items.Count - visibleRows);
        _activityScrollOffset = Math.Clamp(_activityScrollOffset, 0, maxOffset);

        ActivityFeedRenderer.Draw(g, bounds, items, _activityScrollOffset, -1);
    }

    // ── Recommendations (Y=590..698, 3 cards at 36px) ───────────────

    void DrawRecommendations(Graphics g, IReadOnlyList<Recommendation>? recs)
    {
        if (recs == null || recs.Count == 0) return;

        int startY = 590, cardH = 36, cardGap = 4;
        var bounds = new Rectangle(Margin, startY, ContentWidth, cardH * 3 + cardGap * 2);

        // Determine hovered card/button index
        int hoveredIndex = -1;
        int hoveredButton = -1;
        for (int i = 0; i < recs.Count && i < 3; i++)
        {
            if (_hoveredId == $"rec-{i}") { hoveredIndex = i; break; }
        }

        RecommendationRenderer.Draw(g, bounds, recs, hoveredIndex, hoveredButton);

        // Register hit regions for each card
        for (int i = 0; i < recs.Count && i < 3; i++)
        {
            int y = startY + i * (cardH + cardGap);
            var rect = new Rectangle(Margin, y, ContentWidth, cardH);
            _hitRegions.Add(new HitRegion(rect, $"rec-{i}", null));
        }
    }

    // ── Quick action buttons (Y=706..730) ───────────────────────────

    void DrawQuickActions(Graphics g)
    {
        int y = 706, btnW = 140, btnH = 28, gap = 8;
        int totalW = btnW * 4 + gap * 3;
        int startX = (PanelWidth - totalW) / 2;

        var buttons = new (string Label, string Id, Action? Handler)[]
        {
            ("Scan Now", "btn-scan", () => ScanRequested?.Invoke()),
            ("Activity", "btn-activity", () => ActivityRequested?.Invoke()),
            ("Network", "btn-network", () => NetworkRequested?.Invoke()),
            ("Settings", "btn-settings", () => SettingsRequested?.Invoke()),
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            var btn = buttons[i];
            int x = startX + i * (btnW + gap);
            var rect = new Rectangle(x, y, btnW, btnH);
            bool isHovered = _hoveredId == btn.Id;

            var bgColor = isHovered ? Theme.BgCardHover : Theme.BgCard;
            using var bgBrush = new SolidBrush(bgColor);
            using var path = RoundedRect(rect, 4);
            g.FillPath(bgBrush, path);

            using var textBrush = new SolidBrush(Theme.TextPrimary);
            var textSize = g.MeasureString(btn.Label, _btnFont);
            float tx = rect.X + (rect.Width - textSize.Width) / 2;
            float ty = rect.Y + (rect.Height - textSize.Height) / 2;
            g.DrawString(btn.Label, _btnFont, textBrush, tx, ty);

            _hitRegions.Add(new HitRegion(rect, btn.Id, btn.Handler));
        }
    }

    // ── Mouse interaction ───────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? newHovered = null;

        foreach (var region in _hitRegions)
        {
            if (region.Bounds.Contains(e.Location))
            {
                newHovered = region.Id;
                break;
            }
        }

        if (newHovered != _hoveredId)
        {
            _hoveredId = newHovered;
            Cursor = _hoveredId != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        foreach (var region in _hitRegions)
        {
            if (region.Bounds.Contains(e.Location))
            {
                region.OnClick?.Invoke();
                break;
            }
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // Only scroll the activity feed area (Y=442..562)
        if (e.Y >= 442 && e.Y <= 562 && _state.RecentActivity != null)
        {
            int delta = e.Delta > 0 ? -1 : 1;
            int maxOffset = Math.Max(0, _state.RecentActivity.Count - 5);
            _activityScrollOffset = Math.Clamp(_activityScrollOffset + delta, 0, maxOffset);
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredId != null)
        {
            _hoveredId = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);

    static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── Cleanup ──────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.StateChanged -= OnEngineStateChanged;
            _animTimer.Stop();
            _animTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Internal types ──────────────────────────────────────────────

    readonly record struct HitRegion(Rectangle Bounds, string Id, Action? OnClick);
}
