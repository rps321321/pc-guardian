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
    RealTimeMonitor? realTimeMonitor;
    HardwareMonitor? hwMonitor;
    Report? lastReport;
    Report? previousReport;
    ToolTip tip = null!;

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
        if (settings.ProcessMonitorEnabled)
            monitor = new ProcessMonitor(db);
        if (settings.ITSharingEnabled)
        {
            itServer = new ITServer();
            try { itServer.Start(settings.ITSharingPort, string.IsNullOrWhiteSpace(settings.ITSharingPin) ? null : settings.ITSharingPin); }
            catch { }
        }

        try { hwMonitor = new HardwareMonitor(db); } catch { /* LHM init failed */ }

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

        if (startMinimized)
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
        Size = new(640, 800);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = LoadAppIcon();

        tip = DarkTooltip.Create();
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Home view (dashboard — always accessible) ---
        viewIdle = new Panel { Dock = DockStyle.Fill, Visible = false, AutoScroll = true, BackColor = Theme.BgPrimary };

        // Inner container — centered, max 800px, holds all content
        var homeInner = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.BgPrimary,
            Padding = new(0, 12, 0, 4),
        };

        // Center homeInner inside viewIdle and resize on window change
        void CenterHome()
        {
            int maxW = 800;
            int w = Math.Min(viewIdle.ClientSize.Width - 48, maxW);
            if (w < 400) w = viewIdle.ClientSize.Width - 24;
            int x = Math.Max((viewIdle.ClientSize.Width - w) / 2, 12);
            homeInner.Location = new(x, 0);
            homeInner.Width = w;
            // Resize children that depend on width
            foreach (Control c in homeInner.Controls)
            {
                if (c is FlowLayoutPanel f)
                {
                    f.Width = w;
                    // Resize cards inside action rows to fill evenly
                    int cardCount = f.Controls.Count;
                    if (cardCount > 0)
                    {
                        int gap = 6;
                        int cardW = (w - gap * (cardCount - 1)) / cardCount;
                        foreach (Control card in f.Controls)
                            card.Width = cardW;
                    }
                }
                else if (c is Panel p) p.Width = w;
                else if (c is Label l && l.Tag?.ToString() == "stretch") l.MaximumSize = new(w, 0);
            }
        }
        viewIdle.Resize += (_, _) => CenterHome();
        viewIdle.Controls.Add(homeInner);

        // Use homeInner for adding controls (replaces homeFlow)
        var homeFlow = homeInner;
        int hw = 760; // Initial width, will be overridden by CenterHome

        // Header — single label to avoid clipping
        var lblHeader = new Label
        {
            Text = "PC Guardian",
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new(0, 0, 0, 12),
        };
        homeFlow.Controls.Add(lblHeader);

        // Admin status banner
        bool isAdmin = AdminHelper.IsAdmin();
        bool hasAutoStart = AdminHelper.IsAdminAutoStartEnabled();

        if (isAdmin && hasAutoStart)
        {
            // Best state: running as admin with auto-start configured
            var pnlAdmin = new Panel { Width = hw, Height = 36, BackColor = Color.FromArgb(15, Theme.Safe), Margin = new(0, 0, 0, 8) };
            pnlAdmin.Controls.Add(new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Theme.Safe });
            pnlAdmin.Controls.Add(new Label
            {
                Text = "\u2705  Full access \u2014 always protected, even after restart",
                Font = Theme.CardBody,
                ForeColor = Theme.Safe,
                AutoSize = true,
                Location = new(14, 9),
            });
            tip.SetToolTip(pnlAdmin, "PC Guardian has full access and starts automatically\nwith your PC. You're fully protected.");
            homeFlow.Controls.Add(pnlAdmin);
        }
        else if (isAdmin && !hasAutoStart)
        {
            // Running as admin but no auto-start yet — offer to set it up
            var pnlAdmin = new Panel { Width = hw, Height = 52, BackColor = Color.FromArgb(15, Theme.Safe), Margin = new(0, 0, 0, 8) };
            pnlAdmin.Controls.Add(new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Theme.Safe });
            pnlAdmin.Controls.Add(new Label
            {
                Text = "\u2705  Full access right now",
                Font = Theme.CardBody,
                ForeColor = Theme.Safe,
                AutoSize = true,
                Location = new(14, 6),
            });
            var btnPermanent = new Button
            {
                Text = "\uD83D\uDD12 Keep full access on every restart",
                Font = Theme.Small,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, Theme.Safe),
                ForeColor = Theme.Safe,
                Size = new(250, 24),
                Location = new(14, 28),
                Cursor = Cursors.Hand,
            };
            btnPermanent.FlatAppearance.BorderColor = Color.FromArgb(50, Theme.Safe);
            btnPermanent.FlatAppearance.BorderSize = 1;
            btnPermanent.Click += (_, _) =>
            {
                if (AdminHelper.SetupAdminAutoStart(true))
                {
                    btnPermanent.Text = "\u2705  Done! Full access on every restart.";
                    btnPermanent.Enabled = false;
                    settings.StartWithWindows = true;
                    SettingsManager.Save(settings);
                }
                else
                    MessageBox.Show("Could not set up auto-start. Try again.",
                        "PC Guardian", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            tip.SetToolTip(btnPermanent, "Set it up so PC Guardian always starts with full\naccess when your PC turns on. You'll never have to\nthink about it again \u2014 it just works.");
            pnlAdmin.Controls.Add(btnPermanent);
            homeFlow.Controls.Add(pnlAdmin);
        }
        else
        {
            // Not admin — show unlock button
            var pnlAdmin = new Panel { Width = hw, Height = 58, BackColor = Color.FromArgb(20, Theme.Warning), Margin = new(0, 0, 0, 8) };
            pnlAdmin.Controls.Add(new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Theme.Warning });
            pnlAdmin.Controls.Add(new Label
            {
                Text = "\u26A0  Limited access \u2014 some checks may be incomplete",
                Font = Theme.CardBody,
                ForeColor = Theme.Warning,
                AutoSize = true,
                Location = new(14, 6),
            });
            var btnElevate = new Button
            {
                Text = "\uD83D\uDD13 Unlock Full Access (one-time setup)",
                Font = Theme.Small,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, Theme.Warning),
                ForeColor = Theme.Warning,
                Size = new(260, 26),
                Location = new(14, 30),
                Cursor = Cursors.Hand,
            };
            btnElevate.FlatAppearance.BorderColor = Color.FromArgb(60, Theme.Warning);
            btnElevate.FlatAppearance.BorderSize = 1;
            btnElevate.Click += (_, _) =>
            {
                // Restart as admin with a flag to auto-setup the scheduled task
                if (AdminHelper.RestartAsAdmin("--setup-admin"))
                    Application.Exit();
            };
            tip.SetToolTip(btnElevate, "Windows will ask you to confirm just this once.\nAfter that, PC Guardian will always have full access\n\u2014 even after restarting your PC. One click, done forever.");
            pnlAdmin.Controls.Add(btnElevate);
            tip.SetToolTip(pnlAdmin, "Without full access, some checks may show\nincomplete results. Click the button below\nto fix this permanently with one click.");
            homeFlow.Controls.Add(pnlAdmin);
        }

        // Scan card — prominent but not blocking
        var pnlScanCard = new Panel
        {
            Width = hw, Height = 100,
            BackColor = Color.FromArgb(20, Theme.Accent),
            Margin = new(0, 4, 0, 12),
        };
        var scanAccent = new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Theme.Accent };
        pnlScanCard.Controls.Add(scanAccent);
        pnlScanCard.Controls.Add(new Label
        {
            Text = "Security Scan",
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new(18, 14),
        });
        pnlScanCard.Controls.Add(new Label
        {
            Text = "Check for remote access, open ports, suspicious\nprograms, firewall issues, and more.",
            Font = Theme.CardBody,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new(18, 42),
        });
        var btnScan = MakeButton("Scan Now", 120, 36, Theme.Accent);
        btnScan.Location = new(hw - 140, 32);
        btnScan.Click += (_, _) => RunScan();
        tip.SetToolTip(btnScan, "Check your PC for anything suspicious right now.\nTakes about 10 seconds and covers everything from\nscreen sharing to antivirus to who's connected.");
        pnlScanCard.Controls.Add(btnScan);
        homeFlow.Controls.Add(pnlScanCard);

        // Quick actions grid
        homeFlow.Controls.Add(new Label
        {
            Text = "Quick Actions",
            Font = new Font("Segoe UI Semibold", 11f),
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new(0, 4, 0, 8),
        });

        var pnlActions = new FlowLayoutPanel
        {
            Width = hw, Height = 78,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new(0, 0, 0, 8),
        };
        pnlActions.Controls.Add(QuickActionCard("\uD83D\uDCCA", "Activity Log", "Process history & timeline", hw / 3 - 8,
            () => new ActivityForm(db).Show(),
            "See a timeline of everything that's been running\non your PC \u2014 when it started, when it stopped,\nand your past scan results."));
        pnlActions.Controls.Add(QuickActionCard("\u2699\uFE0F", "Settings", "Scan interval, startup, data", hw / 3 - 8,
            () => { using var f = new SettingsForm(settings, db, ApplySettings); f.ShowDialog(this); },
            "Customize how PC Guardian works for you \u2014\nhow often it scans, what it tracks, and more."));
        pnlActions.Controls.Add(QuickActionCard("\uD83D\uDCC1", "Open Database", "View raw data in explorer", hw / 3 - 8,
            () =>
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCGuardian");
                if (Directory.Exists(dir)) System.Diagnostics.Process.Start("explorer.exe", dir);
            },
            "Opens the folder where your history is saved.\nUseful if you want to back it up or see the files."));
        homeFlow.Controls.Add(pnlActions);

        // Second row — more tools
        var pnlActions2 = new FlowLayoutPanel
        {
            Width = hw, Height = 78,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new(0, 0, 0, 8),
        };
        pnlActions2.Controls.Add(QuickActionCard("\uD83C\uDF10", "Network", "Live connections monitor", hw / 3 - 8,
            () => new NetworkForm().Show(),
            "Watch in real-time which programs are connecting\nto the internet and where they're connecting to."));
        pnlActions2.Controls.Add(QuickActionCard("\uD83D\uDEA8", "Quarantine", "Block all remote access", hw / 3 - 8,
            () =>
            {
                bool active = QuarantineManager.IsQuarantineActive();
                var msg = active
                    ? "Quarantine is ON. This is blocking all remote access.\n\nTurn it off?"
                    : "This will block ALL remote access to your PC:\nRDP, VNC, SSH, TeamViewer, AnyDesk, and more.\n\nTurn it on?";
                var result = MessageBox.Show(msg, "Quarantine Mode",
                    MessageBoxButtons.YesNo, active ? MessageBoxIcon.Question : MessageBoxIcon.Warning);
                if (result == DialogResult.Yes)
                {
                    bool ok = active ? QuarantineManager.DisableQuarantine() : QuarantineManager.EnableQuarantine();
                    if (ok) SoundManager.ActionDone();
                    MessageBox.Show(ok
                        ? (active ? "Quarantine turned OFF. Remote access is allowed again." : "Quarantine turned ON. All remote access is now blocked.")
                        : "Could not change quarantine. Try running as admin.",
                        "PC Guardian", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
            },
            "Emergency button \u2014 instantly blocks all remote access\nto your PC. Use this on public WiFi or if you\nsuspect someone is connected."));
        pnlActions2.Controls.Add(QuickActionCard("\uD83C\uDFAF", "Risk Score", "Your security rating", hw / 3 - 8,
            () =>
            {
                if (lastReport == null) { MessageBox.Show("Run a scan first to see your score.", "PC Guardian"); return; }
                int score = RiskScore.Calculate(lastReport);
                MessageBox.Show(
                    $"Your Security Score: {score}/100  ({RiskScore.Grade(score)})\n\n{RiskScore.FriendlyDescription(score)}",
                    "PC Guardian \u2014 Risk Score", MessageBoxButtons.OK, MessageBoxIcon.Information);
            },
            "See your PC's security as a simple 0\u2013100 score\nwith a letter grade. Higher is better."));
        homeFlow.Controls.Add(pnlActions2);

        var pnlActions3 = new FlowLayoutPanel
        {
            Width = hw, Height = 78,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new(0, 0, 0, 8),
        };
        pnlActions3.Controls.Add(QuickActionCard("\uD83C\uDF21\uFE0F", "Hardware", "Temps, fans, storage health", hw / 3 - 8,
            () => { if (hwMonitor != null) new HardwareForm(hwMonitor, db).Show();
                    else MessageBox.Show("Hardware monitoring is not available.", "PC Guardian"); },
            "Monitor CPU and GPU temperatures, fan speeds,\nstorage health, and battery condition in real-time.\nAlso detects potential crypto miners."));
        homeFlow.Controls.Add(pnlActions3);

        // Keyboard shortcuts hint
        homeFlow.Controls.Add(new Label
        {
            Text = "Ctrl+S Scan  \u00B7  Ctrl+E Export  \u00B7  Ctrl+L Activity  \u00B7  Ctrl+N Network  \u00B7  Ctrl+H Hardware  \u00B7  F5 Refresh",
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new(0, 0, 0, 12),
        });

        // Info section
        homeFlow.Controls.Add(new Label
        {
            Text = "About",
            Font = new Font("Segoe UI Semibold", 11f),
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Margin = new(0, 4, 0, 8),
        });

        var pnlInfo = new Panel { Width = hw, Height = 110, BackColor = Theme.BgCard, Margin = new(0, 0, 0, 8) };
        pnlInfo.Controls.Add(new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Theme.TextMuted });
        string[] infoLines =
        [
            "\u2022  14 security checks across remote access, ports, firewall, AV, DNS, USB & more",
            "\u2022  Background process monitoring with full activity history",
            "\u2022  Periodic automatic scans with tray notifications",
            "\u2022  100% local \u2014 nothing leaves your computer",
            "\u2022  All data stored in SQLite at %AppData%\\PCGuardian",
        ];
        int iy = 10;
        foreach (var line in infoLines)
        {
            pnlInfo.Controls.Add(new Label
            {
                Text = line,
                Font = Theme.Small,
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                Location = new(14, iy),
                MaximumSize = new(hw - 30, 0),
            });
            iy += 18;
        }
        homeFlow.Controls.Add(pnlInfo);

        // Initial layout pass — set correct widths before first display
        CenterHome();

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
        btnSettingsR.Click += (_, _) =>
        {
            using var form = new SettingsForm(settings, db, ApplySettings);
            form.ShowDialog(this);
        };
        tip.SetToolTip(btnSettingsR, "Change how the app works \u2014 scanning schedule,\nstartup, notifications, and more.");

        var btnActivityR = MakeButton("\uD83D\uDCCA  Activity", 100, 34, Theme.BgCard);
        btnActivityR.Font = Theme.CardBody;
        btnActivityR.Location = new(222, 8);
        btnActivityR.Click += (_, _) => new ActivityForm(db).Show();
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

            var report = await Task.Run(() => ScanEngine.RunFullScan(hwMonitor));

            // Guard: form may have been disposed while scan was running
            if (IsDisposed || !IsHandleCreated) return;

            stepTimer.Stop();

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
            ShowView("results");
            lblLastScan.Text = $"Last: {report.Timestamp:h:mm tt}";

            // Tray notification if issues found and minimized
            if (WindowState == FormWindowState.Minimized && report.Overall != Status.Safe)
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
            stepTimer.Stop();
            ShowView("idle");
            MessageBox.Show($"Scan failed: {ex.Message}", "PC Guardian",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
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

        // Process monitor
        if (settings.ProcessMonitorEnabled && monitor == null)
            monitor = new ProcessMonitor(db);
        else if (!settings.ProcessMonitorEnabled && monitor != null)
        {
            monitor.Dispose();
            monitor = null;
        }

        // IT Server
        if (settings.ITSharingEnabled)
        {
            if (itServer == null) itServer = new ITServer();
            else itServer.Stop();
            itServer.Start(settings.ITSharingPort,
                string.IsNullOrWhiteSpace(settings.ITSharingPin) ? null : settings.ITSharingPin);
            if (lastReport != null) itServer.UpdateReport(lastReport);
        }
        else
        {
            itServer?.Stop();
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
            tray.ShowBalloonTip(2000, "PC Guardian",
                "Running in the background. Right-click the tray icon for options.", ToolTipIcon.Info);
            return;
        }
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

    void HandleAlert(SecurityAlert alert)
    {
        if (IsDisposed) return;
        SoundManager.Alert();
        tray.BalloonTipTitle = $"PC Guardian \u2014 {alert.Title}";
        tray.BalloonTipText = alert.Detail;
        tray.BalloonTipIcon = alert.Severity == AlertSeverity.Danger ? ToolTipIcon.Error : ToolTipIcon.Warning;
        tray.ShowBalloonTip(5000);
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
                new ActivityForm(db).Show();
                return true;
            case Keys.Control | Keys.N: // Ctrl+N = Network Monitor
                new NetworkForm().Show();
                return true;
            case Keys.Control | Keys.H:
                if (hwMonitor != null) new HardwareForm(hwMonitor, db).Show();
                return true;
            case Keys.Control | Keys.Oemcomma: // Ctrl+, = Settings
                using (var f = new SettingsForm(settings, db, ApplySettings))
                    f.ShowDialog(this);
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
            hwMonitor?.Dispose();
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

    Panel QuickActionCard(string icon, string title, string desc, int width, Action onClick, string tooltip = "")
    {
        var card = new Panel
        {
            Width = width,
            Height = 72,
            BackColor = Theme.BgCard,
            Margin = new(0, 0, 8, 0),
            Cursor = Cursors.Hand,
        };

        card.Controls.Add(new Label
        {
            Text = title,
            Font = Theme.CardTitle,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new(14, 12),
            BackColor = Color.Transparent,
        });
        card.Controls.Add(new Label
        {
            Text = desc,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new(14, 34),
            MaximumSize = new(width - 24, 0),
            BackColor = Color.Transparent,
        });
        // Accent left bar
        card.Controls.Add(new Panel { Width = 3, Dock = DockStyle.Left, BackColor = Theme.Accent });

        void OnClick(object? s, EventArgs e) => onClick();
        void Hover(object? s, EventArgs e) => card.BackColor = Theme.BgCardHover;
        void Leave(object? s, EventArgs e) => card.BackColor = Theme.BgCard;

        card.Click += OnClick;
        card.MouseEnter += Hover;
        card.MouseLeave += Leave;
        foreach (Control c in card.Controls)
        {
            c.Click += OnClick;
            c.MouseEnter += Hover;
            c.MouseLeave += Leave;
        }

        if (!string.IsNullOrEmpty(tooltip))
        {
            tip.SetToolTip(card, tooltip);
            foreach (Control c in card.Controls)
                tip.SetToolTip(c, tooltip);
        }

        return card;
    }

    static Icon LoadAppIcon()
    {
        try
        {
            // Load from embedded resource — works in single-file publish
            var stream = typeof(MainForm).Assembly.GetManifestResourceStream("guardian.ico");
            if (stream != null)
                return new Icon(stream);
        }
        catch { }
        return SystemIcons.Shield;
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
