namespace PCGuardian;

internal sealed class OnboardingForm : Form
{
    private const int StepCount = 4;

    private int _currentStep;
    private readonly Panel[] _panels = new Panel[StepCount];
    private readonly Label[] _dots = new Label[StepCount];
    private readonly Button _btnBack = new();
    private readonly Button _btnNext = new();
    private readonly CheckBox _chkStartWithWindows = new();
    private readonly CheckBox _chkScanOnStartup = new();
    private readonly CheckBox _chkNotifications = new();

    public bool ShouldRunFirstScan { get; private set; }
    public bool StartWithWindows { get; private set; }
    public bool ScanOnStartup { get; private set; }
    public bool ShowNotifications { get; private set; }

    public OnboardingForm()
    {
        InitializeForm();
        BuildStepPanels();
        BuildNavigation();
        BuildStepDots();
        ShowStep(0);
    }

    private void InitializeForm()
    {
        Text = "Welcome to PC Guardian";
        Size = new Size(520, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;
    }

    // ── Step Panels ────────────────────────────────────────────

    private void BuildStepPanels()
    {
        _panels[0] = BuildWelcomePage();
        _panels[1] = BuildFeaturesPage();
        _panels[2] = BuildSetupPage();
        _panels[3] = BuildReadyPage();

        foreach (var panel in _panels)
        {
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            panel.Padding = new Padding(40, 30, 40, 60);
            Controls.Add(panel);
        }
    }

    private Panel BuildWelcomePage()
    {
        var panel = new Panel();

        var icon = MakeLabel("\U0001f6e1\ufe0f", Theme.BigIcon);
        icon.Bounds = new Rectangle(0, 40, 440, 70);
        icon.TextAlign = ContentAlignment.MiddleCenter;

        var title = MakeLabel("Welcome to PC Guardian",
            new Font("Segoe UI Semibold", 18f));
        title.Bounds = new Rectangle(0, 120, 440, 40);
        title.TextAlign = ContentAlignment.MiddleCenter;

        var subtitle = MakeLabel(
            "Your personal security scanner.\nLet's get you set up in 30 seconds.",
            new Font("Segoe UI", 10f));
        subtitle.ForeColor = Theme.TextSecondary;
        subtitle.Bounds = new Rectangle(0, 170, 440, 50);
        subtitle.TextAlign = ContentAlignment.MiddleCenter;

        panel.Controls.AddRange([icon, title, subtitle]);
        return panel;
    }

    private Panel BuildFeaturesPage()
    {
        var panel = new Panel();

        var heading = MakeLabel("Here's what PC Guardian does for you:",
            new Font("Segoe UI Semibold", 13f));
        heading.Bounds = new Rectangle(40, 30, 400, 30);

        string[] bullets =
        [
            "\U0001f50d  Scans for remote access, open ports, and suspicious programs",
            "\U0001f4ca  Tracks which programs run and when",
            "\U0001f514  Alerts you in real-time if something looks off",
            "\U0001f4c4  Creates reports you can share with your IT person",
            "\U0001f6e1\ufe0f  Everything stays on your PC \u2014 nothing is sent anywhere"
        ];

        for (int i = 0; i < bullets.Length; i++)
        {
            var lbl = MakeLabel(bullets[i], new Font("Segoe UI", 10f));
            lbl.Bounds = new Rectangle(50, 75 + i * 38, 380, 34);
            lbl.ForeColor = Theme.TextSecondary;
            panel.Controls.Add(lbl);
        }

        panel.Controls.Add(heading);
        return panel;
    }

    private Panel BuildSetupPage()
    {
        var panel = new Panel();

        var heading = MakeLabel("A couple of quick choices:",
            new Font("Segoe UI Semibold", 13f));
        heading.Bounds = new Rectangle(40, 30, 400, 30);

        StyleCheckBox(_chkStartWithWindows,
            "Start PC Guardian when my PC turns on", 90);
        StyleCheckBox(_chkScanOnStartup,
            "Run a scan automatically when the app opens", 135);
        StyleCheckBox(_chkNotifications,
            "Show me notifications when something needs attention", 180);

        panel.Controls.AddRange([heading, _chkStartWithWindows,
            _chkScanOnStartup, _chkNotifications]);
        return panel;
    }

    private Panel BuildReadyPage()
    {
        var panel = new Panel();

        var icon = MakeLabel("\u2705", Theme.BigIcon);
        icon.Bounds = new Rectangle(0, 40, 440, 70);
        icon.TextAlign = ContentAlignment.MiddleCenter;

        var title = MakeLabel("You're all set!",
            new Font("Segoe UI Semibold", 18f));
        title.Bounds = new Rectangle(0, 120, 440, 40);
        title.TextAlign = ContentAlignment.MiddleCenter;

        var subtitle = MakeLabel(
            "Click 'Get Started' to run your first scan.",
            new Font("Segoe UI", 10f));
        subtitle.ForeColor = Theme.TextSecondary;
        subtitle.Bounds = new Rectangle(0, 170, 440, 30);
        subtitle.TextAlign = ContentAlignment.MiddleCenter;

        panel.Controls.AddRange([icon, title, subtitle]);
        return panel;
    }

    // ── Navigation Buttons ─────────────────────────────────────

    private void BuildNavigation()
    {
        StyleButton(_btnBack, "\u2190 Back", Theme.BgCard);
        _btnBack.Location = new Point(290, 340);
        _btnBack.Click += (_, _) => ShowStep(_currentStep - 1);

        StyleButton(_btnNext, "Next \u2192", Theme.Accent);
        _btnNext.Location = new Point(398, 340);
        _btnNext.Click += OnNextClicked;

        Controls.AddRange([_btnBack, _btnNext]);
        _btnBack.BringToFront();
        _btnNext.BringToFront();
    }

    private void OnNextClicked(object? sender, EventArgs e)
    {
        if (_currentStep < StepCount - 1)
        {
            ShowStep(_currentStep + 1);
            return;
        }

        // Final step — commit settings and close
        StartWithWindows = _chkStartWithWindows.Checked;
        ScanOnStartup = _chkScanOnStartup.Checked;
        ShowNotifications = _chkNotifications.Checked;
        ShouldRunFirstScan = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    // ── Step Indicator Dots ────────────────────────────────────

    private void BuildStepDots()
    {
        int totalWidth = StepCount * 18;
        int startX = (ClientSize.Width - totalWidth) / 2;

        for (int i = 0; i < StepCount; i++)
        {
            var dot = new Label
            {
                Text = "\u25cf",
                Font = new Font("Segoe UI", 10f),
                Size = new Size(18, 20),
                Location = new Point(startX + i * 18, 345),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent
            };
            _dots[i] = dot;
            Controls.Add(dot);
            dot.BringToFront();
        }
    }

    // ── Show / Hide Logic ──────────────────────────────────────

    private void ShowStep(int step)
    {
        if (step < 0 || step >= StepCount) return;
        _currentStep = step;

        for (int i = 0; i < StepCount; i++)
        {
            _panels[i].Visible = i == step;
            _dots[i].ForeColor = i == step ? Theme.Accent : Theme.TextMuted;
        }

        _btnBack.Visible = step > 0;

        bool isLast = step == StepCount - 1;
        _btnNext.Text = isLast ? "Get Started" : "Next \u2192";
        _btnNext.BackColor = Theme.Accent;

        _btnNext.Location = new Point(398, 340);
    }

    // ── Helpers ────────────────────────────────────────────────

    private static Label MakeLabel(string text, Font font) => new()
    {
        Text = text,
        Font = font,
        ForeColor = Theme.TextPrimary,
        BackColor = Color.Transparent,
        AutoSize = false
    };

    private static void StyleButton(Button btn, string text, Color bgColor)
    {
        btn.Text = text;
        btn.Size = new Size(100, 38);
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.BackColor = bgColor;
        btn.ForeColor = Theme.TextPrimary;
        btn.Font = new Font("Segoe UI Semibold", 9.5f);
        btn.Cursor = Cursors.Hand;
    }

    private static void StyleCheckBox(CheckBox chk, string text, int top)
    {
        chk.Text = text;
        chk.Checked = true;
        chk.Bounds = new Rectangle(55, top, 380, 28);
        chk.Font = new Font("Segoe UI", 10f);
        chk.ForeColor = Theme.TextPrimary;
        chk.FlatStyle = FlatStyle.Flat;
        chk.Cursor = Cursors.Hand;
    }
}
