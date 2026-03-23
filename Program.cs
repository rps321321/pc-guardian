using System.Diagnostics;

namespace PCGuardian;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Auto-whitelist in Windows Defender on first run (requires admin, which we have)
        SelfWhitelist();

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

        bool minimized = args.Contains("--minimized");
        Application.Run(new MainForm(minimized));
    }

    /// <summary>
    /// Adds the exe's directory to Windows Defender exclusions on first run.
    /// Runs silently — if it fails (non-admin, Defender not present), just continues.
    /// Only runs once; creates a marker file after success.
    /// </summary>
    static void SelfWhitelist()
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PCGuardian", ".whitelisted");

            if (File.Exists(marker)) return; // Already done

            var exePath = Path.Combine(appDir, "PCGuardian.exe");
            if (!File.Exists(exePath)) return;

            // Add exclusion for the exe itself
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath '{exePath}'\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);

            // Also exclude the AppData folder (where cloudflared and DB live)
            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCGuardian");
            Directory.CreateDirectory(appDataDir);

            var psi2 = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"Add-MpPreference -ExclusionPath '{appDataDir}'\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc2 = Process.Start(psi2);
            proc2?.WaitForExit(10_000);

            // Write marker so we don't do this every launch
            File.WriteAllText(marker, DateTime.Now.ToString("o"));
        }
        catch
        {
            // Silent failure — app still works, AV might just be annoying
        }
    }
}
