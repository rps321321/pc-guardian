using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;

namespace PCGuardian;

internal enum AlertSeverity { Info, Warning, Danger }

internal sealed record SecurityAlert(
    string Title,
    string Detail,
    AlertSeverity Severity,
    DateTime Timestamp);

internal sealed class RealTimeMonitor : IDisposable
{
    private static readonly string[] RemoteAccessPatterns =
    [
        "vnc", "anydesk", "teamviewer", "parsec", "rustdesk",
        "logmein", "splashtop", "screenconnect", "ammyy"
    ];

    public event Action<SecurityAlert>? OnAlert;

    private ManagementEventWatcher? _processWatcher;
    private ManagementEventWatcher? _usbWatcher;
    private System.Threading.Timer? _portTimer;
    private HashSet<int> _knownPorts = [];
    private volatile bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;

        StartProcessWatcher();
        StartUsbWatcher();
        StartPortWatcher();
    }

    public void Stop()
    {
        _running = false;
        StopWatcher(ref _processWatcher);
        StopWatcher(ref _usbWatcher);

        if (_portTimer is not null)
        {
            _portTimer.Dispose();
            _portTimer = null;
        }
    }

    public void Dispose() => Stop();

    // ── Process watcher ─────────────────────────────────────────

    private void StartProcessWatcher()
    {
        try
        {
            _processWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _processWatcher.EventArrived += OnProcessStarted;
            _processWatcher.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] Process watcher failed: {ex.Message}");
        }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var name = e.NewEvent["ProcessName"]?.ToString() ?? "";
            var pid = e.NewEvent["ProcessID"]?.ToString() ?? "?";
            var lower = name.ToLowerInvariant();

            foreach (var pattern in RemoteAccessPatterns)
            {
                if (!lower.Contains(pattern)) continue;

                FireAlert(
                    "Remote Access App Started",
                    $"{name} (PID {pid}) just started running",
                    AlertSeverity.Danger);
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] Process event error: {ex.Message}");
        }
    }

    // ── USB watcher ─────────────────────────────────────────────

    private void StartUsbWatcher()
    {
        try
        {
            var query = new WqlEventQuery(
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_USBControllerDevice'");
            _usbWatcher = new ManagementEventWatcher(query);
            _usbWatcher.EventArrived += OnUsbInserted;
            _usbWatcher.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] USB watcher failed: {ex.Message}");
        }
    }

    private void OnUsbInserted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            FireAlert(
                "USB Device Connected",
                "A new USB device was plugged in",
                AlertSeverity.Warning);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] USB event error: {ex.Message}");
        }
    }

    // ── Port watcher ────────────────────────────────────────────

    private void StartPortWatcher()
    {
        try
        {
            _knownPorts = GetCurrentListeningPorts();
            _portTimer = new System.Threading.Timer(
                _ => CheckForNewPorts(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] Port watcher failed: {ex.Message}");
        }
    }

    private void CheckForNewPorts()
    {
        if (!_running) return;

        try
        {
            var current = GetCurrentListeningPorts();
            var newPorts = current.Except(_knownPorts).ToList();

            foreach (var port in newPorts)
            {
                FireAlert(
                    "New Network Listener",
                    $"Port {port} started listening on a non-localhost address",
                    AlertSeverity.Warning);
            }

            _knownPorts = current;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] Port check error: {ex.Message}");
        }
    }

    private static HashSet<int> GetCurrentListeningPorts()
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        return props.GetActiveTcpListeners()
            .Where(ep => !IPAddress.IsLoopback(ep.Address))
            .Select(ep => ep.Port)
            .ToHashSet();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private void FireAlert(string title, string detail, AlertSeverity severity)
    {
        OnAlert?.Invoke(new SecurityAlert(title, detail, severity, DateTime.Now));
    }

    private static void StopWatcher(ref ManagementEventWatcher? watcher)
    {
        if (watcher is null) return;

        try
        {
            watcher.Stop();
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealTimeMonitor] Watcher dispose error: {ex.Message}");
        }

        watcher = null;
    }
}
