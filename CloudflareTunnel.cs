using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace PCGuardian;

internal sealed class CloudflareTunnel : IDisposable
{
    private readonly object _lock = new();
    private Process? _process;
    private volatile string? _tunnelUrl;
    private bool _disposed;

    public string? TunnelUrl => _tunnelUrl;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is not null && !_process.HasExited;
            }
        }
    }

    public string? NotFoundReason { get; private set; }

    public event Action<string>? UrlAssigned;

    public bool Start(int localPort)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            if (_process is not null && !_process.HasExited) return true;

            var exePath = FindCloudflared();
            if (exePath is null) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"tunnel --url http://localhost:{localPort}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += OnDataReceived;
                proc.ErrorDataReceived += OnDataReceived;

                if (!proc.Start())
                {
                    proc.Dispose();
                    return false;
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                _process = proc;
                _tunnelUrl = null;
                NotFoundReason = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CloudflareTunnel] Failed to start: {ex.Message}");
                return false;
            }
        }
    }

    public void Stop()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _process;
            _process = null;
            _tunnelUrl = null;
        }

        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill();
                proc.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CloudflareTunnel] Stop error: {ex.Message}");
        }
        finally
        {
            proc.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static readonly HttpClient _http = new();
    private const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    /// <summary>Fires with progress text during download.</summary>
    public event Action<string>? StatusChanged;

    private string? FindCloudflared()
    {
        // 1. Same directory as the running exe
        var appDir = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        if (File.Exists(appDir)) return appDir;

        // 2. %APPDATA%\PCGuardian\cloudflared.exe (cached download location)
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cachedPath = Path.Combine(appData, "PCGuardian", "cloudflared.exe");
        if (File.Exists(cachedPath)) return cachedPath;

        // 3. On PATH
        try
        {
            var testPsi = new ProcessStartInfo
            {
                FileName = "cloudflared",
                Arguments = "version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var testProc = Process.Start(testPsi);
            if (testProc is not null)
            {
                testProc.WaitForExit(5000);
                return "cloudflared";
            }
        }
        catch { }

        // 4. Not found — auto-download to %APPDATA%\PCGuardian\
        try
        {
            StatusChanged?.Invoke("Downloading cloudflared (one-time, ~60 MB)...");
            Debug.WriteLine("[CloudflareTunnel] Auto-downloading cloudflared...");

            var dir = Path.Combine(appData, "PCGuardian");
            Directory.CreateDirectory(dir);

            var tmpPath = cachedPath + ".tmp";
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
            using (var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using var stream = response.Content.ReadAsStream();
                using var fileStream = File.Create(tmpPath);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    fileStream.Write(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                    if (totalRead % (5 * 1024 * 1024) == 0) // Update every ~5MB
                        StatusChanged?.Invoke($"Downloading cloudflared... {totalRead / (1024 * 1024)} MB");
                }
            }

            // Atomic rename
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
            File.Move(tmpPath, cachedPath);

            StatusChanged?.Invoke("Cloudflared ready.");
            Debug.WriteLine($"[CloudflareTunnel] Downloaded to {cachedPath}");
            return cachedPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CloudflareTunnel] Download failed: {ex.Message}");
            NotFoundReason = $"Could not download cloudflared: {ex.Message}";
            StatusChanged?.Invoke("Tunnel unavailable — cloudflared download failed.");
            return null;
        }
    }

    private void OnDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        Debug.WriteLine($"[CloudflareTunnel] {e.Data}");

        // Look for the trycloudflare.com URL in output
        var match = Regex.Match(e.Data, @"(https://[a-zA-Z0-9\-]+\.trycloudflare\.com)");
        if (!match.Success) return;

        var url = match.Groups[1].Value;
        _tunnelUrl = url;

        try
        {
            UrlAssigned?.Invoke(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CloudflareTunnel] UrlAssigned handler error: {ex.Message}");
        }
    }
}
