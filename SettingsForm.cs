namespace PCGuardian;

internal sealed class SettingsForm : Form
{
    readonly AppSettings _settings;
    readonly Database _db;
    readonly Action _onSave;
    readonly ToolTip _tip;

    // Controls
    ComboBox cboInterval = null!;
    CheckBox chkScanOnStartup = null!;
    CheckBox chkStartWithWindows = null!;
    CheckBox chkMinimizeToTray = null!;
    CheckBox chkNotifications = null!;
    CheckBox chkProcessMonitor = null!;
    ComboBox cboSnapshotInterval = null!;
    ComboBox cboRetention = null!;
    Label lblDbSize = null!;

    // IT Sharing
    CheckBox chkITSharing = null!;
    CheckBox chkTunnel = null!;
    TextBox txtPin = null!;
    Label lblShareUrl = null!;
    string? _tunnelUrl;
    string? _tunnelStatus;

    public SettingsForm(AppSettings settings, Database db, Action onSave, string? tunnelUrl = null, string? tunnelStatus = null)
    {
        _settings = settings;
        _db = db;
        _onSave = onSave;
        _tunnelUrl = tunnelUrl;
        _tunnelStatus = tunnelStatus;

        _tip = DarkTooltip.Create();

        BuildUI();
        LoadValues();
    }

    void BuildUI()
    {
        Text = "Settings \u2014 PC Guardian";
        Size = new(500, 780);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.BgPrimary,
        };

        int left = 28;
        int right = 430;
        int y = 20;

        // ── Scanning ──────────────────────────────────
        y = AddSection(scroll, "Scanning", left, y);

        y = AddDropdownRow(scroll, "Automatic scan interval",
            ["5 minutes", "15 minutes", "30 minutes", "1 hour", "2 hours", "Off"],
            left, y, right, out cboInterval,
            "Pick how often your PC gets checked automatically.\nChoose \"Off\" if you only want to scan when you feel like it.");

        chkScanOnStartup = AddCheckbox(scroll, "Run a scan when app starts", left, ref y,
            "When you open PC Guardian, it'll check your PC\nright away so you don't have to remember.");

        y = AddSpacer(scroll, left, y);

        // ── Behavior ──────────────────────────────────
        y = AddSection(scroll, "Behavior", left, y);

        var startLabel = AdminHelper.IsAdmin()
            ? "Start with Windows (with full access)"
            : "Start with Windows";
        var startTip = AdminHelper.IsAdmin()
            ? "PC Guardian will start automatically with full admin\naccess every time you turn on your PC.\nNo extra prompts \u2014 it just works."
            : "PC Guardian will start when you log in, but without\nfull access. Use \"Unlock Full Access\" on the home\nscreen to set up permanent admin access.";
        chkStartWithWindows = AddCheckbox(scroll, startLabel, left, ref y, startTip);

        chkMinimizeToTray = AddCheckbox(scroll, "Minimize to system tray on close", left, ref y,
            "When you hit the X button, the app doesn't actually\nclose \u2014 it hides near your clock and keeps working.\nRight-click the little shield icon to bring it back.");

        chkNotifications = AddCheckbox(scroll, "Show tray notifications when issues found", left, ref y,
            "You'll get a little pop-up near your clock if\nsomething looks off. Handy so you don't miss anything.");

        y = AddSpacer(scroll, left, y);

        // ── Process Monitor ───────────────────────────
        y = AddSection(scroll, "Process Monitor", left, y);

        chkProcessMonitor = AddCheckbox(scroll, "Enable process activity logging", left, ref y,
            "Keeps a diary of every program that opens and closes\non your PC. You can look through it anytime in the\nActivity Log to see what's been happening.");

        y = AddDropdownRow(scroll, "Snapshot interval",
            ["10 seconds", "30 seconds", "1 minute", "5 minutes"],
            left, y, right, out cboSnapshotInterval,
            "How often to look for new or closed programs.\nShorter = catches more, but your PC works a tiny bit harder.\n30 seconds is a good balance for most people.");

        y = AddSpacer(scroll, left, y);

        // ── IT Sharing ───────────────────────────────
        y = AddSection(scroll, "Share with IT", left, y);

        chkITSharing = AddCheckbox(scroll, "Let your IT team view results remotely", left, ref y,
            "Turn this on and your IT person can see your\nscan results from their own computer \u2014 just by\nopening a link. No software needed on their end.");

        chkTunnel = AddCheckbox(scroll, "Enable remote access (via secure tunnel)", left, ref y,
            "Creates a secure tunnel so your IT person can\naccess your PC from anywhere on the internet \u2014\nnot just your local network. Free and encrypted.");

        // PIN
        var lblPin = new Label
        {
            Text = "PIN (optional)",
            Font = Theme.CardBody,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new(left, y + 4),
            Cursor = Cursors.Help,
        };
        _tip.SetToolTip(lblPin, "Set a short code so only your IT person can see the\nresults. Leave empty if your network is already private.");
        scroll.Controls.Add(lblPin);

        txtPin = new TextBox
        {
            Font = Theme.CardBody,
            BackColor = Theme.BgCard,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new(90, 26),
            Location = new(right - 90, y),
            MaxLength = 6,
            PlaceholderText = "e.g. 1234",
        };
        _tip.SetToolTip(txtPin, "A short code your IT person types in before they\ncan see your results. Up to 6 characters.");
        scroll.Controls.Add(txtPin);
        y += 36;

        // Remote access status + URL
        if (!string.IsNullOrEmpty(_tunnelUrl))
        {
            // Tunnel is active — show the URL prominently
            var lblStatus = new Label
            {
                Text = "Remote Access: Connected",
                Font = new Font("Segoe UI Semibold", 10f),
                ForeColor = Theme.Safe,
                AutoSize = true,
                Location = new(left, y),
            };
            scroll.Controls.Add(lblStatus);
            y += 24;

            var fullUrl = _tunnelUrl + "/terminal";
            lblShareUrl = new Label
            {
                Text = fullUrl,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Theme.Accent,
                AutoSize = true,
                Location = new(left, y),
                Cursor = Cursors.Hand,
            };
            lblShareUrl.Click += (_, _) =>
            {
                Clipboard.SetText($"URL: {fullUrl}\nPIN: {txtPin.Text.Trim()}");
                lblShareUrl.Text = fullUrl + "  (copied!)";
            };
            _tip.SetToolTip(lblShareUrl, "Click to copy the URL and PIN to clipboard.\nSend this to your IT person — they open it in\nany browser to access your PC remotely.");
            scroll.Controls.Add(lblShareUrl);
            y += 24;

            var btnCopy = new Button
            {
                Text = "Copy Link + PIN",
                Size = new(160, 32),
                Location = new(left, y),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
            };
            btnCopy.FlatAppearance.BorderSize = 0;
            btnCopy.Click += (_, _) =>
            {
                var pin = txtPin.Text.Trim();
                Clipboard.SetText($"PC Guardian Remote Access\nURL: {fullUrl}\nPIN: {pin}");
                btnCopy.Text = "Copied!";
                var t = new System.Windows.Forms.Timer { Interval = 2000 };
                t.Tick += (_, _) => { btnCopy.Text = "Copy Link + PIN"; t.Dispose(); };
                t.Start();
            };
            _tip.SetToolTip(btnCopy, "Copies the URL and PIN so you can paste\nit into a chat, email, or text to your IT person.");
            scroll.Controls.Add(btnCopy);
            y += 40;
        }
        else
        {
            // No tunnel — show status or local URL
            var statusText = !string.IsNullOrEmpty(_tunnelStatus) ? _tunnelStatus : "Not connected";
            var statusColor = statusText.Contains("Download") ? Theme.Warning : Theme.TextMuted;

            var lblStatus = new Label
            {
                Text = $"Remote Access: {statusText}",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = statusColor,
                AutoSize = true,
                Location = new(left, y),
            };
            scroll.Controls.Add(lblStatus);
            y += 22;

            var ip = ITServer.GetLocalIp();
            lblShareUrl = new Label
            {
                Text = $"Local network: http://{ip}:7777/terminal",
                Font = Theme.Small,
                ForeColor = Theme.TextMuted,
                AutoSize = true,
                Location = new(left, y),
            };
            _tip.SetToolTip(lblShareUrl, "This only works if your IT person is on\nthe same WiFi or network as you.");
            scroll.Controls.Add(lblShareUrl);
            y += 24;
        }

        y = AddSpacer(scroll, left, y);

        // ── Data ──────────────────────────────────────
        y = AddSection(scroll, "Data", left, y);

        y = AddDropdownRow(scroll, "Keep data for",
            ["7 days", "30 days", "90 days", "1 year", "Forever"],
            left, y, right, out cboRetention,
            "How long to remember what happened on your PC.\n\"Forever\" keeps everything until you decide to delete it.\nYou can always clear it yourself with the button below.");

        // Database size + purge
        lblDbSize = new Label
        {
            Text = "Database size: ...",
            Font = Theme.CardBody,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new(left, y + 4),
        };
        _tip.SetToolTip(lblDbSize, "How much space your saved history is using.\nThis grows over time as more activity is recorded.");
        scroll.Controls.Add(lblDbSize);

        var btnPurge = new Button
        {
            Text = "Clear All Data",
            Font = Theme.CardBody,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, Theme.Danger),
            ForeColor = Theme.Danger,
            Size = new(120, 30),
            Location = new(right - 120, y),
            Cursor = Cursors.Hand,
        };
        btnPurge.FlatAppearance.BorderColor = Color.FromArgb(80, Theme.Danger);
        btnPurge.FlatAppearance.BorderSize = 1;
        btnPurge.Click += OnPurgeClick;
        _tip.SetToolTip(btnPurge, "Erase everything PC Guardian has recorded.\nAll history, all past scans \u2014 gone for good.\nYou'll be asked to confirm first, don't worry.");
        scroll.Controls.Add(btnPurge);
        y += 48;

        y = AddSpacer(scroll, left, y);

        // ── Save / Cancel ─────────────────────────────
        var btnSave = new Button
        {
            Text = "Save",
            Font = new Font("Segoe UI Semibold", 10f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Theme.TextPrimary,
            Size = new(100, 40),
            Location = new(right - 220, y),
            Cursor = Cursors.Hand,
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
        btnSave.Click += OnSaveClick;
        _tip.SetToolTip(btnSave, "Save your choices and go back.\nChanges take effect right away.");
        scroll.Controls.Add(btnSave);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 10f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BgCard,
            ForeColor = Theme.TextSecondary,
            Size = new(100, 40),
            Location = new(right - 110, y),
            Cursor = Cursors.Hand,
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (_, _) => Close();
        _tip.SetToolTip(btnCancel, "Never mind \u2014 go back without changing anything.");
        scroll.Controls.Add(btnCancel);

        Controls.Add(scroll);
        UpdateDbSize();
    }

    // ===================================================================
    // Layout helpers
    // ===================================================================

    static int AddSection(Control parent, string title, int x, int y)
    {
        parent.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new(x, y),
        });
        return y + 32;
    }

    int AddDropdownRow(Control parent, string title, string[] items,
        int x, int y, int rightEdge, out ComboBox combo, string tooltip)
    {
        var lbl = new Label
        {
            Text = title,
            Font = Theme.CardBody,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new(x, y + 4),
            Cursor = Cursors.Help,
        };
        _tip.SetToolTip(lbl, tooltip);
        parent.Controls.Add(lbl);

        combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Theme.CardBody,
            BackColor = Theme.BgCard,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Size = new(140, 26),
            Location = new(rightEdge - 140, y),
        };
        combo.Items.AddRange(items);
        _tip.SetToolTip(combo, tooltip);
        parent.Controls.Add(combo);

        return y + 36;
    }

    CheckBox AddCheckbox(Control parent, string text, int x, ref int y, string tooltip)
    {
        var chk = new CheckBox
        {
            Text = "   " + text,
            Font = Theme.CardBody,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new(x, y),
            FlatStyle = FlatStyle.Standard,
            Appearance = Appearance.Normal,
            Cursor = Cursors.Help,
        };
        _tip.SetToolTip(chk, tooltip);
        parent.Controls.Add(chk);
        y += 30;
        return chk;
    }

    static int AddSpacer(Control parent, int x, int y)
    {
        parent.Controls.Add(new Panel
        {
            Location = new(x, y + 4),
            Size = new(400, 1),
            BackColor = Theme.Border,
        });
        return y + 16;
    }

    // ===================================================================
    // Load / Save
    // ===================================================================

    void LoadValues()
    {
        cboInterval.SelectedIndex = _settings.ScanIntervalMinutes switch
        {
            5 => 0, 15 => 1, 30 => 2, 60 => 3, 120 => 4, _ => 5,
        };
        chkScanOnStartup.Checked = _settings.ScanOnStartup;
        chkStartWithWindows.Checked = _settings.StartWithWindows;
        chkMinimizeToTray.Checked = _settings.MinimizeToTray;
        chkNotifications.Checked = _settings.ShowNotifications;
        chkProcessMonitor.Checked = _settings.ProcessMonitorEnabled;
        cboSnapshotInterval.SelectedIndex = _settings.ProcessSnapshotIntervalSec switch
        {
            10 => 0, 30 => 1, 60 => 2, 300 => 3, _ => 1,
        };
        cboRetention.SelectedIndex = _settings.DataRetentionDays switch
        {
            7 => 0, 30 => 1, 90 => 2, 365 => 3, _ => 4,
        };
        chkITSharing.Checked = _settings.ITSharingEnabled;
        chkTunnel.Checked = _settings.TunnelEnabled;
        txtPin.Text = _settings.ITSharingPin;
    }

    void OnSaveClick(object? sender, EventArgs e)
    {
        _settings.ScanIntervalMinutes = cboInterval.SelectedIndex switch
        {
            0 => 5, 1 => 15, 2 => 30, 3 => 60, 4 => 120, _ => 0,
        };
        _settings.ScanOnStartup = chkScanOnStartup.Checked;
        _settings.StartWithWindows = chkStartWithWindows.Checked;
        _settings.MinimizeToTray = chkMinimizeToTray.Checked;
        _settings.ShowNotifications = chkNotifications.Checked;
        _settings.ProcessMonitorEnabled = chkProcessMonitor.Checked;
        _settings.ProcessSnapshotIntervalSec = cboSnapshotInterval.SelectedIndex switch
        {
            0 => 10, 1 => 30, 2 => 60, 3 => 300, _ => 30,
        };
        _settings.DataRetentionDays = cboRetention.SelectedIndex switch
        {
            0 => 7, 1 => 30, 2 => 90, 3 => 365, _ => 0,
        };
        _settings.ITSharingEnabled = chkITSharing.Checked;
        _settings.TunnelEnabled = chkTunnel.Checked;
        _settings.ITSharingPin = txtPin.Text.Trim();

        // Use scheduled task (admin auto-start) when running as admin,
        // fall back to registry for standard user
        if (AdminHelper.IsAdmin())
            AdminHelper.SetupAdminAutoStart(_settings.StartWithWindows);
        else
            SettingsManager.SetStartWithWindows(_settings.StartWithWindows);
        SettingsManager.Save(_settings);
        _onSave();
        Close();
    }

    void OnPurgeClick(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "This will permanently delete ALL activity history,\nprocess logs, and scan results.\n\nThis cannot be undone. Continue?",
            "Clear All Data",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            _db.PurgeAllData();
            UpdateDbSize();
            MessageBox.Show("All data has been cleared.", "PC Guardian",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    void UpdateDbSize()
    {
        try
        {
            long bytes = _db.GetDatabaseSizeBytes();
            lblDbSize.Text = "Database size: " + bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
                _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
            };
        }
        catch { lblDbSize.Text = "Database size: unknown"; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip?.Dispose();
        base.Dispose(disposing);
    }
}
