namespace PCGuardian;

internal sealed class CpuGpuForm : Form
{
    readonly SystemMonitor _monitor;
    readonly Database? _db;
    readonly TabControl _tabs;
    readonly Label _lblStatus;

    // Dashboard
    readonly Label[] _gaugeValues = new Label[9];
    readonly Label[] _gaugeTitles = new Label[9];

    // Storage
    readonly DataGridView _gridStorage;

    // Battery
    readonly Panel _batteryPanel;

    // Security
    readonly DataGridView _gridSecurity;

    // Trends
    readonly Panel _trendsPanel;
    List<HardwareMetricRow>? _trendData;

    // Raw Data
    readonly DataGridView _gridRaw;

    // -----------------------------------------------------------------------
    //  Constructor
    // -----------------------------------------------------------------------

    public CpuGpuForm(SystemMonitor monitor, Database? db)
    {
        _monitor = monitor;
        _db = db;

        Text = "System Monitor \u2014 PC Guardian";
        Size = new(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Status bar (top) ---
        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextPrimary,
            Font = Theme.CardTitle,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(14, 0, 0, 0),
            Text = "  Collecting data\u2026",
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

        // ── Tab 1: Dashboard ──
        var tabDash = new TabPage("  Dashboard  ") { BackColor = Theme.BgPrimary };
        tabDash.Controls.Add(BuildDashboard());
        _tabs.TabPages.Add(tabDash);

        // ── Tab 2: Storage ──
        _gridStorage = CreateGrid();
        _gridStorage.Columns.AddRange(
        [
            Col("Drive", 70), Col("Type", 70), Col("Size", 70),
            Col("Free Space", 80), Col("Used%", 60), Col("Health", 70),
            Col("Failure Predicted", 90),
        ]);
        _gridStorage.CellFormatting += OnCellFormattingStorage;

        var tabStorage = new TabPage("  Storage  ") { BackColor = Theme.BgPrimary };
        tabStorage.Controls.Add(_gridStorage);
        _tabs.TabPages.Add(tabStorage);

        // ── Tab 3: Battery ──
        _batteryPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgPrimary };
        var tabBattery = new TabPage("  Battery  ") { BackColor = Theme.BgPrimary };
        tabBattery.Controls.Add(_batteryPanel);
        _tabs.TabPages.Add(tabBattery);

        // ── Tab 4: Security ──
        _gridSecurity = CreateGrid();
        _gridSecurity.Columns.AddRange(
        [
            Col("Check", 130), Col("Status", 80), Col("Detail", 200),
        ]);
        _gridSecurity.CellFormatting += OnCellFormattingSecurity;

        var tabSecurity = new TabPage("  Security  ") { BackColor = Theme.BgPrimary };
        tabSecurity.Controls.Add(_gridSecurity);
        _tabs.TabPages.Add(tabSecurity);

        // ── Tab 5: Trends ──
        _trendsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.BgPrimary,
        };
        _trendsPanel.Paint += PaintTrends;

        var tabTrends = new TabPage("  Trends  ") { BackColor = Theme.BgPrimary };
        tabTrends.Controls.Add(_trendsPanel);
        _tabs.TabPages.Add(tabTrends);

        // ── Tab 6: Raw Data ──
        _gridRaw = CreateGrid();
        _gridRaw.Columns.AddRange(
        [
            Col("Source", 80), Col("Name", 120), Col("Value", 120),
        ]);

        var tabRaw = new TabPage("  Raw Data  ") { BackColor = Theme.BgPrimary };
        tabRaw.Controls.Add(_gridRaw);
        _tabs.TabPages.Add(tabRaw);

        // Layout: status top, tabs fill
        Controls.Add(_tabs);
        Controls.Add(_lblStatus);

        // Subscribe to live updates
        _monitor.Updated += OnMonitorUpdated;

        // Initial data load
        LoadTrendData();
        RefreshAll();
    }

    // -----------------------------------------------------------------------
    //  Dashboard builder
    // -----------------------------------------------------------------------

    static readonly string[] GaugeTitles =
    [
        "CPU LOAD", "GPU LOAD", "RAM USED",
        "DISK READ", "DISK WRITE", "CPU TEMP",
        "BATTERY", "UPTIME", "SYSTEM INFO",
    ];

    TableLayoutPanel BuildDashboard()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Padding = new(12),
            BackColor = Theme.BgPrimary,
        };

        for (int c = 0; c < 3; c++)
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int r = 0; r < 3; r++)
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

        for (int i = 0; i < 9; i++)
        {
            var cell = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.BgCard,
                Margin = new(6),
                Padding = new(12),
            };

            _gaugeTitles[i] = new Label
            {
                Text = GaugeTitles[i],
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Theme.TextSecondary,
                Font = Theme.Small,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            _gaugeValues[i] = new Label
            {
                Text = "\u2014",
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextPrimary,
                Font = new Font("Segoe UI", 20f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };

            cell.Controls.Add(_gaugeValues[i]);
            cell.Controls.Add(_gaugeTitles[i]);

            table.Controls.Add(cell, i % 3, i / 3);
        }

        return table;
    }

    // -----------------------------------------------------------------------
    //  Live data refresh
    // -----------------------------------------------------------------------

    void OnMonitorUpdated()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(RefreshAll); } catch { }
    }

    void RefreshAll()
    {
        try
        {
            var perf = _monitor.GetLatestPerf();
            if (perf is null) return;
            var wmiStatic = _monitor.GetStaticInfo();
            var wmiDynamic = _monitor.GetDynamic();

            // ── Status bar ──
            float diskMBs = (perf.DiskReadBps + perf.DiskWriteBps) / (1024f * 1024f);
            _lblStatus.Text = $"  CPU: {perf.CpuPercent:F0}%  \u00B7  " +
                              $"GPU: {perf.GpuPercent:F0}%  \u00B7  " +
                              $"RAM: {perf.RamUsedPercent}%  \u00B7  " +
                              $"Disk: {diskMBs:F0} MB/s";

            // ── Dashboard ──
            RefreshDashboard(perf, wmiStatic, wmiDynamic);

            // ── Tab-specific refreshes ──
            int selectedTab = _tabs.SelectedIndex;

            if (selectedTab == 1) // Storage
                RefreshStorage(wmiDynamic?.Drives);

            if (selectedTab == 2) // Battery
                RefreshBattery(wmiDynamic?.Battery);

            if (selectedTab == 3) // Security
                RefreshSecurity();

            if (selectedTab == 4) // Trends
                _trendsPanel.Invalidate();

            if (selectedTab == 5) // Raw Data
                RefreshRawData(perf, wmiStatic, wmiDynamic);
        }
        catch { }
    }

    // -----------------------------------------------------------------------
    //  Dashboard refresh
    // -----------------------------------------------------------------------

    void RefreshDashboard(PerfReading perf, SystemStaticInfo wmiStatic, DynamicHwInfo? dyn)
    {
        // CPU Load
        _gaugeValues[0].Text = $"{perf.CpuPercent:F0}%";
        _gaugeValues[0].ForeColor = perf.CpuPercent < 70 ? Theme.Safe
            : perf.CpuPercent < 90 ? Theme.Warning : Theme.Danger;

        // GPU Load
        _gaugeValues[1].Text = $"{perf.GpuPercent:F0}%";
        _gaugeValues[1].ForeColor = perf.GpuPercent < 70 ? Theme.Safe
            : perf.GpuPercent < 90 ? Theme.Warning : Theme.Danger;

        // RAM
        ulong usedMB = perf.RamTotalMB - perf.RamAvailMB;
        float totalGB = perf.RamTotalMB / 1024f;
        float usedGB = usedMB / 1024f;
        _gaugeValues[2].Text = $"{perf.RamUsedPercent}%\n{usedGB:F1} of {totalGB:F1} GB";
        _gaugeValues[2].ForeColor = perf.RamUsedPercent < 70 ? Theme.Safe
            : perf.RamUsedPercent < 90 ? Theme.Warning : Theme.Danger;

        // Disk Read
        float readMBs = perf.DiskReadBps / (1024f * 1024f);
        _gaugeValues[3].Text = $"{readMBs:F1} MB/s";
        _gaugeValues[3].ForeColor = Theme.TextPrimary;

        // Disk Write
        float writeMBs = perf.DiskWriteBps / (1024f * 1024f);
        _gaugeValues[4].Text = $"{writeMBs:F1} MB/s";
        _gaugeValues[4].ForeColor = Theme.TextPrimary;

        // CPU Temp
        if (dyn?.ThermalZoneTempC is float temp)
        {
            _gaugeValues[5].Text = $"{temp:F0} \u00B0C";
            _gaugeValues[5].ForeColor = temp < 70 ? Theme.Safe
                : temp < 90 ? Theme.Warning : Theme.Danger;
        }
        else
        {
            _gaugeValues[5].Text = "N/A";
            _gaugeValues[5].ForeColor = Theme.TextSecondary;
        }

        // Battery
        if (dyn?.Battery is BatteryHealth bat)
        {
            float health = 100f - bat.DegradationPercent;
            _gaugeValues[6].Text = $"{bat.ChargePercent:F0}%\nHealth: {health:F0}%";
            _gaugeValues[6].ForeColor = bat.ChargePercent < 20 ? Theme.Danger
                : bat.ChargePercent < 40 ? Theme.Warning : Theme.Safe;
        }
        else
        {
            _gaugeValues[6].Text = "No battery";
            _gaugeValues[6].ForeColor = Theme.TextSecondary;
        }

        // Uptime
        if (wmiStatic.BootTime != DateTime.MinValue)
        {
            var uptime = DateTime.Now - wmiStatic.BootTime;
            _gaugeValues[7].Text = uptime.TotalDays >= 1
                ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
                : $"{uptime.Hours}h {uptime.Minutes}m";
            _gaugeValues[7].ForeColor = uptime.TotalDays > 14 ? Theme.Warning : Theme.TextPrimary;
        }
        else
        {
            _gaugeValues[7].Text = "\u2014";
            _gaugeValues[7].ForeColor = Theme.TextSecondary;
        }

        // System Info
        _gaugeValues[8].Font = Theme.CardBody;
        _gaugeValues[8].Text = $"{Truncate(wmiStatic.CpuName, 40)}\n" +
                               $"{Truncate(wmiStatic.OsCaption, 40)}\n" +
                               $"{Truncate(wmiStatic.GpuName, 40)}";
        _gaugeValues[8].ForeColor = Theme.TextSecondary;
    }

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    // -----------------------------------------------------------------------
    //  Storage tab
    // -----------------------------------------------------------------------

    void RefreshStorage(IReadOnlyList<DriveHealth>? drives)
    {
        if (drives is null) return;

        _gridStorage.SuspendLayout();
        _gridStorage.Rows.Clear();

        foreach (var d in drives)
        {
            string sizeStr = FormatBytes(d.SizeBytes);
            string freeStr = FormatBytes(d.FreeBytes);
            float usedPct = d.SizeBytes > 0
                ? (float)(d.SizeBytes - d.FreeBytes) / d.SizeBytes * 100f
                : 0f;
            string driveType = d.BusType == "NVMe" ? "NVMe"
                : d.MediaType == "SSD" ? "SSD"
                : d.MediaType == "HDD" ? "HDD"
                : d.MediaType;

            _gridStorage.Rows.Add(
                d.Name, driveType, sizeStr, freeStr,
                $"{usedPct:F0}%", d.HealthStatus,
                d.PredictFailure switch
                {
                    true => "Yes",
                    false => "No",
                    null => "N/A",
                });
        }

        _gridStorage.ResumeLayout();
    }

    void OnCellFormattingStorage(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var colName = _gridStorage.Columns[e.ColumnIndex].Name;

        if (colName == "Health")
        {
            var val = e.Value?.ToString() ?? "";
            e.CellStyle!.ForeColor = val switch
            {
                "Healthy" => Theme.Safe,
                "Warning" => Theme.Warning,
                _ => Theme.Danger,
            };
        }
        else if (colName == "Failure Predicted")
        {
            var val = e.Value?.ToString() ?? "";
            if (val == "Yes") e.CellStyle!.ForeColor = Theme.Danger;
        }
    }

    // -----------------------------------------------------------------------
    //  Battery tab
    // -----------------------------------------------------------------------

    void RefreshBattery(BatteryHealth? battery)
    {
        _batteryPanel.Controls.Clear();

        if (battery is null)
        {
            _batteryPanel.Controls.Add(new Label
            {
                Text = "No battery detected",
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextSecondary,
                Font = Theme.CardTitle,
                TextAlign = ContentAlignment.MiddleCenter,
            });
            return;
        }

        var bat = battery;
        float health = 100f - bat.DegradationPercent;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new(24),
            BackColor = Theme.BgPrimary,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        string[] labels =
        [
            "Charge", $"{bat.ChargePercent:F1}%",
            "Design Capacity", $"{bat.DesignCapacityMWh} mWh",
            "Full Charge Capacity", $"{bat.FullChargeCapacityMWh} mWh",
            "Degradation", $"{bat.DegradationPercent:F1}%",
            "Cycle Count", bat.CycleCount.ToString(),
            "Charging Status", bat.IsCharging ? "Charging" : "Discharging",
            "AC Connected", bat.IsAcConnected ? "Yes" : "No",
        ];

        for (int i = 0; i < labels.Length; i += 2)
        {
            int row = i / 2;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            layout.Controls.Add(new Label
            {
                Text = labels[i],
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextSecondary,
                Font = Theme.CardTitle,
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, row);

            var valLabel = new Label
            {
                Text = labels[i + 1],
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            // Color coding
            if (labels[i] == "Charge")
                valLabel.ForeColor = bat.ChargePercent < 20 ? Theme.Danger
                    : bat.ChargePercent < 40 ? Theme.Warning : Theme.Safe;
            else if (labels[i] == "Degradation")
                valLabel.ForeColor = bat.DegradationPercent > 30 ? Theme.Danger
                    : bat.DegradationPercent > 15 ? Theme.Warning : Theme.Safe;
            else
                valLabel.ForeColor = Theme.TextPrimary;

            layout.Controls.Add(valLabel, 1, row);
        }

        _batteryPanel.Controls.Add(layout);
    }

    // -----------------------------------------------------------------------
    //  Security tab
    // -----------------------------------------------------------------------

    void RefreshSecurity()
    {
        SecurityPosture sec;
        try { sec = _monitor.GetSecurityPosture(); }
        catch { return; }

        _gridSecurity.SuspendLayout();
        _gridSecurity.Rows.Clear();

        AddSecRow("BitLocker", sec.BitLockerEnabled switch
        {
            true => ("Enabled", "Safe"),
            false => ("Disabled", "Danger"),
            null => ("Unknown (admin required)", "Warning"),
        });

        AddSecRow("Secure Boot", sec.SecureBootEnabled switch
        {
            true => ("Enabled", "Safe"),
            false => ("Disabled", "Danger"),
            null => ("Unknown", "Warning"),
        });

        AddSecRow("TPM", sec.TpmPresent switch
        {
            true => ($"Present (v{sec.TpmVersion ?? "?"})", "Safe"),
            false => ("Not found", "Danger"),
            null => ("Unknown (admin required)", "Warning"),
        });

        AddSecRow("UAC", sec.UacEnabled
            ? ($"Enabled (level {sec.UacConsentLevel})", sec.UacConsentLevel >= 2 ? "Safe" : "Warning")
            : ("Disabled", "Danger"));

        AddSecRow("Auto-Login", !sec.AutoLoginEnabled
            ? ("Disabled", "Safe")
            : sec.AutoLoginPasswordStored
                ? ("Enabled + password stored", "Danger")
                : ("Enabled", "Warning"));

        AddSecRow("Password Policy", sec.PasswordMinLength >= 8
            ? ($"Min length: {sec.PasswordMinLength}", "Safe")
            : sec.PasswordMinLength > 0
                ? ($"Min length: {sec.PasswordMinLength} (weak)", "Warning")
                : ("No minimum length", "Danger"));

        AddSecRow("Guest Account", sec.GuestAccountEnabled switch
        {
            false => ("Disabled", "Safe"),
            true => ("Enabled", "Danger"),
            null => ("Unknown", "Warning"),
        });

        AddSecRow("Screen Lock", sec.ScreenLockTimeoutSec is int timeout
            ? (sec.ScreenLockRequiresPassword
                ? ($"{timeout}s timeout, password required", "Safe")
                : ($"{timeout}s timeout, no password", "Warning"))
            : ("Not configured", "Danger"));

        AddSecRow("Pending Updates", !sec.RebootPending
            ? ("No reboot pending", "Safe")
            : ("Reboot pending", "Warning"));

        _gridSecurity.ResumeLayout();
    }

    void AddSecRow(string check, (string detail, string status) info)
    {
        _gridSecurity.Rows.Add(check, info.status, info.detail);
    }

    void OnCellFormattingSecurity(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var colName = _gridSecurity.Columns[e.ColumnIndex].Name;
        if (colName != "Status") return;

        var val = e.Value?.ToString() ?? "";
        e.CellStyle!.ForeColor = val switch
        {
            "Safe" => Theme.Safe,
            "Warning" => Theme.Warning,
            "Danger" => Theme.Danger,
            _ => Theme.TextSecondary,
        };
    }

    // -----------------------------------------------------------------------
    //  Trends tab (GDI+ line charts)
    // -----------------------------------------------------------------------

    void LoadTrendData()
    {
        try { _trendData = _db?.GetHardwareHistory(24); }
        catch { _trendData = null; }
    }

    void PaintTrends(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Theme.BgPrimary);

        var data = _trendData;
        if (data is null || data.Count < 2)
        {
            using var noData = new SolidBrush(Theme.TextSecondary);
            g.DrawString("Not enough trend data yet", Theme.CardTitle, noData,
                _trendsPanel.Width / 2f - 80, _trendsPanel.Height / 2f - 10);
            return;
        }

        int w = _trendsPanel.Width;
        int h = _trendsPanel.Height;
        int chartH = (h - 60) / 2; // two charts stacked
        int leftPad = 50, rightPad = 16, topPad = 24;

        // Chart 1: CPU% + GPU%
        DrawChart(g, data, leftPad, topPad, w - leftPad - rightPad, chartH,
            "CPU & GPU Load",
            [
                (r => r.CpuLoad, Theme.Accent, "CPU%"),
                (r => r.GpuLoad, Theme.Warning, "GPU%"),
            ]);

        // Chart 2: Battery Level
        int chart2Top = topPad + chartH + 20;
        DrawChart(g, data, leftPad, chart2Top, w - leftPad - rightPad, chartH,
            "Battery Level",
            [
                (r => r.BatteryLevel, Theme.Safe, "Level%"),
                (r => r.BatteryHealth, Theme.Accent, "Health%"),
            ]);
    }

    void DrawChart(Graphics g, List<HardwareMetricRow> data,
        int x, int y, int w, int h, string title,
        (Func<HardwareMetricRow, double?> getter, Color color, string legend)[] series)
    {
        // Background
        using var bgBrush = new SolidBrush(Theme.BgCard);
        g.FillRectangle(bgBrush, x, y, w, h);

        // Title
        using var titleBrush = new SolidBrush(Theme.TextSecondary);
        g.DrawString(title, Theme.CardTitle, titleBrush, x + 8, y + 4);

        // Grid lines
        using var gridPen = new Pen(Theme.Border, 1);
        int chartX = x + 4, chartY = y + 24;
        int chartW = w - 8, chartH = h - 36;

        for (int pct = 0; pct <= 100; pct += 25)
        {
            float ly = chartY + chartH - (pct / 100f * chartH);
            g.DrawLine(gridPen, chartX, ly, chartX + chartW, ly);

            using var lblBrush = new SolidBrush(Theme.TextMuted);
            g.DrawString($"{pct}", Theme.Small, lblBrush, x - 28, ly - 6);
        }

        // Time axis labels
        if (data.Count > 0)
        {
            int labelCount = Math.Min(6, data.Count);
            for (int i = 0; i < labelCount; i++)
            {
                int idx = i * (data.Count - 1) / (labelCount - 1);
                if (DateTime.TryParse(data[idx].Timestamp, out var dt))
                {
                    float lx = chartX + (float)idx / (data.Count - 1) * chartW;
                    using var lblBrush = new SolidBrush(Theme.TextMuted);
                    g.DrawString(dt.ToLocalTime().ToString("HH:mm"), Theme.Small, lblBrush, lx - 12, chartY + chartH + 2);
                }
            }
        }

        // Plot each series
        int legendX = x + w - 140;
        int legendY = y + 4;

        foreach (var (getter, color, legend) in series)
        {
            if (color == Color.Empty) continue;

            var points = new List<PointF>();
            for (int i = 0; i < data.Count; i++)
            {
                var val = getter(data[i]);
                if (val is null) continue;
                float px = chartX + (float)i / (data.Count - 1) * chartW;
                float py = chartY + chartH - ((float)val.Value / 100f * chartH);
                py = Math.Clamp(py, chartY, chartY + chartH);
                points.Add(new PointF(px, py));
            }

            if (points.Count >= 2)
            {
                using var pen = new Pen(color, 2f);
                g.DrawLines(pen, points.ToArray());
            }

            // Legend
            using var legBrush = new SolidBrush(color);
            g.FillRectangle(legBrush, legendX, legendY + 2, 10, 10);
            using var legText = new SolidBrush(Theme.TextSecondary);
            g.DrawString(legend, Theme.Small, legText, legendX + 14, legendY);
            legendY += 14;
        }
    }

    // -----------------------------------------------------------------------
    //  Raw Data tab
    // -----------------------------------------------------------------------

    void RefreshRawData(PerfReading perf, SystemStaticInfo wmiStatic, DynamicHwInfo? dyn)
    {
        _gridRaw.SuspendLayout();
        _gridRaw.Rows.Clear();

        // Perf readings
        _gridRaw.Rows.Add("Perf", "CPU Load", $"{perf.CpuPercent:F1}%");
        _gridRaw.Rows.Add("Perf", "GPU Load", $"{perf.GpuPercent:F1}%");
        _gridRaw.Rows.Add("Perf", "RAM Used", $"{perf.RamUsedPercent}%");
        _gridRaw.Rows.Add("Perf", "RAM Total", $"{perf.RamTotalMB} MB");
        _gridRaw.Rows.Add("Perf", "RAM Available", $"{perf.RamAvailMB} MB");
        _gridRaw.Rows.Add("Perf", "Disk Read", $"{perf.DiskReadBps / (1024f * 1024f):F2} MB/s");
        _gridRaw.Rows.Add("Perf", "Disk Write", $"{perf.DiskWriteBps / (1024f * 1024f):F2} MB/s");

        // WMI static
        _gridRaw.Rows.Add("WMI", "CPU Name", wmiStatic.CpuName);
        _gridRaw.Rows.Add("WMI", "CPU Cores", wmiStatic.CpuCores.ToString());
        _gridRaw.Rows.Add("WMI", "CPU Threads", wmiStatic.CpuThreads.ToString());
        _gridRaw.Rows.Add("WMI", "Total RAM", $"{wmiStatic.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GB");
        _gridRaw.Rows.Add("WMI", "OS", wmiStatic.OsCaption);
        _gridRaw.Rows.Add("WMI", "OS Build", wmiStatic.OsBuild);
        _gridRaw.Rows.Add("WMI", "BIOS", wmiStatic.BiosVersion);
        _gridRaw.Rows.Add("WMI", "Board", wmiStatic.BoardModel);
        _gridRaw.Rows.Add("WMI", "GPU Name", wmiStatic.GpuName);
        _gridRaw.Rows.Add("WMI", "Boot Time", wmiStatic.BootTime.ToString("g"));

        // WMI dynamic
        if (dyn is not null)
        {
            _gridRaw.Rows.Add("WMI", "Thermal Zone", dyn.ThermalZoneTempC is float t ? $"{t:F1} \u00B0C" : "N/A");

            if (dyn.Battery is BatteryHealth b)
            {
                _gridRaw.Rows.Add("Battery", "Charge", $"{b.ChargePercent:F1}%");
                _gridRaw.Rows.Add("Battery", "Design Capacity", $"{b.DesignCapacityMWh} mWh");
                _gridRaw.Rows.Add("Battery", "Full Charge Cap", $"{b.FullChargeCapacityMWh} mWh");
                _gridRaw.Rows.Add("Battery", "Degradation", $"{b.DegradationPercent:F1}%");
                _gridRaw.Rows.Add("Battery", "Cycle Count", b.CycleCount.ToString());
                _gridRaw.Rows.Add("Battery", "Charging", b.IsCharging.ToString());
                _gridRaw.Rows.Add("Battery", "AC Connected", b.IsAcConnected.ToString());
            }

            if (dyn.Drives is not null)
            {
                foreach (var d in dyn.Drives)
                {
                    _gridRaw.Rows.Add("Drive", $"{d.Name} Health", d.HealthStatus);
                    _gridRaw.Rows.Add("Drive", $"{d.Name} Size", FormatBytes(d.SizeBytes));
                    _gridRaw.Rows.Add("Drive", $"{d.Name} Free", FormatBytes(d.FreeBytes));
                }
            }
        }

        _gridRaw.ResumeLayout();
    }

    // -----------------------------------------------------------------------
    //  Grid factory (same pattern as NetworkForm)
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
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(tab.Text, Theme.CardTitle, fgBrush, e.Bounds, sf);
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "\u2014";
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):F1} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    // -----------------------------------------------------------------------
    //  Cleanup
    // -----------------------------------------------------------------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Updated -= OnMonitorUpdated;
            // Do NOT dispose monitor — it's shared
        }
        base.Dispose(disposing);
    }
}
