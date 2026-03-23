using System.Text.Json;
using Microsoft.Win32;

namespace PCGuardian;

internal static class SettingsManager
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCGuardian");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    const string StartupKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string StartupName = "PCGuardian";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsManager] Save failed: {ex.Message}");
        }
    }

    public static void SetStartWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(StartupName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(StartupName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public static bool IsStartWithWindowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            return key?.GetValue(StartupName) != null;
        }
        catch { return false; }
    }
}
