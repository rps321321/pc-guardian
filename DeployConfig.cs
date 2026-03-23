using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PCGuardian;

/// <summary>
/// Baked-in IT deployment settings loaded from an embedded or sidecar deploy.json.
/// </summary>
internal sealed record DeployConfig(
    string Company,
    string Pin,
    bool TunnelEnabled,
    string TrustLevel,
    bool ShowTrayIcon,
    bool ShowMainWindow,
    string? ContactUrl,
    string? ContactPhone
);

/// <summary>
/// Loads <see cref="DeployConfig"/> from an embedded resource or a file next to the executable.
/// Falls back to safe defaults when neither source is available or parsing fails.
/// </summary>
internal static class DeployConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static DeployConfig Defaults => new(
        Company: "PC Guardian",
        Pin: "",
        TunnelEnabled: false,
        TrustLevel: "view",
        ShowTrayIcon: true,
        ShowMainWindow: true,
        ContactUrl: null,
        ContactPhone: null
    );

    /// <summary>
    /// Loads deployment configuration with the following priority:
    /// 1. Embedded resource <c>deploy.json</c> in the executing assembly.
    /// 2. <c>deploy.json</c> file next to the executable.
    /// 3. Hard-coded defaults (view-only, no tunnel, visible UI).
    /// </summary>
    public static DeployConfig Load()
    {
        try
        {
            // 1. Try embedded resource
            var json = ReadEmbeddedResource();
            if (json is not null)
                return Deserialize(json);

            // 2. Try sidecar file next to exe
            json = ReadSidecarFile();
            if (json is not null)
                return Deserialize(json);
        }
        catch
        {
            // Swallowing intentionally — return safe defaults below.
        }

        return Defaults;
    }

    private static string? ReadEmbeddedResource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        // Resource names are <RootNamespace>.<FileName>
        var resourceName = assembly.GetManifestResourceNames()
            is { Length: > 0 } names
                ? Array.Find(names, n => n.EndsWith("deploy.json", StringComparison.OrdinalIgnoreCase))
                : null;

        if (resourceName is null)
            return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? ReadSidecarFile()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (exeDir is null)
            return null;

        var path = Path.Combine(exeDir, "deploy.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static DeployConfig Deserialize(string json)
    {
        var cfg = JsonSerializer.Deserialize<DeployConfig>(json, JsonOptions);
        return cfg ?? Defaults;
    }
}
