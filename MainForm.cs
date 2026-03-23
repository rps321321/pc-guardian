namespace PCGuardian;

internal sealed class MainForm : Form
{
    // Views
    Panel viewIdle = null!;
    Panel viewScanning = null!;
    Panel viewResults = null!;
    Label lblScanStep = null!;

    // Results view controls
    Panel pnlBanner = null!;
    Label lblBannerIcon = null!;
    Label lblBannerText = null!;
    Label lblBannerStats = null!;
    FlowLayoutPanel pnlCards = null!;

    // Footer
    Label lblLastScan = null!;

    // System tray
    NotifyIcon tray = null!;
    System.Windows.Forms.Timer scanTimer = null!;
    System.Windows.Forms.Timer stepTimer = null!;

    // State
    AppSettings settings;
    string? expandedCardId;
    int stepIndex;
    bool isScanning;

    // Data
    Database db = null!;
    ProcessMonitor? monitor;
    ITServer? itServer;
    CloudflareTunnel? tunnel;
    string? tunnelUrl;
    RealTimeMonitor? realTimeMonitor;
    SystemMonitor? sysMonitor;
    Report? lastReport;
    Report? previousReport;
    ToolTip tip = null!;

    // Dashboard
    DashboardEngine? dashEngine;
    DashboardPanel? dashPanel;

    // Modeless form instances (Bug 1: track to prevent handle leaks)
    ActivityForm? activityForm;
    NetworkForm? networkForm;
    CpuGpuForm? cpuGpuForm;

    static readonly string[] ScanSteps =
    [
        "Checking if someone can control your screen...",
        "Looking for remote access apps...",
        "Scanning for open doors on your PC...",
        "Checking who your PC is talking to...",
        "Reviewing shared folders...",
        "Checking background services...",
        "Inspecting your firewall...",
        "Checking startup programs...",
        "Reviewing scheduled tasks...",
        "Checking antivirus & updates...",
        "Checking hardware health...",
        "Checking device security...",
        "Scanning security events...",
        "Almost done...",
    ];

    public MainForm(bool startMinimized)
    {
        settings = SettingsManager.Load();
        settings.StartWithWindows = SettingsManager.IsStartWithWindowsEnabled();

        // Apply saved preferences
        Theme.SetDark(settings.DarkMode);
        SoundManager.Enabled = settings.SoundsEnabled;

        // Initialize database, process monitor, IT server, real-time monitor
        db = new Database();
        db.Initialize();
        db.PurgeOldData(settings.DataRetentionDays);
        if (settings.ProcessMonitorEnabled)
            monitor = new ProcessMonitor(db, settings.ProcessSnapshotIntervalSec * 1000);

        // IT sharing — all config comes from settings (no deploy.json)
        if (settings.ITSharingEnabled)
        {
            itServer = new ITServer
            {
                TrustLevel = settings.TrustLevel ?? "standard",
                CompanyName = settings.CompanyName ?? "PC Guardian",
            };
            itServer.ScanRequested += () => RunScan();
            try
            {
                var pin = string.IsNullOrWhiteSpace(settings.ITSharingPin) ? null : settings.ITSharingPin;
                itServer.Start(settings.ITSharingPort, pin);
                if (lastReport != null) itServer.UpdateReport(lastReport);

                // Start Cloudflare tunnel if enabled
                if (settings.TunnelEnabled)
                {
                    tunnel = new CloudflareTunnel();
                    tunnel.UrlAssigned += url =>
                    {
                        tunnelUrl = url;
                        try
                        {
                            if (InvokeRequired)
                                BeginInvoke(() => UpdateTrayForTunnel(url));
                            else
                                UpdateTrayForTunnel(url);
                        }
                        catch { /* Form may be disposed */ }
                    };
                    tunnel.Start(itServer.Port);
                }
            }
            catch { /* Server or tunnel start failed — continue without */ }
        }

        try { sysMonitor = new SystemMonitor(db); } catch { /* Init failed */ }

        dashEngine = new DashboardEngine(sysMonitor, db, monitor);

        // Real-time alerts
        realTimeMonitor = new RealTimeMonitor();
        realTimeMonitor.OnAlert += alert =>
        {
            if (InvokeRequired)
                BeginInvoke(() => HandleAlert(alert));
            else
                HandleAlert(alert);
        };
        realTimeMonitor.Start();

        BuildUI();
        SetupTray();
        SetupTimers();
        ShowView("idle");

        // Check for updates in background (tray is now available)
        _ = Task.Run(async () => { try { await UpdateChecker.CheckAndNotify(tray); } catch { } });

        // Determine whether to start minimized: deploy config can force it,
        // or the caller can request it (e.g., --minimized flag)
        bool shouldMinimize = startMinimized;
        if (shouldMinimize)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Visible = false;
        }
    }

    // ===================================================================
    // UI Construction
    // ===================================================================

    void BuildUI()
    {
        Text = "PC Guardian";
        Size = new(1050, 1250);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = LoadAppIcon();

        tip = DarkTooltip.Create();
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Home view (dashboard — always accessible) ---
        viewIdle = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.BgPrimary };
        dashPanel = new DashboardPanel(dashEngine);
        dashPanel.ScanRequested += () => RunScan();
        dashPanel.ActivityRequested += () => ShowActivityForm();
        dashPanel.NetworkRequested += () => ShowNetworkForm();
        dashPanel.SettingsRequested += () => OpenSettings();
        dashPanel.FixRequested += ExecuteRecommendationFix;
        dashPanel.ThemeToggled += () =>
        {
            settings.DarkMode = Theme.IsDark;
            SettingsManager.Save(settings);
        };
        viewIdle.Controls.Add(dashPanel);

        // --- Scanning view ---
        viewScanning = new Panel { Dock = DockStyle.Fill, Visible = false };
        var lblScanShield = new Label
        {
            Text = "\uD83D\uDEE1\uFE0F",
            Font = Theme.BigIcon,
            AutoSize = true,
        };
        var lblScanning = new Label
        {
            Text = "Scanning your PC...",
            Font = new Font("Segoe UI Semibold", 14f),
            AutoSize = true,
            ForeColor = Theme.TextPrimary,
        };
        lblScanStep = new Label
        {
            Text = ScanSteps[0],
            Font = Theme.CardBody,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
        };
        var lblWait = new Label
        {
            Text = "This usually takes 10\u201315 seconds",
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
        };
        viewScanning.Controls.AddRange([lblScanShield, lblScanning, lblScanStep, lblWait]);
        viewScanning.Resize += (_, _) => CenterControls(viewScanning);

        // --- Results view ---
        viewResults = new Panel { Dock = DockStyle.Fill, Visible = false };

        // Banner
        pnlBanner = new Panel { Height = 80, Dock = DockStyle.Top, Padding = new(20, 15, 20, 15) };
        lblBannerIcon = new Label { AutoSize = true, Font = new Font("Segoe UI", 12f), Location = new(20, 18) };
        lblBannerText = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Location = new(45, 16) };
        lblBannerStats = new Label { AutoSize = true, Font = Theme.CardBody, ForeColor = Theme.TextSecondary, Location = new(45, 44) };
        var btnRescan = MakeButton("Scan Again", 110, 34, Theme.BgCardHover);
        btnRescan.Location = new(pnlBanner.Width - 140, 22);
        btnRescan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnRescan.Click += (_, _) => RunScan();
        pnlBanner.Controls.AddRange([lblBannerIcon, lblBannerText, lblBannerStats, btnRescan]);

        // Cards
        pnlCards = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.BgPrimary,
            Padding = new(20, 10, 20, 10),
        };
        // Resize cards when panel resizes
        pnlCards.Resize += (_, _) =>
        {
            int newW = Math.Max(pnlCards.ClientSize.Width - 50, 400);
            foreach (Control c in pnlCards.Controls)
                if (c is Panel p) p.Width = newW;
        };

        // Footer — clean bar with 3 buttons + last scan time
        var pnlFooter = new Panel { Height = 50, Dock = DockStyle.Bottom, BackColor = Color.FromArgb(15, 15, 18), Padding = new(10, 8, 10, 8) };

        var btnHome = MakeButton("\u2190  Home", 90, 34, Theme.BgCard);
        btnHome.Font = Theme.CardBody;
        btnHome.Location = new(12, 8);
        btnHome.Click += (_, _) => ShowView("idle");
        tip.SetToolTip(btnHome, "Back to the main screen.");

        var btnSettingsR = MakeButton("\u2699  Settings", 100, 34, Theme.BgCard);
        btnSettingsR.Font = Theme.CardBody;
        btnSettingsR.Location = new(112, 8);
        btnSettingsR.Click += (_, _) => OpenSettings();
        tip.SetToolTip(btnSettingsR, "Change how the app works \u2014 scanning schedule,\nstartup, notifications, and more.");

        var btnActivityR = MakeButton("\uD83D\uDCCA  Activity", 100, 34, Theme.BgCard);
        btnActivityR.Font = Theme.CardBody;
        btnActivityR.Location = new(222, 8);
        btnActivityR.Click += (_, _) => ShowActivityForm();
        tip.SetToolTip(btnActivityR, "See what programs have been running on your PC\nand browse your past scan results.");

        var btnExport = MakeButton("\uD83D\uDCE4  Export", 90, 34, Theme.BgCard);
        btnExport.Font = Theme.CardBody;
        btnExport.Location = new(332, 8);
        btnExport.Click += (_, _) => ExportReport();
        tip.SetToolTip(btnExport, "Save a copy of this report as a file you can\nemail to your IT person or anyone who can help.");

        lblLastScan = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        lblLastScan.Location = new(pnlFooter.Width - 160, 18);

        pnlFooter.Controls.AddRange([btnHome, btnSettingsR, btnActivityR, btnExport, lblLastScan]);

        viewResults.Controls.Add(pnlCards); // Fill goes first
        viewResults.Controls.Add(pnlBanner); // Top
        viewResults.Controls.Add(pnlFooter); // Bottom

        Controls.AddRange([viewIdle, viewScanning, viewResults]);
    }

    static void CenterControls(Panel panel)
    {
        if (panel.Controls.Count == 0) return;
        int cx = panel.ClientSize.Width / 2;
        int totalH = 0;
        foreach (Control c in panel.Controls) totalH += c.Height + 16;
        int y = (panel.ClientSize.Height - totalH) / 2;
        foreach (Control c in panel.Controls)
        {
            c.Location = new(cx - c.Width / 2, y);
            y += c.Height + 16;
        }
    }

    void ShowView(string view)
    {
        viewIdle.Visible = view == "idle";
        viewScanning.Visible = view == "scanning";
        viewResults.Visible = view == "results";

        if (view == "scanning") CenterControls(viewScanning);
    }

    // ===================================================================
    // Scanning
    // ===================================================================

    async void RunScan(bool manual = true)
    {
        if (isScanning) return; // Prevent overlapping scans
        isScanning = true;

        try
        {
            ShowView("scanning");
            stepIndex = 0;
            stepTimer.Start();

            var report = await Task.Run(() => ScanEngine.RunFullScan(sysMonitor));

            // Guard: form may have been disposed while scan was running
            if (IsDisposed || !IsHandleCreated) return;

            // Store report + save to DB + update IT server
            previousReport = lastReport;
            lastReport = report;

            // Sound: always play on manual scan; on background scan, only if issues got worse
            if (manual)
                SoundManager.ForScanResult(report.Overall);
            else if (previousReport != null && report.Overall > previousReport.Overall)
                SoundManager.ForScanResult(report.Overall);
            try { db.SaveScanResult(report.Timestamp, report.Overall.ToString(), report.SafeCount, report.WarningCount, report.DangerCount, null); }
            catch { }
            itServer?.UpdateReport(report);

            PopulateResults(report);
            dashEngine?.IngestScanResult(report);
            ShowView("results");
            lblLastScan.Text = $"Last: {report.Timestamp:h:mm tt}";

            // Tray notification if issues found and minimized
            if (settings.ShowNotifications && WindowState == FormWindowState.Minimized && report.Overall != Status.Safe)
            {
                tray.BalloonTipTitle = "PC Guardian";
                tray.BalloonTipText = report.Overall == Status.Danger
                    ? $"{report.DangerCount} issue(s) need your attention!"
                    : $"{report.WarningCount} thing(s) worth reviewing.";
                tray.BalloonTipIcon = report.Overall == Status.Danger ? ToolTipIcon.Error : ToolTipIcon.Warning;
                tray.ShowBalloonTip(5000);
            }
        }
        catch (ObjectDisposedException)
        {
            // Form was closed during scan — silently exit
        }
        catch (Exception ex)
        {
            if (IsDisposed || !IsHandleCreated) return;
            ShowView("idle");
            MessageBox.Show($"Scan failed: {ex.Message}", "PC Guardian",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            stepTimer.Stop();
            isScanning = false;
        }
    }

    void PopulateResults(Report report)
    {
        // Banner
        var sc = Theme.StatusColor(report.Overall);
        pnlBanner.BackColor = Color.FromArgb(20, sc);
        lblBannerIcon.Text = "\u25CF";
        lblBannerIcon.ForeColor = sc;
        lblBannerText.ForeColor = sc;
        lblBannerText.Text = report.Overall switch
        {
            Status.Safe => "Your PC looks secure!",
            Status.Warning => "A few things to review",
            _ => "Some things need your attention",
        };
        var parts = new List<string> { $"{report.SafeCount} of {report.Categories.Count} checks passed" };
        if (report.WarningCount > 0) parts.Add($"{report.WarningCount} to review");
        if (report.DangerCount > 0) parts.Add($"{report.DangerCount} need attention");
        if (!AdminHelper.IsAdmin()) parts.Add("limited mode");
        lblBannerStats.Text = string.Join(" \u00B7 ", parts);

        // Cards
        pnlCards.SuspendLayout();
        pnlCards.Controls.Clear();
        expandedCardId = null;

        int cardWidth = Math.Max(pnlCards.ClientSize.Width - 50, 400);
        foreach (var cat in report.Categories)
            pnlCards.Controls.Add(CreateCard(cat, cardWidth));

        pnlCards.ResumeLayout();
    }

    // ===================================================================
    // Card UI
    // ===================================================================

    Panel CreateCard(Category cat, int width)
    {
        var sc = Theme.StatusColor(cat.Status);

        var card = new Panel
        {
            Width = width,
            AutoSize = true,
            MinimumSize = new(width, 58),
            BackColor = Theme.BgCard,
            Margin = new(0, 3, 0, 3),
            Cursor = Cursors.Hand,
            Tag = cat,
        };

        // Left accent bar
        card.Controls.Add(new Panel { Width = 4, Dock = DockStyle.Left, BackColor = sc });

        // Main layout: [Icon] [Title + Badge + Summary] [Chevron]
        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new(6, 6, 6, 6),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // Icon
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // Text (fills)
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // Chevron
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // Title row
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // Summary row

        // Icon — spans both rows
        var lblIcon = new Label
        {
            Text = cat.Icon,
            Font = Theme.Icon,
            AutoSize = true,
            Margin = new(4, 2, 6, 2),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        tbl.Controls.Add(lblIcon, 0, 0);
        tbl.SetRowSpan(lblIcon, 2);

        // Title + Badge in a flow
        var titleFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        titleFlow.Controls.Add(new Label
        {
            Text = cat.Title,
            Font = Theme.CardTitle,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new(0, 2, 4, 0),
        });
        titleFlow.Controls.Add(new Label
        {
            Text = $" {Theme.StatusLabel(cat.Status)} ",
            Font = Theme.Badge,
            ForeColor = sc,
            BackColor = Color.FromArgb(30, sc),
            AutoSize = true,
            Padding = new(4, 2, 4, 2),
            Margin = new(0, 2, 0, 0),
        });
        tbl.Controls.Add(titleFlow, 1, 0);

        // Chevron — spans both rows
        var lblChevron = new Label
        {
            Text = "\u25BC",
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new(4, 8, 4, 2),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        tbl.Controls.Add(lblChevron, 2, 0);
        tbl.SetRowSpan(lblChevron, 2);

        // Summary
        tbl.Controls.Add(new Label
        {
            Text = cat.Summary,
            Font = Theme.CardBody,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new(0, 0, 0, 2),
            MaximumSize = new(width - 100, 0),
        }, 1, 1);

        card.Controls.Add(tbl);

        // Tooltip — shows what this check does + current tip
        var cardTip = $"{cat.Question}\n\nClick to see details and what you can do.";
        tip.SetToolTip(card, cardTip);
        foreach (Control c in card.Controls)
            tip.SetToolTip(c, cardTip);

        // Hover effect
        void SetHover(bool hover)
        {
            card.BackColor = hover ? Theme.BgCardHover : Theme.BgCard;
        }

        foreach (Control c in card.Controls)
        {
            c.MouseEnter += (_, _) => SetHover(true);
            c.MouseLeave += (_, _) => { if (!card.ClientRectangle.Contains(card.PointToClient(Cursor.Position))) SetHover(false); };
            c.Click += (_, _) => ToggleCard(card, cat);
        }
        card.MouseEnter += (_, _) => SetHover(true);
        card.MouseLeave += (_, _) => SetHover(false);
        card.Click += (_, _) => ToggleCard(card, cat);

        return card;
    }

    void ToggleCard(Panel card, Category cat)
    {
        pnlCards.SuspendLayout();

        // Collapse previously expanded card
        if (expandedCardId != null && expandedCardId != cat.Id)
        {
            foreach (Control ctrl in pnlCards.Controls)
            {
                if (ctrl is Panel p && p.Tag is Category c && c.Id == expandedCardId)
                {
                    CollapseCard(p);
                    break;
                }
            }
        }

        if (expandedCardId == cat.Id)
        {
            CollapseCard(card);
            expandedCardId = null;
        }
        else
        {
            ExpandCard(card, cat);
            expandedCardId = cat.Id;
        }

        pnlCards.ResumeLayout();
    }

    void CollapseCard(Panel card)
    {
        var details = card.Controls.Cast<Control>().FirstOrDefault(c => c.Name == "details");
        if (details != null)
        {
            card.Controls.Remove(details);
            details.Dispose();
        }
        // AutoSize will shrink back to just the TableLayoutPanel header
    }

    void ExpandCard(Panel card, Category cat)
    {
        // Details panel uses FlowLayoutPanel — flows naturally, no hardcoded positions
        var details = new FlowLayoutPanel
        {
            Name = "details",
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new(16, 4, 16, 8),
            BackColor = Color.Transparent,
        };
        int maxW = card.Width - 50;

        // Question header
        details.Controls.Add(new Label
        {
            Text = cat.Question.ToUpperInvariant(),
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            MaximumSize = new(maxW, 0),
            Margin = new(0, 0, 0, 6),
        });

        // Findings
        foreach (var f in cat.Findings)
        {
            var row = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new(0, 0, 0, 4),
                BackColor = Color.Transparent,
            };
            row.Controls.Add(new Label
            {
                Text = "\u25CF",
                Font = new Font("Segoe UI", 7f),
                ForeColor = Theme.StatusColor(f.Status),
                AutoSize = true,
                Margin = new(0, 3, 4, 0),
            });
            var textCol = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = Padding.Empty,
                BackColor = Color.Transparent,
            };
            textCol.Controls.Add(new Label
            {
                Text = f.Label,
                Font = new Font("Segoe UI Semibold", 8.5f),
                ForeColor = Theme.TextPrimary,
                AutoSize = true,
                MaximumSize = new(maxW - 20, 0),
            });
            textCol.Controls.Add(new Label
            {
                Text = f.Detail,
                Font = Theme.Small,
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new(maxW - 20, 0),
            });
            row.Controls.Add(textCol);
            details.Controls.Add(row);
        }

        // Separator
        details.Controls.Add(new Panel
        {
            Size = new(maxW, 1),
            BackColor = Theme.Border,
            Margin = new(0, 4, 0, 4),
        });

        // Tip
        details.Controls.Add(new Label
        {
            Text = $"What to do: {cat.Tip}",
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new(maxW, 0),
            Margin = new(0, 0, 0, 4),
        });

        card.Controls.Add(details);
    }

    // ===================================================================
    // System tray
    // ===================================================================

    void UpdateTrayForTunnel(string url)
    {
        if (tray != null)
            tray.Text = $"PC Guardian — IT: {url}";
    }

    void SetupTray()
    {
        tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "PC Guardian",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show PC Guardian", null, (_, _) => ShowFromTray());
        menu.Items.Add("Scan Now", null, (_, _) => { ShowFromTray(); RunScan(); });
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) => { tray.Visible = false; tray.Dispose(); Application.Exit(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowFromTray();
    }

    void ShowFromTray()
    {
        Visible = true;
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ===================================================================
    // Timers
    // ===================================================================

    void SetupTimers()
    {
        stepTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        stepTimer.Tick += (_, _) =>
        {
            stepIndex = (stepIndex + 1) % ScanSteps.Length;
            if (lblScanStep != null)
                lblScanStep.Text = ScanSteps[stepIndex];
        };

        scanTimer = new System.Windows.Forms.Timer();
        UpdateTimerInterval();
        scanTimer.Tick += (_, _) => RunScan(manual: false);
        if (settings.ScanIntervalMinutes > 0)
            scanTimer.Start();
    }

    void UpdateTimerInterval()
    {
        if (settings.ScanIntervalMinutes > 0)
        {
            scanTimer.Interval = Math.Clamp(settings.ScanIntervalMinutes * 60_000, 1_000, int.MaxValue);
            scanTimer.Start();
        }
        else
        {
            scanTimer.Stop();
        }
    }

    // ===================================================================
    // Settings — called after SettingsForm saves
    // ===================================================================

    void ApplySettings()
    {
        UpdateTimerInterval();
        SoundManager.Enabled = settings.SoundsEnabled;
        db.PurgeOldData(settings.DataRetentionDays);

        // Process monitor
        if (settings.ProcessMonitorEnabled && monitor == null)
            monitor = new ProcessMonitor(db, settings.ProcessSnapshotIntervalSec * 1000);
        else if (settings.ProcessMonitorEnabled && monitor != null)
        {
            // Interval may have changed — recreate
            monitor.Dispose();
            monitor = new ProcessMonitor(db, settings.ProcessSnapshotIntervalSec * 1000);
        }
        else if (!settings.ProcessMonitorEnabled && monitor != null)
        {
            monitor.Dispose();
            monitor = null;
        }

        // IT Server
        if (settings.ITSharingEnabled)
        {
            if (itServer == null)
            {
                itServer = new ITServer
                {
                    TrustLevel = settings.TrustLevel ?? "standard",
                    CompanyName = settings.CompanyName ?? "PC Guardian",
                };
                itServer.ScanRequested += () => RunScan();
            }
            else itServer.Stop();
            itServer.Start(settings.ITSharingPort,
                string.IsNullOrWhiteSpace(settings.ITSharingPin) ? null : settings.ITSharingPin);
            if (lastReport != null) itServer.UpdateReport(lastReport);

            // Tunnel — start if enabled and not already running
            if (settings.TunnelEnabled && tunnel == null)
            {
                tunnel = new CloudflareTunnel();
                tunnel.UrlAssigned += url =>
                {
                    tunnelUrl = url;
                    try
                    {
                        if (InvokeRequired)
                            BeginInvoke(() => UpdateTrayForTunnel(url));
                        else
                            UpdateTrayForTunnel(url);
                    }
                    catch { }
                };
                tunnel.Start(itServer.Port);
            }
            else if (!settings.TunnelEnabled && tunnel != null)
            {
                tunnel.Stop();
                tunnel.Dispose();
                tunnel = null;
                tunnelUrl = null;
            }
        }
        else
        {
            // Stop tunnel first
            if (tunnel != null)
            {
                tunnel.Stop();
                tunnel.Dispose();
                tunnel = null;
                tunnelUrl = null;
            }
            itServer?.Dispose();
            itServer = null;
        }
    }

    void ExportReport()
    {
        if (lastReport == null)
        {
            MessageBox.Show("Run a scan first, then you can export it.",
                "PC Guardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Save Security Report",
            FileName = $"PCGuardian-Report-{lastReport.Timestamp:yyyy-MM-dd}",
            Filter = "PDF Document (*.pdf)|*.pdf|Web Page (*.html)|*.html",
            DefaultExt = "pdf",
            FilterIndex = 1,
        };

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            try
            {
                if (dlg.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (!PdfExporter.SaveAsPdf(lastReport, dlg.FileName))
                    {
                        // Fallback to HTML if PDF fails
                        var htmlPath = Path.ChangeExtension(dlg.FileName, ".html");
                        ReportGenerator.SaveToFile(lastReport, htmlPath);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            { FileName = htmlPath, UseShellExecute = true });
                        MessageBox.Show("PDF export needs Edge or Chrome.\nOpened as HTML instead — you can print to PDF from there.",
                            "PC Guardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
                else
                {
                    ReportGenerator.SaveToFile(lastReport, dlg.FileName);
                }

                SoundManager.ActionDone();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = dlg.FileName, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save: {ex.Message}",
                    "PC Guardian", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ===================================================================
    // Form overrides
    // ===================================================================

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && settings.MinimizeToTray)
        {
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Visible = false;
            if (settings.ShowNotifications)
                tray.ShowBalloonTip(2000, "PC Guardian",
                    "Running in the background. Right-click the tray icon for options.", ToolTipIcon.Info);
            return;
        }
        realTimeMonitor?.Stop();
        tray.Visible = false;
        tray.Dispose();
        scanTimer.Stop();
        stepTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && settings.MinimizeToTray)
        {
            ShowInTaskbar = false;
            Visible = false;
        }
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    // ===================================================================
    // Real-time alert handler
    // ===================================================================

    void ExecuteRecommendationFix(string recId)
    {
        FixResult? result = null;
        string action = "";

        switch (recId)
        {
            case "disable-rdp":
                action = "Disable Remote Desktop";
                result = FixActions.DisableRemoteDesktop();
                break;
            case "enable-firewall":
                action = "Enable Windows Firewall";
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("netsh", "advfirewall set allprofiles state on")
                    { CreateNoWindow = true, UseShellExecute = false };
                    System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
                    result = new FixResult(true, "Firewall enabled on all profiles");
                }
                catch (Exception ex) { result = new FixResult(false, ex.Message); }
                break;
            case "stop-remote-apps":
                action = "Stop remote access apps";
                try
                {
                    var remoteProcs = new[] { "teamviewer", "anydesk", "vnc", "parsec", "rustdesk" };
                    int killed = 0;
                    foreach (var proc in remoteProcs)
                    {
                        var r = FixActions.KillProcess(proc);
                        if (r.Success) killed++;
                    }
                    result = new FixResult(true, $"Stopped {killed} remote access process(es)");
                }
                catch (Exception ex) { result = new FixResult(false, ex.Message); }
                break;
            case "close-ports":
                action = "Block risky ports";
                try
                {
                    var riskyPorts = new[] { (3389, "RDP"), (5900, "VNC"), (23, "Telnet") };
                    int blocked = 0;
                    foreach (var (port, desc) in riskyPorts)
                    {
                        var r = FixActions.BlockPort(port, desc);
                        if (r.Success) blocked++;
                    }
                    result = new FixResult(blocked > 0, $"Blocked {blocked} of {riskyPorts.Length} risky port(s)");
                }
                catch (Exception ex) { result = new FixResult(false, ex.Message); }
                break;
            case "fix-dns":
                action = "Reset DNS to safe servers";
                try
                {
                    // Find all active adapters and set DNS on each
                    int fixed_count = 0;
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                            or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                        var props = nic.GetIPProperties();
                        if (props.GatewayAddresses.Count == 0) continue; // skip adapters with no gateway

                        string adapterName = nic.Name;
                        // Set primary DNS to 1.1.1.1 (Cloudflare)
                        var psi1 = new System.Diagnostics.ProcessStartInfo("netsh",
                            $"interface ip set dns name=\"{adapterName}\" static 1.1.1.1 primary")
                        { CreateNoWindow = true, UseShellExecute = false };
                        System.Diagnostics.Process.Start(psi1)?.WaitForExit(10000);
                        // Set secondary DNS to 1.0.0.1 (Cloudflare backup)
                        var psi2 = new System.Diagnostics.ProcessStartInfo("netsh",
                            $"interface ip add dns name=\"{adapterName}\" 1.0.0.1 index=2")
                        { CreateNoWindow = true, UseShellExecute = false };
                        System.Diagnostics.Process.Start(psi2)?.WaitForExit(10000);
                        fixed_count++;
                    }
                    result = fixed_count > 0
                        ? new FixResult(true, $"DNS set to Cloudflare (1.1.1.1 + 1.0.0.1) on {fixed_count} adapter(s)")
                        : new FixResult(false, "No active network adapters found");
                }
                catch (Exception ex) { result = new FixResult(false, ex.Message); }
                break;
            case "disable-autologin":
                action = "Disable auto-login";
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", true);
                    key?.SetValue("AutoAdminLogon", "0");
                    key?.DeleteValue("DefaultPassword", false);
                    result = new FixResult(true, "Auto-login disabled");
                }
                catch (Exception ex) { result = new FixResult(false, ex.Message); }
                break;
            case "disable-guest":
                action = "Disable guest account";
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("net", "user Guest /active:no")
                    { CreateNoWindow = true, UseShellExecute = false };
                    System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
                    result = new FixResult(true, "Guest account disabled");
                }
                catch (Exception ex) { result = new FixResult(false, ex.Message); }
                break;
            default:
                MessageBox.Show($"No automated fix available for '{recId}'.\nCheck the scan results for manual steps.",
                    "PC Guardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
        }

        if (result != null)
        {
            if (result.Success)
            {
                SoundManager.ActionDone();
                MessageBox.Show($"✓ {action}\n\n{result.Message}", "Fix Applied — PC Guardian",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                dashEngine?.AddActivity($"Fix applied: {action}", EventSeverity.Info);
                // Re-scan to update the dashboard
                RunScan(manual: false);
            }
            else
            {
                MessageBox.Show($"✗ {action} failed\n\n{result.Message}", "Fix Failed — PC Guardian",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ===================================================================

    void HandleAlert(SecurityAlert alert)
    {
        if (IsDisposed) return;
        if (tray == null) return;
        SoundManager.Alert();
        if (settings.ShowNotifications)
        {
            tray.BalloonTipTitle = $"PC Guardian \u2014 {alert.Title}";
            tray.BalloonTipText = alert.Detail;
            tray.BalloonTipIcon = alert.Severity == AlertSeverity.Danger ? ToolTipIcon.Error : ToolTipIcon.Warning;
            tray.ShowBalloonTip(5000);
        }
        dashEngine?.AddActivity(alert.Detail, alert.Severity == AlertSeverity.Danger ? EventSeverity.Danger : EventSeverity.Warning);
    }

    // ===================================================================
    // Keyboard shortcuts
    // ===================================================================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S: // Ctrl+S = Scan
                RunScan();
                return true;
            case Keys.Control | Keys.E: // Ctrl+E = Export
                ExportReport();
                return true;
            case Keys.Control | Keys.L: // Ctrl+L = Activity Log
                ShowActivityForm();
                return true;
            case Keys.Control | Keys.N: // Ctrl+N = Network Monitor
                ShowNetworkForm();
                return true;
            case Keys.Control | Keys.H:
                ShowCpuGpuForm();
                return true;
            case Keys.Control | Keys.Oemcomma: // Ctrl+, = Settings
                OpenSettings();
                return true;
            case Keys.Escape: // Esc = go home
                ShowView("idle");
                return true;
            case Keys.F5: // F5 = Scan
                RunScan();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ===================================================================
    // Dispose
    // ===================================================================

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tunnel?.Stop();
            tunnel?.Dispose();
            dashEngine?.Dispose();
            dashPanel?.Dispose();
            sysMonitor?.Dispose();
            realTimeMonitor?.Dispose();
            itServer?.Dispose();
            tip?.Dispose();
            monitor?.Dispose();
            db?.Dispose();
            tray?.Dispose();
            scanTimer?.Dispose();
            stepTimer?.Dispose();
        }
        base.Dispose(disposing);
    }

    void OpenSettings()
    {
        var tStatus = tunnel?.IsRunning == true ? "Connected" : tunnel?.NotFoundReason;
        using var f = new SettingsForm(settings, db, ApplySettings, tunnelUrl, tStatus);
        f.ShowDialog(this);
    }

    void ShowActivityForm()
    {
        if (activityForm != null && !activityForm.IsDisposed) { activityForm.Activate(); return; }
        activityForm = new ActivityForm(db);
        activityForm.FormClosed += (_, _) => activityForm = null;
        activityForm.Show();
    }

    void ShowNetworkForm()
    {
        if (networkForm != null && !networkForm.IsDisposed) { networkForm.Activate(); return; }
        networkForm = new NetworkForm();
        networkForm.FormClosed += (_, _) => networkForm = null;
        networkForm.Show();
    }

    void ShowCpuGpuForm()
    {
        if (sysMonitor == null) return;
        if (cpuGpuForm != null && !cpuGpuForm.IsDisposed) { cpuGpuForm.Activate(); return; }
        cpuGpuForm = new CpuGpuForm(sysMonitor, db);
        cpuGpuForm.FormClosed += (_, _) => cpuGpuForm = null;
        cpuGpuForm.Show();
    }

    static Icon LoadAppIcon()
    {
        try
        {
            // Load from embedded resource — works in single-file publish
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("guardian.ico");
            if (stream != null)
                return new Icon(stream);
        }
        catch { }
        return new Icon(SystemIcons.Shield, SystemIcons.Shield.Size);
    }

    static Button MakeButton(string text, int w, int h, Color bg)
    {
        var btn = new Button
        {
            Text = text,
            Size = new(w, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Theme.TextPrimary,
            Font = new Font("Segoe UI Semibold", 11f),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(
            Math.Min(bg.R + 20, 255), Math.Min(bg.G + 20, 255), Math.Min(bg.B + 20, 255));
        return btn;
    }
}
