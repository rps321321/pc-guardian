namespace PCGuardian;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Single instance check
        using var mutex = new Mutex(true, "PCGuardian_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("PC Guardian is already running.\nCheck your system tray.",
                "PC Guardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);

        // --setup-admin: auto-create scheduled task for permanent admin access
        if (args.Contains("--setup-admin") && AdminHelper.IsAdmin())
        {
            AdminHelper.SetupAdminAutoStart(true);
            var s = SettingsManager.Load();
            s.StartWithWindows = true;
            SettingsManager.Save(s);
        }

        // First-run onboarding
        var settings = SettingsManager.Load();
        if (!settings.OnboardingCompleted)
        {
            using var onboarding = new OnboardingForm();
            if (onboarding.ShowDialog() == DialogResult.OK)
            {
                settings.StartWithWindows = onboarding.StartWithWindows;
                settings.ScanOnStartup = onboarding.ScanOnStartup;
                settings.ShowNotifications = onboarding.ShowNotifications;
                settings.OnboardingCompleted = true;
                SettingsManager.Save(settings);

                if (onboarding.StartWithWindows)
                    SettingsManager.SetStartWithWindows(true);
            }
            else
            {
                settings.OnboardingCompleted = true;
                SettingsManager.Save(settings);
            }
        }

        // Check for updates in background
        _ = Task.Run(async () =>
        {
            try { await UpdateChecker.CheckAndNotify(null); }
            catch { }
        });

        bool minimized = args.Contains("--minimized");
        Application.Run(new MainForm(minimized));
    }
}
