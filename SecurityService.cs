using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCGuardian;

internal sealed record SecurityPosture(
    bool? BitLockerEnabled,
    bool? SecureBootEnabled,
    bool? TpmPresent,
    string? TpmVersion,
    bool UacEnabled,
    int UacConsentLevel,
    bool AutoLoginEnabled,
    bool AutoLoginPasswordStored,
    int PasswordMinLength,
    int LockoutThreshold,
    bool? GuestAccountEnabled,
    int? ScreenLockTimeoutSec,
    bool ScreenLockRequiresPassword,
    bool RebootPending);

internal sealed class SecurityService
{
    SecurityPosture? _cached;
    DateTime _cachedAt;
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns the current security posture, using a cached result if less than 5 minutes old.
    /// </summary>
    public SecurityPosture GetPosture()
    {
        if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        _cached = Refresh();
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    SecurityPosture Refresh()
    {
        var bitLocker = CheckBitLocker();
        var secureBoot = CheckSecureBoot();
        var (tpmPresent, tpmVersion) = CheckTpm();
        var (uacEnabled, uacConsent) = CheckUac();
        var (autoLogin, autoLoginPwd) = CheckAutoLogin();
        var (pwdMinLen, lockoutThreshold) = CheckPasswordPolicy();
        var guestEnabled = CheckGuestAccount();
        var (screenTimeout, screenSecure) = CheckScreenLock();
        var rebootPending = CheckRebootPending();

        return new SecurityPosture(
            BitLockerEnabled: bitLocker,
            SecureBootEnabled: secureBoot,
            TpmPresent: tpmPresent,
            TpmVersion: tpmVersion,
            UacEnabled: uacEnabled,
            UacConsentLevel: uacConsent,
            AutoLoginEnabled: autoLogin,
            AutoLoginPasswordStored: autoLoginPwd,
            PasswordMinLength: pwdMinLen,
            LockoutThreshold: lockoutThreshold,
            GuestAccountEnabled: guestEnabled,
            ScreenLockTimeoutSec: screenTimeout,
            ScreenLockRequiresPassword: screenSecure,
            RebootPending: rebootPending);
    }

    // -----------------------------------------------------------------------
    // 1. BitLocker (admin required)
    // -----------------------------------------------------------------------

    static bool? CheckBitLocker()
    {
        if (!AdminHelper.IsAdmin()) return null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='C:'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var status = Convert.ToInt32(obj["ProtectionStatus"]);
                return status == 1; // 0=OFF, 1=ON
            }

            return false;
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // 2. Secure Boot (no admin)
    // -----------------------------------------------------------------------

    static bool? CheckSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key is null) return false;

            var val = key.GetValue("UEFISecureBootEnabled");
            return val is int i && i == 1;
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // 3. TPM (admin required)
    // -----------------------------------------------------------------------

    static (bool? Present, string? Version) CheckTpm()
    {
        if (!AdminHelper.IsAdmin()) return (null, null);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftTpm",
                "SELECT IsActivated_InitialValue, IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm");

            foreach (ManagementObject obj in searcher.Get())
            {
                var isActivated = obj["IsActivated_InitialValue"] is true;
                var isEnabled = obj["IsEnabled_InitialValue"] is true;
                var specVersion = obj["SpecVersion"]?.ToString();

                // SpecVersion looks like "2.0, 0, 1.59" — take first segment
                var version = specVersion?.Split(',').FirstOrDefault()?.Trim();

                return (isActivated && isEnabled, version);
            }

            return (false, null);
        }
        catch { return (null, null); }
    }

    // -----------------------------------------------------------------------
    // 4. UAC Level (no admin)
    // -----------------------------------------------------------------------

    static (bool Enabled, int ConsentLevel) CheckUac()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            if (key is null) return (false, 0);

            var enableLua = key.GetValue("EnableLUA") is int lua ? lua == 1 : false;
            var consent = key.GetValue("ConsentPromptBehaviorAdmin") is int c ? c : 5;

            return (enableLua, consent);
        }
        catch { return (false, 0); }
    }

    // -----------------------------------------------------------------------
    // 5. Auto-Login (no admin)
    // -----------------------------------------------------------------------

    static (bool Enabled, bool PasswordStored) CheckAutoLogin()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (key is null) return (false, false);

            var autoLogon = key.GetValue("AutoAdminLogon")?.ToString() == "1";
            var hasPassword = key.GetValue("DefaultPassword") is not null;

            return (autoLogon, hasPassword);
        }
        catch { return (false, false); }
    }

    // -----------------------------------------------------------------------
    // 6. Password Policy (no admin) — parse net.exe accounts
    // -----------------------------------------------------------------------

    static (int MinLength, int LockoutThreshold) CheckPasswordPolicy()
    {
        try
        {
            var output = Run("net.exe", "accounts");
            var minLen = ParseNetAccountsInt(output, "Minimum password length");
            var lockout = ParseNetAccountsInt(output, "Lockout threshold");

            return (minLen, lockout);
        }
        catch { return (0, 0); }
    }

    static int ParseNetAccountsInt(string output, string label)
    {
        // Lines look like: "Minimum password length                  0"
        // or "Lockout threshold                        Never"
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(label, StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split(':');
            if (parts.Length < 2) continue;

            var value = parts[1].Trim();
            if (value.Equals("Never", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (int.TryParse(Regex.Match(value, @"\d+").Value, out var num))
                return num;
        }

        return 0;
    }

    // -----------------------------------------------------------------------
    // 7. Guest Account (no admin)
    // -----------------------------------------------------------------------

    static bool? CheckGuestAccount()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Disabled FROM Win32_UserAccount WHERE LocalAccount=True AND Name='Guest'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var disabled = obj["Disabled"] is true;
                return !disabled; // true = guest is enabled (danger)
            }

            return null;
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // 8. Screen Lock (no admin)
    // -----------------------------------------------------------------------

    static (int? TimeoutSec, bool RequiresPassword) CheckScreenLock()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key is null) return (null, false);

            var active = key.GetValue("ScreenSaveActive")?.ToString() == "1";
            if (!active) return (null, false);

            int? timeout = null;
            if (int.TryParse(key.GetValue("ScreenSaveTimeOut")?.ToString(), out var t))
                timeout = t;

            var secure = key.GetValue("ScreenSaverIsSecure")?.ToString() == "1";

            return (timeout, secure);
        }
        catch { return (null, false); }
    }

    // -----------------------------------------------------------------------
    // 9. Pending Updates (no admin)
    // -----------------------------------------------------------------------

    static bool CheckRebootPending()
    {
        try
        {
            // Check Windows Update reboot required
            using var wuKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wuKey is not null) return true;

            // Check Component Based Servicing reboot pending
            using var cbsKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbsKey is not null) return true;

            return false;
        }
        catch { return false; }
    }

    // -----------------------------------------------------------------------
    // Helper — run a process and capture stdout (duplicated from ScanEngine)
    // -----------------------------------------------------------------------

    static string Run(string exe, string args)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();

            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(); } catch { }
            }
            return output.Trim();
        }
        catch { return ""; }
    }
}
