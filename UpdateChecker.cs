using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PCGuardian;

internal sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string? DownloadUrl,
    string? ReleaseNotes);

internal static class UpdateChecker
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private const string VersionUrl =
        "https://raw.githubusercontent.com/pcguardian/releases/main/version.json";

    public static string CurrentVersion =>
        Application.ProductVersion ?? "1.0.0";

    public static async Task<UpdateInfo> CheckAsync()
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<VersionPayload>(VersionUrl);

            if (payload?.Version is null)
                return NoUpdate();

            if (!Version.TryParse(payload.Version, out var latest) ||
                !Version.TryParse(CurrentVersion, out var current))
                return NoUpdate();

            bool hasUpdate = latest > current;

            return new UpdateInfo(
                CurrentVersion,
                payload.Version,
                hasUpdate,
                hasUpdate ? payload.Url : null,
                hasUpdate ? payload.Notes : null);
        }
        catch
        {
            return NoUpdate();
        }
    }

    public static async Task CheckAndNotify(NotifyIcon? tray)
    {
        if (tray is null) return;

        try
        {
            var info = await CheckAsync();
            if (!info.UpdateAvailable) return;

            tray.BalloonTipTitle = "PC Guardian Update Available";
            tray.BalloonTipText = $"Version {info.LatestVersion} is available (current: {info.CurrentVersion}).";
            tray.BalloonTipIcon = ToolTipIcon.Info;
            tray.ShowBalloonTip(5000);
        }
        catch
        {
            // Network failures must never surface to the user.
        }
    }

    private static UpdateInfo NoUpdate() =>
        new(CurrentVersion, CurrentVersion, false, null, null);

    private sealed record VersionPayload(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("notes")] string? Notes);
}
