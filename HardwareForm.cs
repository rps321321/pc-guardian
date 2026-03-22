namespace PCGuardian;

internal sealed class HardwareForm : Form
{
    readonly HardwareMonitor _hwMonitor;
    readonly Database? _db;

    readonly TabControl _tabs;
    readonly Label _lblTopBar;
    readonly Label _lblStatus;

    // Overview tab
    readonly Label[] _titleLabels = new Label[9];
    readonly Label[] _valueLabels = new Label[9];

    // Storage tab
    readonly DataGridView _gridStorage;

    // Trends tab
    readonly Panel _chartPanel;
    List<HardwareMetricRow> _history = [];
    DateTime _lastHistoryLoad = DateTime.MinValue;

    // All Sensors tab
    readonly DataGridView _gridSensors;

    // Refresh timer for trends
    readonly System.Windows.Forms.Timer _trendsTimer;

    // -----------------------------------------------------------------------
    //  Constructor
    // -----------------------------------------------------------------------

    public HardwareForm(HardwareMonitor hwMonitor, Database? db)
    {
        _hwMonitor = hwMonitor;
        _db = db;

        Text = "Hardware Monitor \u2014 PC Guardian";
        Size = new(850, 600);
        MinimumSize = new(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Top status bar ---
        _lblTopBar = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextPrimary,
            Font = Theme.CardTitle,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(14, 0, 0, 0),
            Text = "  Collecting hardware data\u2026",
        };

        // --- Bottom status bar ---
        _lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextMuted,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(14, 0, 0, 0),
        };

        // --- Tabs ---
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.CardTitle,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            Padding = new(12, 4),
        };
        _tabs.DrawItem += DrawTab;
        _tabs.SelectedIndexChanged += OnTabChanged;

        // Tab 1: Overview
        var tabOverview = new TabPage("  \uD83D\uDDA5 Overview  ") { BackColor = Theme.BgPrimary };
        tabOverview.Controls.Add(BuildOverviewPanel());
        _tabs.TabPages.Add(tabOverview);

        // Tab 2: Storage
        _gridStorage = CreateGrid();
        _gridStorage.Columns.AddRange(
        [
            Col("Drive", 120),
            Col("Temperature", 80),
            Col("Remaining Life", 80),
            Col("Total Written", 90),
            Col("Status", 80),
        ]);
        _gridStorage.CellFormatting += OnCellFormattingStorage;

        var tabStorage = new TabPage("  \uD83D\uDCBE Storage  ") { BackColor = Theme.BgPrimary };
        tabStorage.Controls.Add(_gridStorage);
        _tabs.TabPages.Add(tabStorage);

        // Tab 3: Trends
        _chartPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.BgPrimary,
        };
        _chartPanel.Paint += OnPaintCharts;

        var lblChartHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Theme.BgPrimary,
            ForeColor = Theme.TextMuted,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Click a point to see what was running",
        };

        var tabTrends = new TabPage("  \uD83D\uDCC8 Trends  ") { BackColor = Theme.BgPrimary };
        tabTrends.Controls.Add(_chartPanel);
        tabTrends.Controls.Add(lblChartHint);
        _tabs.TabPages.Add(tabTrends);

        // Tab 4: All Sensors
        _gridSensors = CreateGrid();
        _gridSensors.Columns.AddRange(
        [
            Col("Hardware", 120),
            Col("Sensor", 130),
            Col("Type", 80),
            Col("Value", 70),
            Col("Min", 60),
            Col("Max", 60),
        ]);

        var tabSensors = new TabPage("  \uD83D\uDD0D All Sensors  ") { BackColor = Theme.BgPrimary };
        tabSensors.Controls.Add(_gridSensors);
        _tabs.TabPages.Add(tabSensors);

        // Layout: status bottom, top bar top, tabs fill
        Controls.Add(_tabs);
        Controls.Add(_lblTopBar);
        Controls.Add(_lblStatus);

        // --- Trends refresh timer ---
        _trendsTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _trendsTimer.Tick += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated && _tabs.SelectedIndex == 2)
            {
                LoadHistory();
                _chartPanel.Invalidate();
            }
        };
        _trendsTimer.Start();

        // --- Subscribe to hardware updates ---
        _hwMonitor.Updated += OnHardwareUpdated;

        UpdateStatusBar();
    }

    // -----------------------------------------------------------------------
    //  Overview panel builder
    // -----------------------------------------------------------------------

    TableLayoutPanel BuildOverviewPanel()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = Theme.BgPrimary,
            Padding = new(20, 16, 20, 16),
        };

        for (int c = 0; c < 3; c++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int r = 0; r < 3; r++)
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

        string[] titles =
        [
            "CPU Temperature", "CPU Load", "GPU Temperature",
            "GPU Load", "Fan Speed", "CPU Power",
            "Battery Charge", "Battery Health", "",
        ];

        for (int i = 0; i < 9; i++)
        {
            int col = i % 3;
            int row = i / 3;

            var cell = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgCard, Margin = new(4) };

            var titleLbl = new Label
            {
                Text = titles[i],
                Font = Theme.Small,
                ForeColor = Theme.TextMuted,
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.BottomLeft,
                Padding = new(10, 0, 0, 0),
            };

            var valueLbl = new Label
            {
                Text = "\u2014",
                Font = new Font("Segoe UI Semibold", 20f),
                ForeColor = Theme.TextPrimary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new(10, 0, 0, 0),
            };

            cell.Controls.Add(valueLbl);
            cell.Controls.Add(titleLbl);

            _titleLabels[i] = titleLbl;
            _valueLabels[i] = valueLbl;

            table.Controls.Add(cell, col, row);
        }

        return table;
    }

    // -----------------------------------------------------------------------
    //  Hardware update handler
    // -----------------------------------------------------------------------

    void OnHardwareUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(RefreshAll); } catch { }
    }

    void RefreshAll()
    {
        try
        {
            var snap = _hwMonitor.GetSnapshot();
            if (snap is null) return;

            RefreshTopBar(snap);
            RefreshOverview(snap);
            RefreshStorage(snap);
            RefreshSensors(snap);
            UpdateStatusBar();
        }
        catch { }
    }

    // -----------------------------------------------------------------------
    //  Top bar
    // -----------------------------------------------------------------------

    void RefreshTopBar(HardwareSnapshot snap)
    {
        double cpuTemp = snap.CpuTemp ?? 0;
        double gpuTemp = snap.GpuTemp ?? 0;
        double fanRpm = snap.CpuFanRpm ?? 0;
        double cpuLoad = snap.CpuLoad ?? 0;

        _lblTopBar.Text = $"  CPU: {cpuTemp:0}\u00B0C  \u00B7  " +
                          $"GPU: {(snap.HasGpu ? $"{gpuTemp:0}\u00B0C" : "N/A")}  \u00B7  " +
                          $"Fan: {fanRpm:0} RPM  \u00B7  " +
                          $"Load: {cpuLoad:0}%";

        bool hasDanger = cpuTemp > 85 || gpuTemp > 85;
        bool hasWarning = cpuTemp > 70 || gpuTemp > 70;

        _lblTopBar.ForeColor = hasDanger ? Theme.Danger
            : hasWarning ? Theme.Warning
            : Theme.Safe;
    }

    // -----------------------------------------------------------------------
    //  Overview tab
    // -----------------------------------------------------------------------

    void RefreshOverview(HardwareSnapshot snap)
    {
        // 0: CPU Temp (0°C without admin = unreliable)
        SetOverviewValue(0, snap.CpuTemp, "\u00B0C", v => v > 85 ? Theme.Danger : v > 70 ? Theme.Warning : Theme.Safe, zeroIsUnreliable: true);

        // 1: CPU Load
        SetOverviewValue(1, snap.CpuLoad, "%", _ => Theme.TextPrimary);

        // 2: GPU Temp
        if (snap.HasGpu)
            SetOverviewValue(2, snap.GpuTemp, "\u00B0C", v => v > 85 ? Theme.Danger : v > 70 ? Theme.Warning : Theme.Safe);
        else
        {
            _valueLabels[2].Text = "No dedicated GPU";
            _valueLabels[2].Font = Theme.CardBody;
            _valueLabels[2].ForeColor = Theme.TextMuted;
        }

        // 3: GPU Load
        if (snap.HasGpu)
            SetOverviewValue(3, snap.GpuLoad, "%", _ => Theme.TextPrimary);
        else
        {
            _valueLabels[3].Text = "\u2014";
            _valueLabels[3].ForeColor = Theme.TextMuted;
        }

        // 4: Fan Speed (0 RPM without admin = unreliable, not a stopped fan)
        SetOverviewValue(4, snap.CpuFanRpm, " RPM", _ => Theme.TextPrimary, zeroIsUnreliable: true);

        // 5: CPU Power
        SetOverviewValue(5, snap.CpuPower, " W", _ => Theme.TextPrimary);

        // 6: Battery Charge
        if (snap.Battery != null)
            SetOverviewValue(6, snap.Battery?.ChargeLevel, "%", _ => Theme.TextPrimary);
        else
        {
            _valueLabels[6].Text = "No battery";
            _valueLabels[6].Font = Theme.CardBody;
            _valueLabels[6].ForeColor = Theme.TextMuted;
        }

        // 7: Battery Health
        if (snap.Battery != null)
            SetOverviewValue(7, (100f - (snap.Battery?.DegradationPercent ?? 0f)), "%", v => v < 50 ? Theme.Danger : v < 80 ? Theme.Warning : Theme.Safe);
        else
        {
            _valueLabels[7].Text = "\u2014";
            _valueLabels[7].ForeColor = Theme.TextMuted;
        }

        // 8: reserved (empty cell)
        _titleLabels[8].Text = "";
        _valueLabels[8].Text = "";
    }

    void SetOverviewValue(int index, double? value, string suffix, Func<double, Color> colorFn, bool zeroIsUnreliable = false)
    {
        if (value is null || (zeroIsUnreliable && value == 0 && !AdminHelper.IsAdmin()))
        {
            _valueLabels[index].Text = !AdminHelper.IsAdmin() ? "Run as admin" : "\u2014";
            _valueLabels[index].Font = Theme.CardBody;
            _valueLabels[index].ForeColor = Theme.TextMuted;
            return;
        }

        _valueLabels[index].Text = $"{value:0}{suffix}";
        _valueLabels[index].Font = new Font("Segoe UI Semibold", 20f);
        _valueLabels[index].ForeColor = colorFn(value.Value);
    }

    // -----------------------------------------------------------------------
    //  Storage tab
    // -----------------------------------------------------------------------

    void RefreshStorage(HardwareSnapshot snap)
    {
        if (_tabs.SelectedIndex != 1) return;

        _gridStorage.SuspendLayout();
        _gridStorage.Rows.Clear();

        foreach (var d in snap.Drives)
        {
            // RemainingLife can be null (no admin), 0 (unreliable read without admin), or a real value
            bool unreliable = !d.RemainingLife.HasValue || (d.RemainingLife == 0 && !AdminHelper.IsAdmin());
            string status = unreliable ? "Unknown" : d.RemainingLife switch
            {
                <= 5 => "Critical",
                <= 20 => "Wear",
                _ => "Healthy",
            };

            _gridStorage.Rows.Add(
                d.Name,
                d.Temperature.HasValue ? $"{d.Temperature:0}\u00B0C" : "\u2014",
                unreliable ? "Run as admin" : $"{d.RemainingLife:0}%",
                d.TotalWritten.HasValue ? $"{d.TotalWritten / 1024f:0.1} TB" : "\u2014",
                status);
        }

        _gridStorage.ResumeLayout();
    }

    void OnCellFormattingStorage(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var colName = _gridStorage.Columns[e.ColumnIndex].Name;
        if (colName != "Status") return;

        var val = e.Value?.ToString() ?? "";
        e.CellStyle!.ForeColor = val switch
        {
            "Critical" => Theme.Danger,
            "Wear" => Theme.Warning,
            "Healthy" => Theme.Safe,
            "Unknown" => Theme.TextMuted,
            _ => Theme.TextPrimary,
        };
    }

    // -----------------------------------------------------------------------
    //  Trends tab – GDI+ charts
    // -----------------------------------------------------------------------

    void OnTabChanged(object? sender, EventArgs e)
    {
        if (_tabs.SelectedIndex == 2)
        {
            LoadHistory();
            _chartPanel.Invalidate();
        }
    }

    void LoadHistory()
    {
        if (_db is null) return;
        if ((DateTime.Now - _lastHistoryLoad).TotalSeconds < 30) return;

        try
        {
            _history = _db.GetHardwareHistory(24);
            _lastHistoryLoad = DateTime.Now;
        }
        catch { _history = []; }
    }

    void OnPaintCharts(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = _chartPanel.ClientRectangle;

        int chartHeight = (bounds.Height - 20) / 2;
        var tempRect = new Rectangle(bounds.X + 50, bounds.Y + 10, bounds.Width - 80, chartHeight - 20);
        var loadRect = new Rectangle(bounds.X + 50, bounds.Y + chartHeight + 10, bounds.Width - 80, chartHeight - 20);

        DrawChart(g, tempRect, "Temperature (\u00B0C)",
            _history.Select(h => h.CpuTemp).ToList(),
            _history.Select(h => h.GpuTemp).ToList(),
            Theme.Danger, Theme.Accent,
            "CPU", "GPU");

        DrawChart(g, loadRect, "Load (%)",
            _history.Select(h => h.CpuLoad).ToList(),
            _history.Select(h => h.GpuLoad).ToList(),
            Theme.Warning, Theme.Safe,
            "CPU", "GPU");
    }

    void DrawChart(Graphics g, Rectangle rect, string title,
        List<double?> series1, List<double?> series2,
        Color color1, Color color2,
        string legend1, string legend2)
    {
        // Background
        using (var bg = new SolidBrush(Theme.BgCard))
            g.FillRectangle(bg, rect);

        // Title
        using (var titleBrush = new SolidBrush(Theme.TextSecondary))
            g.DrawString(title, Theme.CardTitle, titleBrush, rect.X + 4, rect.Y - 14);

        if (series1.Count == 0) return;

        // Grid lines
        using var gridPen = new Pen(Theme.Border, 1);
        for (int y = 0; y <= 100; y += 25)
        {
            float py = rect.Bottom - (y / 100f * rect.Height);
            g.DrawLine(gridPen, rect.Left, py, rect.Right, py);

            using var yBrush = new SolidBrush(Theme.TextMuted);
            g.DrawString(y.ToString(), Theme.Small, yBrush, rect.Left - 28, py - 6);
        }

        // X-axis time labels
        if (series1.Count > 1)
        {
            int step = Math.Max(1, series1.Count / 12); // ~every 2 hours for 24h data
            using var xBrush = new SolidBrush(Theme.TextMuted);
            for (int i = 0; i < _history.Count; i += step)
            {
                float px = rect.Left + (float)i / (series1.Count - 1) * rect.Width;
                string timeLabel = DateTime.TryParse(_history[i].Timestamp, out var dt)
                    ? dt.ToString("h:mm tt") : "";
                g.DrawString(timeLabel, Theme.Small, xBrush, px - 16, rect.Bottom + 2);
            }
        }

        // Draw series
        DrawSeries(g, rect, series1, color1);
        DrawSeries(g, rect, series2, color2);

        // Legend
        float lx = rect.Right - 140;
        float ly = rect.Y + 4;

        using (var pen1 = new Pen(color1, 2))
        {
            g.DrawLine(pen1, lx, ly + 6, lx + 16, ly + 6);
            using var lb = new SolidBrush(Theme.TextSecondary);
            g.DrawString(legend1, Theme.Small, lb, lx + 20, ly);
        }

        using (var pen2 = new Pen(color2, 2))
        {
            g.DrawLine(pen2, lx + 70, ly + 6, lx + 86, ly + 6);
            using var lb = new SolidBrush(Theme.TextSecondary);
            g.DrawString(legend2, Theme.Small, lb, lx + 90, ly);
        }
    }

    static void DrawSeries(Graphics g, Rectangle rect, List<double?> data, Color color)
    {
        if (data.Count < 2) return;

        var points = new List<PointF>();
        for (int i = 0; i < data.Count; i++)
        {
            if (data[i] is not double val) continue;
            float x = rect.Left + (float)i / (data.Count - 1) * rect.Width;
            float y = rect.Bottom - (float)(Math.Clamp(val, 0, 100) / 100.0 * rect.Height);
            points.Add(new PointF(x, y));
        }

        if (points.Count < 2) return;

        using var pen = new Pen(color, 2);
        g.DrawLines(pen, points.ToArray());
    }

    // -----------------------------------------------------------------------
    //  All Sensors tab
    // -----------------------------------------------------------------------

    void RefreshSensors(HardwareSnapshot snap)
    {
        if (_tabs.SelectedIndex != 3) return;

        _gridSensors.SuspendLayout();
        _gridSensors.Rows.Clear();

        foreach (var s in snap.AllSensors)
        {
            _gridSensors.Rows.Add(
                s.HardwareName,
                s.SensorName,
                s.SensorType,
                s.Value,
                s.Min,
                s.Max);
        }

        _gridSensors.ResumeLayout();
    }

    // -----------------------------------------------------------------------
    //  Status bar
    // -----------------------------------------------------------------------

    void UpdateStatusBar()
    {
        string prefix = AdminHelper.IsAdmin() ? "" : "\u26A0 Limited access \u2014 ";
        _lblStatus.Text = $"  {prefix}Live \u00B7 Updated: {DateTime.Now:h:mm:ss tt} \u00B7 Refreshing every 5s";
    }

    // -----------------------------------------------------------------------
    //  Grid factory
    // -----------------------------------------------------------------------

    static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Theme.BgPrimary,
            GridColor = Theme.Border,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            Dock = DockStyle.Fill,
        };
        grid.DefaultCellStyle.BackColor = Theme.BgCard;
        grid.DefaultCellStyle.ForeColor = Theme.TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        grid.DefaultCellStyle.Font = Theme.CardBody;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(15, 15, 18);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.CardTitle;
        grid.ColumnHeadersHeight = 36;
        grid.RowTemplate.Height = 28;
        grid.EnableHeadersVisualStyles = false;
        return grid;
    }

    static DataGridViewTextBoxColumn Col(string header, int weight) => new()
    {
        HeaderText = header,
        Name = header,
        MinimumWidth = 40,
        FillWeight = weight,
    };

    // -----------------------------------------------------------------------
    //  Tab drawing (dark theme)
    // -----------------------------------------------------------------------

    void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var tab = _tabs.TabPages[e.Index];
        bool selected = _tabs.SelectedIndex == e.Index;

        using var bgBrush = new SolidBrush(selected ? Theme.BgCard : Theme.BgPrimary);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        using var fgBrush = new SolidBrush(selected ? Theme.Accent : Theme.TextSecondary);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(tab.Text, Theme.CardTitle, fgBrush, e.Bounds, sf);
    }

    // -----------------------------------------------------------------------
    //  Cleanup
    // -----------------------------------------------------------------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _trendsTimer.Stop();
        _hwMonitor.Updated -= OnHardwareUpdated;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hwMonitor.Updated -= OnHardwareUpdated;
            _trendsTimer?.Stop();
            _trendsTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
