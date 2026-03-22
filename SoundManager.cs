using System.Media;

namespace PCGuardian;

/// <summary>
/// Subtle audio cues for key events. Uses Windows system sounds — no external files needed.
/// </summary>
internal static class SoundManager
{
    static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Scan completed successfully — subtle chime.</summary>
    public static void ScanComplete() { if (_enabled) SystemSounds.Asterisk.Play(); }

    /// <summary>Something dangerous found — attention-grabbing.</summary>
    public static void DangerFound() { if (_enabled) SystemSounds.Exclamation.Play(); }

    /// <summary>Warning found — mild alert.</summary>
    public static void WarningFound() { if (_enabled) SystemSounds.Hand.Play(); }

    /// <summary>All clear — reassuring.</summary>
    public static void AllClear() { if (_enabled) SystemSounds.Asterisk.Play(); }

    /// <summary>Action completed (fix applied, export saved, etc).</summary>
    public static void ActionDone() { if (_enabled) SystemSounds.Beep.Play(); }

    /// <summary>Real-time alert triggered.</summary>
    public static void Alert() { if (_enabled) SystemSounds.Exclamation.Play(); }

    /// <summary>Play sound based on scan result.</summary>
    public static void ForScanResult(Status overall)
    {
        if (!_enabled) return;
        switch (overall)
        {
            case Status.Safe: AllClear(); break;
            case Status.Warning: WarningFound(); break;
            case Status.Danger: DangerFound(); break;
        }
    }
}
