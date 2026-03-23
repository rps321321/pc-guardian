using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PCGuardian;

/// <summary>
/// Tiny built-in web server. IT person opens http://PC-IP:7777 in their browser
/// and sees the full scan report. One toggle, zero setup.
/// </summary>
internal sealed class ITServer : IDisposable
{
    HttpListener? _listener;
    Thread? _thread;
    volatile bool _running;
    int _shellActiveInt; // 0=false, 1=true; use Interlocked for atomic check-then-set
    volatile Report? _latestReport;
    string? _pin;
    int _port;
    PerformanceCounter? _cpuCounter;
    PerformanceCounter? _memCounter;

    public int Port => _port;
    public bool IsRunning => _running;
    public string? ActivePin { get; private set; }

    public string LocalUrl => $"http://{GetLocalIp()}:{_port}";

    public void Start(int port = 7777, string? pin = null)
    {
        if (_running) return;
        _port = port;
        if (string.IsNullOrWhiteSpace(pin))
        {
            // No PIN supplied — generate a random 6-digit PIN so the report
            // is never exposed to the LAN without authentication.
            _pin = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        }
        else
        {
            _pin = pin.Trim();
        }
        ActivePin = _pin;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // If http://+: fails (no admin), fall back to localhost + LAN IP
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                var ip = GetLocalIp();
                if (ip != "127.0.0.1")
                    _listener.Prefixes.Add($"http://{ip}:{port}/");
            }
            catch { }

            try { _listener.Start(); }
            catch
            {
                // Last resort: localhost only
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }
        }

        _running = true;

        // Auto-open firewall port so IT can connect without manual setup
        OpenFirewallPort(port);

        // Initialize PerformanceCounters once and prime them
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // prime
            _memCounter = new PerformanceCounter("Memory", "Available MBytes");
            _memCounter.NextValue(); // prime
        }
        catch { }

        _thread = new Thread(Listen) { IsBackground = true, Name = "ITServer" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cpuCounter?.Dispose();
        _cpuCounter = null;
        _memCounter?.Dispose();
        _memCounter = null;
    }

    static void OpenFirewallPort(int port)
    {
        try
        {
            var ruleName = $"PCGuardian_IT_{port}";
            // Check if rule already exists
            var checkPsi = new ProcessStartInfo("netsh",
                $"advfirewall firewall show rule name=\"{ruleName}\"")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var check = Process.Start(checkPsi);
            var output = check?.StandardOutput.ReadToEnd() ?? "";
            check?.WaitForExit(5000);
            if (output.Contains(ruleName)) return; // Already exists

            // Create inbound rule
            var psi = new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=tcp localport={port}")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { /* Not admin or netsh unavailable — server still works on localhost */ }
    }

    public void UpdateReport(Report report)
    {
        _latestReport = report;
    }

    // ── Request loop ──────────────────────────────────────────

    void Listen()
    {
        while (_running)
        {
            try
            {
                var ctx = _listener?.GetContext();
                if (ctx == null) break;

                // WebSocket upgrade for /shell endpoint
                if (ctx.Request.IsWebSocketRequest
                    && ctx.Request.Url?.AbsolutePath?.TrimEnd('/').Equals("/shell", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _ = Task.Run(() => HandleWebSocket(ctx));
                }
                else
                {
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    async Task HandleWebSocket(HttpListenerContext ctx)
    {
        // PIN check
        var pin = ctx.Request.QueryString["pin"];
        if (_pin != null && !PinMatches(pin, _pin))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }

        // Full access always enabled

        // Bug 7: Guard against multiple simultaneous shells (atomic check-then-set)
        if (Interlocked.CompareExchange(ref _shellActiveInt, 1, 0) != 0)
        {
            var wsReject = await ctx.AcceptWebSocketAsync(null);
            await wsReject.WebSocket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                "Another session is active", CancellationToken.None);
            wsReject.WebSocket.Dispose();
            return;
        }

        System.Net.WebSockets.WebSocket? ws = null;
        Process? shell = null;
        using var cts = new CancellationTokenSource();
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            ws = wsCtx.WebSocket;

            IsITConnected = true;
            ITConnectionChanged?.Invoke(true);

            // Start PowerShell process
            shell = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"[Console]::OutputEncoding = [Text.Encoding]::UTF8\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true
            };
            shell.Exited += (_, _) => { try { cts.Cancel(); } catch { } };
            shell.Start();

            var token = cts.Token;

            // Read stdout → WebSocket
            var readStdout = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try
                {
                    while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        int n = await shell.StandardOutput.BaseStream.ReadAsync(buf, 0, buf.Length, token);
                        if (n == 0) break;
                        await ws.SendAsync(new ArraySegment<byte>(buf, 0, n),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch { }
            });

            // Read stderr → WebSocket
            var readStderr = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try
                {
                    while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        int n = await shell.StandardError.BaseStream.ReadAsync(buf, 0, buf.Length, token);
                        if (n == 0) break;
                        await ws.SendAsync(new ArraySegment<byte>(buf, 0, n),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch { }
            });

            // WebSocket → PowerShell stdin
            var recvBuf = new byte[4096];
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    break;
                var cmd = Encoding.UTF8.GetString(recvBuf, 0, result.Count);
                await shell.StandardInput.WriteAsync(cmd);
                await shell.StandardInput.FlushAsync();
            }

            await Task.WhenAny(readStdout, readStderr);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ITServer] WebSocket shell error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _shellActiveInt, 0);
            IsITConnected = false;
            ITConnectionChanged?.Invoke(false);

            if (shell is { HasExited: false })
            {
                try { shell.Kill(); } catch { }
            }
            shell?.Dispose();

            if (ws?.State == System.Net.WebSockets.WebSocketState.Open)
            {
                try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
            }
            ws?.Dispose();
        }
    }

    /// <summary>Company name shown in the terminal page header.</summary>
    internal string CompanyName { get; set; } = "PC Guardian";

    // Full access is always enabled — no trust level gating

    void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";

            // ── /terminal — self-contained IT terminal page (PIN handled client-side) ──
            if (path.Equals("/terminal", StringComparison.OrdinalIgnoreCase))
            {
                var html = GenerateTerminalPage(CompanyName);
                WriteResponse(resp, html, 200);
                return;
            }

            // ── /api/metrics — JSON system metrics (PIN-gated) ──
            if (path.Equals("/api/metrics", StringComparison.OrdinalIgnoreCase))
            {
                if (_pin != null && !PinMatches(req.QueryString["pin"], _pin))
                {
                    WriteJsonResponse(resp, "{\"error\":\"unauthorized\"}", 401);
                    return;
                }
                var json = BuildMetricsJson();
                WriteJsonResponse(resp, json, 200);
                return;
            }

            // ── /api/action — execute quick actions (PIN-gated) ──
            if (path.Equals("/api/action", StringComparison.OrdinalIgnoreCase))
            {
                if (_pin != null && !PinMatches(req.QueryString["pin"], _pin))
                {
                    WriteJsonResponse(resp, "{\"error\":\"unauthorized\"}", 401);
                    return;
                }
                var action = req.QueryString["action"] ?? "";
                var result = ExecuteAction(action, req.QueryString["name"]);
                WriteJsonResponse(resp, result, 200);
                return;
            }

            // ── Default: existing report dashboard (PIN-gated server-side) ──
            if (_pin != null)
            {
                var query = req.QueryString["pin"];
                if (!PinMatches(query, _pin))
                {
                    var pinHtml = GeneratePinPage();
                    WriteResponse(resp, pinHtml, 200);
                    return;
                }
            }

            if (_latestReport != null)
            {
                var html = ReportGenerator.ToHtml(_latestReport);

                var headerTag = "<div class=\"header\">";
                int headerIdx = html.IndexOf(headerTag, StringComparison.Ordinal);
                if (headerIdx >= 0)
                {
                    var banner =
                        "<div class=\"it-banner\">" +
                        "\uD83D\uDD12 Shared by PC Guardian &middot; " +
                        $"Live from {WebUtility.HtmlEncode(Environment.MachineName)} &middot; " +
                        $"Scanned {_latestReport.Timestamp:h:mm tt}" +
                        "</div>";
                    html = html.Insert(headerIdx, banner);
                }

                var styleClose = "</style>";
                int styleIdx = html.IndexOf(styleClose, StringComparison.Ordinal);
                if (styleIdx >= 0)
                {
                    var bannerCss =
                        ".it-banner { background: #1e40af; color: white; padding: 10px 20px; " +
                        "font-size: 13px; border-radius: 8px; margin-bottom: 16px; }";
                    html = html.Insert(styleIdx, bannerCss);
                }

                WriteResponse(resp, html, 200);
            }
            else
            {
                var waitHtml = GenerateWaitingPage();
                WriteResponse(resp, waitHtml, 200);
            }
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }

    static void WriteResponse(HttpListenerResponse resp, string html, int code)
    {
        resp.StatusCode = code;
        resp.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
        resp.Close();
    }

    static void WriteJsonResponse(HttpListenerResponse resp, string json, int code)
    {
        resp.StatusCode = code;
        resp.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
        resp.Close();
    }

    // ── API helpers ────────────────────────────────────────────

    string BuildMetricsJson()
    {
        try
        {
            var cpuPercent = _cpuCounter != null ? (int)_cpuCounter.NextValue() : -1;

            var memInfo = GC.GetGCMemoryInfo();
            var totalMemMb = memInfo.TotalAvailableMemoryBytes / (1024 * 1024);
            var availMb = (long)(_memCounter?.NextValue() ?? 0);
            var usedMemMb = totalMemMb - availMb;
            var ramPercent = totalMemMb > 0 ? (int)(usedMemMb * 100 / totalMemMb) : 0;

            // Uptime
            var uptimeTs = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var uptimeStr = uptimeTs.Days > 0
                ? $"{uptimeTs.Days} day{(uptimeTs.Days != 1 ? "s" : "")} {uptimeTs.Hours} hour{(uptimeTs.Hours != 1 ? "s" : "")}"
                : $"{uptimeTs.Hours} hour{(uptimeTs.Hours != 1 ? "s" : "")} {uptimeTs.Minutes} min";

            // GPU placeholder
            var gpuStr = "N/A";
            try
            {
                using var gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", "_Total");
                gpuStr = ((int)gpuCounter.NextValue()) + "%";
            }
            catch { gpuStr = "N/A"; }

            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => new
                {
                    name = d.Name,
                    totalGb = d.TotalSize / (1024L * 1024 * 1024),
                    freeGb = d.AvailableFreeSpace / (1024L * 1024 * 1024),
                    usedGb = (d.TotalSize - d.AvailableFreeSpace) / (1024L * 1024 * 1024),
                    percentUsed = (int)((d.TotalSize - d.AvailableFreeSpace) * 100 / d.TotalSize)
                });

            // Build full scan data if available
            object? scanObj = null;
            if (_latestReport != null)
            {
                var cats = _latestReport.Categories.Select(c => new
                {
                    id = c.Id,
                    title = c.Title,
                    icon = c.Icon,
                    status = c.Status.ToString(),
                    summary = c.Summary,
                    findings = c.Findings.Select(f => new
                    {
                        label = f.Label,
                        detail = f.Detail,
                        status = f.Status.ToString()
                    }),
                    tip = c.Tip
                });
                scanObj = new
                {
                    status = "completed",
                    timestamp = _latestReport.Timestamp.ToString("o"),
                    overall = _latestReport.Overall.ToString(),
                    safeCount = _latestReport.SafeCount,
                    warningCount = _latestReport.WarningCount,
                    dangerCount = _latestReport.DangerCount,
                    categories = cats
                };
            }

            var metrics = new
            {
                cpu = cpuPercent,
                ramTotalMb = totalMemMb,
                ramUsedMb = usedMemMb,
                ramPercent,
                gpu = gpuStr,
                hostname = Environment.MachineName,
                uptime = uptimeStr,
                disks = drives,
                scan = scanObj
            };

            return JsonSerializer.Serialize(metrics);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { cpu = -1, ramTotalMb = 0, ramUsedMb = 0, ramPercent = 0, gpu = "N/A", error = ex.Message, hostname = Environment.MachineName, uptime = "unknown" });
        }
    }

    /// <summary>Fired when a remote IT user requests a scan via the API.</summary>
    public event Action? ScanRequested;

    string ExecuteAction(string action, string? queryName = null)
    {
        try
        {
            var result = action.ToLowerInvariant() switch
            {
                "scan" => InvokeScan(),
                "fixdns" => RunShellAction("ipconfig /flushdns"),
                "firewall" => RunShellAction("netsh advfirewall set allprofiles state on"),
                "blockrdp" => RunShellAction("reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v fDenyTSConnections /t REG_DWORD /d 1 /f"),
                "resetnetwork" => RunShellAction("netsh winsock reset && netsh int ip reset && ipconfig /flushdns && ipconfig /release && ipconfig /renew"),
                "killprocess" => KillProcessByName(queryName ?? ""),
                _ => new { ok = false, message = $"Unknown action: {action}" }
            };
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, message = ex.Message });
        }
    }

    static object KillProcessByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new { ok = false, message = "No process name provided." };
        try
        {
            var procs = Process.GetProcessesByName(name.Replace(".exe", ""));
            if (procs.Length == 0)
                return new { ok = false, message = $"No process found with name '{name}'." };
            int killed = 0;
            foreach (var p in procs)
            {
                try { p.Kill(); killed++; } catch { }
                p.Dispose();
            }
            return new { ok = true, message = $"Killed {killed} instance(s) of '{name}'." };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    object InvokeScan()
    {
        ScanRequested?.Invoke();
        return new { ok = true, message = "Scan initiated." };
    }

    static object RunShellAction(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            var stderrTask = proc?.StandardError.ReadToEndAsync();
            var stdoutTask = proc?.StandardOutput.ReadToEndAsync();
            proc?.WaitForExit(5000);
            var output = stdoutTask?.Result ?? "";
            var error = stderrTask?.Result ?? "";
            return new { ok = true, message = string.IsNullOrWhiteSpace(output) ? "Done." : output.Trim() };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    // ── Page generators ───────────────────────────────────────

    static string GeneratePinPage() => """
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>PC Guardian — Enter PIN</title>
        <style>
            body { font-family: 'Segoe UI', sans-serif; background: #fafafa; display: flex;
                   justify-content: center; align-items: center; min-height: 100vh; }
            .box { text-align: center; background: white; padding: 40px 48px; border-radius: 16px;
                   box-shadow: 0 4px 24px rgba(0,0,0,0.08); }
            h2 { margin-bottom: 8px; }
            p { color: #666; font-size: 14px; margin-bottom: 20px; }
            input { font-size: 24px; width: 160px; text-align: center; padding: 8px;
                    border: 2px solid #ddd; border-radius: 8px; letter-spacing: 8px; }
            input:focus { outline: none; border-color: #6366f1; }
            button { margin-top: 16px; padding: 10px 32px; background: #6366f1; color: white;
                     border: none; border-radius: 8px; font-size: 15px; cursor: pointer; }
            button:hover { background: #5558e6; }
        </style></head><body>
        <div class="box">
            <h2>🛡️ PC Guardian</h2>
            <p>Enter the PIN to view this PC's security report.</p>
            <form method="get"><input type="text" name="pin" maxlength="6" autofocus
                pattern="[0-9]*" inputmode="numeric" placeholder="····">
            <br><button type="submit">View Report</button></form>
        </div></body></html>
        """;

    static string GenerateWaitingPage() =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
        "<title>PC Guardian</title><meta http-equiv=\"refresh\" content=\"5\">" +
        "<style>body{font-family:'Segoe UI',sans-serif;background:#fafafa;display:flex;" +
        "justify-content:center;align-items:center;min-height:100vh}" +
        ".box{text-align:center}h2{margin-bottom:8px}p{color:#666;font-size:14px}</style>" +
        "</head><body><div class=\"box\">" +
        "<h2>\uD83D\uDEE1\uFE0F PC Guardian</h2>" +
        "<p>Waiting for a scan to complete on " + WebUtility.HtmlEncode(Environment.MachineName) + "...</p>" +
        "<p style=\"color:#999;font-size:12px\">This page refreshes automatically.</p>" +
        "</div></body></html>";

    // ── IT Terminal Page ───────────────────────────────────────

    static string GenerateTerminalPage(string company)
    {
        var safeCompany = WebUtility.HtmlEncode(company);
        const bool showTerminal = true; // Always full access
        var sb = new StringBuilder(32000);
        sb.Append(@"<!DOCTYPE html><html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>"); sb.Append(safeCompany); sb.Append(@" - Remote Support</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
:root{--bg:#09090b;--card:#18181b;--border:#27272a;--text:#fafafa;--muted:#a1a1aa;--accent:#6366f1;--accent-hover:#818cf8;--safe:#10b981;--warn:#f59e0b;--danger:#ef4444;--safe-bg:rgba(16,185,129,0.1);--warn-bg:rgba(245,158,11,0.1);--danger-bg:rgba(239,68,68,0.1)}
body{font-family:'Segoe UI',system-ui,-apple-system,sans-serif;background:var(--bg);color:var(--text);height:100vh;display:flex;flex-direction:column;overflow:hidden}
a{color:var(--accent)}
/* PIN screen */
.pin-screen{display:flex;justify-content:center;align-items:center;height:100vh;background:var(--bg)}
.pin-box{text-align:center;background:var(--card);padding:48px 56px;border-radius:20px;border:1px solid var(--border);max-width:420px;width:90%}
.pin-box .logo{font-size:40px;margin-bottom:16px}
.pin-box h2{margin-bottom:8px;font-size:22px;font-weight:600}
.pin-box p{color:var(--muted);margin-bottom:28px;font-size:14px;line-height:1.5}
.pin-box input{font-size:28px;width:220px;text-align:center;padding:14px;background:var(--bg);border:2px solid var(--border);border-radius:10px;color:var(--text);letter-spacing:8px;transition:border-color .2s}
.pin-box input:focus{outline:none;border-color:var(--accent)}
.pin-box button{margin-top:24px;padding:14px 48px;background:var(--accent);color:white;border:none;border-radius:10px;font-size:15px;font-weight:600;cursor:pointer;transition:background .2s}
.pin-box button:hover{background:var(--accent-hover)}
.pin-box .error{color:var(--danger);margin-top:14px;font-size:13px;display:none}
/* Header */
header{background:var(--card);padding:14px 24px;display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid var(--border);flex-shrink:0}
header .left{display:flex;align-items:center;gap:12px}
header .left .shield{font-size:22px}
header h1{font-size:17px;font-weight:600;letter-spacing:-0.3px}
header .right{display:flex;align-items:center;gap:10px;font-size:13px;color:var(--muted)}
.dot{width:8px;height:8px;border-radius:50%;flex-shrink:0}
.dot.on{background:var(--safe);box-shadow:0 0 8px rgba(16,185,129,0.5)}
.dot.off{background:var(--danger)}
/* Tabs */
.tabs{display:flex;gap:0;background:var(--card);border-bottom:1px solid var(--border);flex-shrink:0;overflow-x:auto}
.tab{padding:12px 28px;cursor:pointer;color:var(--muted);font-size:13px;font-weight:500;border-bottom:2px solid transparent;white-space:nowrap;transition:all .15s;user-select:none}
.tab:hover{color:var(--text)}
.tab.active{color:var(--accent);border-color:var(--accent)}
/* Content */
.content{flex:1;overflow-y:auto;padding:24px;min-height:0}
/* Dashboard metric cards */
.metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin-bottom:24px}
@media(max-width:900px){.metrics{grid-template-columns:repeat(2,1fr)}}
@media(max-width:520px){.metrics{grid-template-columns:1fr}}
.mc{background:var(--card);border:1px solid var(--border);border-radius:12px;padding:20px;transition:border-color .2s}
.mc:hover{border-color:#3f3f46}
.mc .l{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:1.2px;font-weight:500}
.mc .v{font-size:30px;font-weight:700;margin-top:6px;letter-spacing:-1px}
.mc .sub{font-size:12px;color:var(--muted);margin-top:4px}
.safe-text{color:var(--safe)}.warn-text{color:var(--warn)}.danger-text{color:var(--danger)}
/* Sections */
.section-title{font-size:14px;font-weight:600;color:var(--muted);text-transform:uppercase;letter-spacing:1px;margin-bottom:16px;margin-top:8px}
/* Disk bars */
.disk-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:12px;margin-bottom:28px}
.disk-item{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:16px}
.disk-item .name{font-size:13px;font-weight:600;margin-bottom:8px}
.disk-item .bar-track{height:8px;background:#27272a;border-radius:4px;overflow:hidden}
.disk-item .bar-fill{height:100%;border-radius:4px;transition:width .5s}
.disk-item .info{display:flex;justify-content:space-between;font-size:11px;color:var(--muted);margin-top:6px}
/* Action buttons */
.actions{display:flex;flex-wrap:wrap;gap:10px;margin-bottom:28px}
.actions button{padding:10px 18px;background:var(--card);border:1px solid var(--border);border-radius:8px;color:var(--text);cursor:pointer;font-size:12px;font-weight:500;transition:all .15s;display:flex;align-items:center;gap:6px}
.actions button:hover{background:#27272a;border-color:#3f3f46}
.actions .pri{background:var(--accent);border-color:var(--accent);font-weight:600}
.actions .pri:hover{background:var(--accent-hover);border-color:var(--accent-hover)}
.actions .red{border-color:rgba(239,68,68,0.3);color:var(--danger)}
.actions .red:hover{background:rgba(239,68,68,0.1)}
.kill-group{display:flex;gap:0}
.kill-group input{padding:10px 12px;background:var(--card);border:1px solid var(--border);border-right:none;border-radius:8px 0 0 8px;color:var(--text);font-size:12px;width:140px}
.kill-group input:focus{outline:none;border-color:var(--accent)}
.kill-group button{border-radius:0 8px 8px 0 !important}
/* Security posture */
.posture{display:flex;gap:16px;margin-bottom:28px;flex-wrap:wrap}
.posture .pill{display:flex;align-items:center;gap:8px;padding:10px 18px;border-radius:10px;font-size:13px;font-weight:600}
.posture .pill.s{background:var(--safe-bg);color:var(--safe)}
.posture .pill.w{background:var(--warn-bg);color:var(--warn)}
.posture .pill.d{background:var(--danger-bg);color:var(--danger)}
/* Scan results */
.scan-cat{background:var(--card);border:1px solid var(--border);border-radius:12px;margin-bottom:14px;overflow:hidden}
.scan-cat-hdr{padding:16px 20px;display:flex;align-items:center;gap:12px;cursor:pointer;user-select:none}
.scan-cat-hdr .icon{font-size:20px}
.scan-cat-hdr .title{font-weight:600;font-size:14px;flex:1}
.scan-cat-hdr .badge{padding:3px 10px;border-radius:20px;font-size:11px;font-weight:600;text-transform:uppercase}
.badge-Safe{background:var(--safe-bg);color:var(--safe)}.badge-Warning{background:var(--warn-bg);color:var(--warn)}.badge-Danger{background:var(--danger-bg);color:var(--danger)}
.scan-cat-body{padding:0 20px 16px;display:none}
.scan-cat.open .scan-cat-body{display:block}
.finding{display:flex;justify-content:space-between;align-items:center;padding:8px 0;border-bottom:1px solid var(--border);font-size:13px}
.finding:last-child{border-bottom:none}
.finding .fd{color:var(--muted)}
.tip{margin-top:10px;padding:10px 14px;background:rgba(99,102,241,0.06);border-radius:8px;font-size:12px;color:var(--muted);line-height:1.5;border-left:3px solid var(--accent)}
.scan-empty{text-align:center;padding:60px 20px;color:var(--muted)}
.scan-empty .big{font-size:48px;margin-bottom:16px}
/* Terminal */
.term-wrap{display:flex;flex-direction:column;height:100%}
#tout{flex:1;background:var(--bg);font-family:'Cascadia Code','Fira Code',Consolas,'Courier New',monospace;font-size:13px;padding:16px;overflow-y:auto;white-space:pre-wrap;word-break:break-all;line-height:1.7;border:1px solid var(--border);border-radius:10px;color:#d4d4d8;min-height:200px}
#tout .err{color:var(--danger)}
#trow{display:flex;align-items:center;padding:12px 0;gap:8px;flex-shrink:0}
#trow .prompt{color:var(--accent);font-family:monospace;font-weight:700;font-size:13px;white-space:nowrap}
#tinp{flex:1;background:var(--card);border:1px solid var(--border);border-radius:8px;color:var(--text);font-family:'Cascadia Code',Consolas,monospace;font-size:13px;padding:10px 14px;transition:border-color .2s}
#tinp:focus{outline:none;border-color:var(--accent)}
/* Session log */
#slog{font-family:'Cascadia Code',Consolas,monospace;font-size:12px;line-height:2}
#slog .e{padding:4px 0;border-bottom:1px solid rgba(39,39,42,0.5)}
#slog .ts{color:#52525b;margin-right:12px}
#slog .act{color:var(--accent)}
#slog .res{color:var(--safe)}
#slog .err-log{color:var(--danger)}
.log-empty{text-align:center;padding:60px 20px;color:var(--muted)}
/* Toast */
.toast-container{position:fixed;bottom:24px;right:24px;z-index:9999;display:flex;flex-direction:column;gap:8px}
.toast{padding:12px 20px;background:var(--card);border:1px solid var(--border);border-radius:10px;font-size:13px;box-shadow:0 8px 30px rgba(0,0,0,0.4);animation:slideIn .3s ease;max-width:360px}
.toast.success{border-left:3px solid var(--safe)}
.toast.error{border-left:3px solid var(--danger)}
@keyframes slideIn{from{transform:translateX(100px);opacity:0}to{transform:translateX(0);opacity:1}}
/* View containers */
.view{display:none}.view.active{display:block}.view.active-flex{display:flex;flex-direction:column}
</style></head><body>

<!-- PIN Screen -->
<div class=""pin-screen"" id=""ps"">
<div class=""pin-box"">
<div class=""logo"">&#x1f6e1;&#xfe0f;</div>
<h2>"); sb.Append(safeCompany); sb.Append(@"</h2>
<p>Enter the PIN displayed on the client machine to access remote support.</p>
<input type=""text"" id=""pi"" maxlength=""10"" autofocus placeholder=""PIN"" autocomplete=""off"">
<br><button onclick=""ck()"">Connect</button>
<div class=""error"" id=""pe"">Invalid PIN. Check the PIN on the client machine.</div>
</div></div>

<!-- Main App -->
<div id=""app"" style=""display:none;flex-direction:column;height:100vh"">
<header>
<div class=""left"">
<span class=""shield"">&#x1f6e1;&#xfe0f;</span>
<h1>"); sb.Append(safeCompany); sb.Append(@" &mdash; Remote Support</h1>
</div>
<div class=""right"">
<div class=""dot on"" id=""cd""></div>
<span id=""ct"">Connected</span>
</div>
</header>

<div class=""tabs"">
<div class=""tab active"" onclick=""st('d')"" id=""tab-d"">Dashboard</div>
<div class=""tab"" onclick=""st('s')"" id=""tab-s"">Scan Results</div>");
        if (showTerminal)
            sb.Append(@"<div class=""tab"" onclick=""st('t')"" id=""tab-t"">Terminal</div>");
        sb.Append(@"
<div class=""tab"" onclick=""st('l')"" id=""tab-l"">Session Log</div>
</div>

<div class=""content"" id=""co"">

<!-- Dashboard View -->
<div class=""view active"" id=""v-d"">
<div class=""metrics"" id=""mt""></div>
<div class=""section-title"">Disk Space</div>
<div class=""disk-grid"" id=""disks""></div>
<div class=""section-title"">Quick Actions</div>
<div class=""actions"">
<button class=""pri"" onclick=""da('scan')"">&#x1f50d; Run Scan</button>
<button onclick=""da('fixdns')"">&#x1f310; Fix DNS</button>
<button onclick=""da('firewall')"">&#x1f6e1;&#xfe0f; Enable Firewall</button>
<button class=""red"" onclick=""da('blockrdp')"">&#x1f6ab; Block RDP</button>
<button onclick=""da('resetnetwork')"">&#x1f504; Reset Network</button>
<div class=""kill-group"">
<input type=""text"" id=""kpname"" placeholder=""process name..."">
<button class=""red"" onclick=""da('killprocess',document.getElementById('kpname').value)"">Kill Process</button>
</div>
</div>
<div class=""section-title"">Security Posture</div>
<div class=""posture"" id=""posture"">
<div class=""pill s"" id=""p-safe"">&#x2714; 0 Passed</div>
<div class=""pill w"" id=""p-warn"">&#x26a0;&#xfe0f; 0 Warnings</div>
<div class=""pill d"" id=""p-danger"">&#x2716; 0 Danger</div>
</div>
</div>

<!-- Scan Results View -->
<div class=""view"" id=""v-s"">
<div id=""scan-list""><div class=""scan-empty""><div class=""big"">&#x1f50d;</div><p>No scan results yet. Run a scan from the Dashboard.</p></div></div>
</div>

<!-- Terminal View -->");
        if (showTerminal)
        {
            sb.Append(@"
<div class=""view"" id=""v-t"" style=""height:100%"">
<div class=""term-wrap"">
<div id=""tout""></div>
<div id=""trow""><span class=""prompt"">PS &gt;</span><input type=""text"" id=""tinp"" placeholder=""Type a PowerShell command..."" autocomplete=""off""></div>
</div>
</div>");
        }
        sb.Append(@"
<!-- Session Log View -->
<div class=""view"" id=""v-l"">
<div id=""slog""><div class=""log-empty"">Session log will appear here once you start taking actions.</div></div>
</div>

</div><!-- /content -->
</div><!-- /app -->

<div class=""toast-container"" id=""toasts""></div>

<script>
let pn='',ws=null,cmdHistory=[],histIdx=-1,metricsData=null;
const $=id=>document.getElementById(id);

function toast(msg,type='success'){
  var c=$('toasts'),d=document.createElement('div');
  d.className='toast '+type;d.textContent=msg;c.appendChild(d);
  setTimeout(()=>{d.style.opacity='0';d.style.transition='opacity .3s';setTimeout(()=>d.remove(),300)},4000);
}

function ck(){
  pn=$('pi').value;
  fetch('/api/metrics?pin='+encodeURIComponent(pn)).then(r=>{
    if(r.ok){
      $('ps').style.display='none';
      $('app').style.display='flex';
      sessionStorage.setItem('pin',pn);
      startPolling();
      logEntry('Connected to '+location.hostname,'act');
      toast('Connected successfully');");
        if (showTerminal)
            sb.Append("connectWS();");
        sb.Append(@"
    }else{$('pe').style.display='block'}
  }).catch(()=>$('pe').style.display='block');
}

function st(t){
  document.querySelectorAll('.tab').forEach(e=>e.classList.remove('active'));
  var tabEl=$('tab-'+t);if(tabEl)tabEl.classList.add('active');
  ['d','s','t','l'].forEach(v=>{
    var el=$('v-'+v);if(!el)return;
    el.classList.remove('active','active-flex');
    if(v===t){el.classList.add(v==='t'?'active-flex':'active')}
  });
}

function startPolling(){refreshMetrics();setInterval(refreshMetrics,3000)}

function refreshMetrics(){
  fetch('/api/metrics?pin='+encodeURIComponent(pn)).then(r=>r.json()).then(d=>{
    metricsData=d;
    renderCards(d);
    renderDisks(d.disks||[]);
    renderPosture(d.scan);
    renderScan(d.scan);
  }).catch(()=>{});
}

function statusClass(v,lo,hi){return v<lo?'safe-text':v<hi?'warn-text':'danger-text'}

function renderCards(d){
  var m=$('mt');m.innerHTML='';
  var rp=d.ramPercent||0;
  var cards=[
    {l:'CPU Usage',v:d.cpu+'%',s:statusClass(d.cpu,50,80),sub:'Processor utilization'},
    {l:'RAM Usage',v:rp+'%',s:statusClass(rp,60,85),sub:Math.round(d.ramUsedMb/1024)+' / '+Math.round(d.ramTotalMb/1024)+' GB'},
    {l:'Hostname',v:d.hostname||'N/A',s:'',sub:'Machine name',small:true},
    {l:'Uptime',v:d.uptime||'N/A',s:'',sub:'System uptime',small:true}
  ];
  cards.forEach(c=>{
    var div=document.createElement('div');div.className='mc';
    div.innerHTML='<div class=""l"">'+esc(c.l)+'</div><div class=""v'+(c.s?' '+c.s:'')+'""'+(c.small?' style=""font-size:16px""':'')+'>'+esc(c.v)+'</div><div class=""sub"">'+esc(c.sub)+'</div>';
    m.appendChild(div);
  });
}

function renderDisks(disks){
  var c=$('disks');c.innerHTML='';
  disks.forEach(dk=>{
    var pct=dk.percentUsed||0;
    var col=pct<70?'var(--safe)':pct<85?'var(--warn)':'var(--danger)';
    var d=document.createElement('div');d.className='disk-item';
    d.innerHTML='<div class=""name"">'+esc(dk.name)+'</div><div class=""bar-track""><div class=""bar-fill"" style=""width:'+pct+'%;background:'+col+'""></div></div><div class=""info""><span>'+dk.usedGb+' GB used</span><span>'+dk.freeGb+' GB free / '+dk.totalGb+' GB</span></div>';
    c.appendChild(d);
  });
}

function renderPosture(scan){
  if(!scan||scan.status!=='completed'){
    $('p-safe').innerHTML='&#x2714; -- Passed';$('p-warn').innerHTML='&#x26a0;&#xfe0f; -- Warnings';$('p-danger').innerHTML='&#x2716; -- Danger';return;
  }
  $('p-safe').innerHTML='&#x2714; '+scan.safeCount+' Passed';
  $('p-warn').innerHTML='&#x26a0;&#xfe0f; '+scan.warningCount+' Warning'+(scan.warningCount!==1?'s':'');
  $('p-danger').innerHTML='&#x2716; '+scan.dangerCount+' Danger';
}

function renderScan(scan){
  var c=$('scan-list');
  if(!scan||scan.status!=='completed'||!scan.categories){
    c.innerHTML='<div class=""scan-empty""><div class=""big"">&#x1f50d;</div><p>No scan results yet. Run a scan from the Dashboard.</p></div>';return;
  }
  var html='<div style=""margin-bottom:16px;font-size:13px;color:var(--muted)"">Scanned '+new Date(scan.timestamp).toLocaleString()+' &mdash; Overall: <strong class=""'+(scan.overall==='Safe'?'safe-text':scan.overall==='Warning'?'warn-text':'danger-text')+'"">'+ esc(scan.overall)+'</strong></div>';
  scan.categories.forEach(cat=>{
    html+='<div class=""scan-cat"" onclick=""this.classList.toggle(\'open\')"">';
    html+='<div class=""scan-cat-hdr""><span class=""icon"">'+cat.icon+'</span><span class=""title"">'+ esc(cat.title)+'</span><span class=""badge badge-'+cat.status+'"">'+esc(cat.status)+'</span></div>';
    html+='<div class=""scan-cat-body"">';
    if(cat.findings&&cat.findings.length){
      cat.findings.forEach(f=>{
        html+='<div class=""finding""><span>'+esc(f.label)+'</span><span class=""fd '+(f.status==='Safe'?'safe-text':f.status==='Warning'?'warn-text':'danger-text')+'"">'+ esc(f.detail)+'</span></div>';
      });
    }
    if(cat.tip)html+='<div class=""tip"">'+esc(cat.tip)+'</div>';
    html+='</div></div>';
  });
  c.innerHTML=html;
}

function da(action,name){
  var url='/api/action?pin='+encodeURIComponent(pn)+'&action='+encodeURIComponent(action);
  if(name)url+='&name='+encodeURIComponent(name);
  logEntry('Action: '+action+(name?' ('+name+')':''),'act');
  toast('Running: '+action,'success');
  fetch(url).then(r=>r.json()).then(d=>{
    var msg=d.message||JSON.stringify(d);
    logEntry('Result: '+msg,d.ok?'res':'err-log');
    toast(msg,d.ok?'success':'error');
    if(action==='scan')setTimeout(refreshMetrics,3000);
  }).catch(e=>{logEntry('Error: '+e,'err-log');toast(''+e,'error')});
}");
        if (showTerminal)
        {
            sb.Append(@"
function connectWS(){
  var p=location.protocol==='https:'?'wss:':'ws:';
  ws=new WebSocket(p+'//'+location.host+'/shell?pin='+encodeURIComponent(pn));
  ws.onopen=function(){logEntry('Shell connected','act');$('cd').className='dot on';$('ct').textContent='Connected'};
  ws.onmessage=function(e){var o=$('tout');o.textContent+=e.data;o.scrollTop=o.scrollHeight};
  ws.onclose=function(){logEntry('Shell disconnected','err-log');$('cd').className='dot off';$('ct').textContent='Reconnecting...';setTimeout(connectWS,3000)};
  ws.onerror=function(){};
}
function sendCmd(){
  var i=$('tinp');if(!i.value.trim()||!ws||ws.readyState!==1)return;
  var cmd=i.value;cmdHistory.unshift(cmd);histIdx=-1;
  logEntry('$ '+cmd,'act');ws.send(cmd+'\n');i.value='';
}");
        }
        sb.Append(@"
function logEntry(msg,cls){
  var log=$('slog');
  if(log.querySelector('.log-empty'))log.innerHTML='';
  var d=document.createElement('div');d.className='e';
  var ts=document.createElement('span');ts.className='ts';ts.textContent=new Date().toLocaleTimeString();
  var sp=document.createElement('span');sp.className=cls||'';sp.textContent=msg;
  d.appendChild(ts);d.appendChild(sp);log.appendChild(d);
  log.scrollTop=log.scrollHeight;
}

function esc(s){if(!s)return'';var d=document.createElement('div');d.textContent=s;return d.innerHTML}

window.onload=function(){
  var s=sessionStorage.getItem('pin');
  if(s){$('pi').value=s;ck()}
  var inp=$('tinp');
  if(inp){
    inp.addEventListener('keydown',function(e){
      if(e.key==='Enter'){"); if (showTerminal) sb.Append("sendCmd();"); sb.Append(@"return}
      if(e.key==='ArrowUp'&&cmdHistory.length){e.preventDefault();histIdx=Math.min(histIdx+1,cmdHistory.length-1);e.target.value=cmdHistory[histIdx]}
      if(e.key==='ArrowDown'){e.preventDefault();histIdx=Math.max(histIdx-1,-1);e.target.value=histIdx>=0?cmdHistory[histIdx]:''}
    });
  }
  $('pi').addEventListener('keydown',function(e){if(e.key==='Enter')ck()});
};
</script></body></html>");
        return sb.ToString();
    }

    // ── WebSocket Shell ─────────────────────────────────────────

    /// <summary>Whether an IT person is currently connected.</summary>
    public bool IsITConnected { get; private set; }

    /// <summary>Fires when IT connects or disconnects.</summary>
    public event Action<bool>? ITConnectionChanged;

    // ── Network helpers ───────────────────────────────────────

    public static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530); // Doesn't actually send anything
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>
    /// Constant-time PIN comparison to prevent timing side-channel attacks.
    /// </summary>
    static bool PinMatches(string? submitted, string stored)
    {
        if (submitted is null) return false;
        var a = Encoding.UTF8.GetBytes(submitted);
        var b = Encoding.UTF8.GetBytes(stored);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void Dispose()
    {
        Stop();
    }
}
