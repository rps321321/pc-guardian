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
    volatile bool _shellActive;
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
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cpuCounter?.Dispose();
        _cpuCounter = null;
        _memCounter?.Dispose();
        _memCounter = null;
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

        // Trust level check — shell requires "full"
        if (TrustLevel != "full")
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }

        // Bug 7: Guard against multiple simultaneous shells
        if (_shellActive)
        {
            var wsReject = await ctx.AcceptWebSocketAsync(null);
            await wsReject.WebSocket.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                "Another session is active", CancellationToken.None);
            wsReject.WebSocket.Dispose();
            return;
        }
        _shellActive = true;

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
            _shellActive = false;
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

    /// <summary>Trust level from deploy config — "view", "assist", or "full".</summary>
    internal string TrustLevel { get; set; } = "view";

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
                var html = GenerateTerminalPage(CompanyName, TrustLevel);
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
                var result = ExecuteAction(action);
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
            var usedMemMb = totalMemMb - (long)(_memCounter?.NextValue() ?? 0);

            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => new
                {
                    name = d.Name,
                    totalGb = d.TotalSize / (1024L * 1024 * 1024),
                    freeGb = d.AvailableFreeSpace / (1024L * 1024 * 1024)
                });

            var scanStatus = _latestReport != null
                ? new { status = "completed", timestamp = _latestReport.Timestamp.ToString("o"), issueCount = _latestReport.WarningCount + _latestReport.DangerCount }
                : null;

            var metrics = new
            {
                cpu = cpuPercent,
                ramTotalMb = totalMemMb,
                ramUsedMb = usedMemMb,
                gpu = "N/A",
                disks = drives,
                hostname = Environment.MachineName,
                scan = scanStatus
            };

            return JsonSerializer.Serialize(metrics);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { cpu = -1, ramTotalMb = 0, ramUsedMb = 0, gpu = "N/A", error = ex.Message, hostname = Environment.MachineName });
        }
    }

    /// <summary>Fired when a remote IT user requests a scan via the API.</summary>
    public event Action? ScanRequested;

    string ExecuteAction(string action)
    {
        try
        {
            var result = action.ToLowerInvariant() switch
            {
                "scan" => InvokeScan(),
                "fixdns" => RunShellAction("ipconfig /flushdns"),
                "firewall" => RunShellAction("netsh advfirewall set allprofiles state on"),
                _ => new { ok = false, message = $"Unknown action: {action}" }
            };
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, message = ex.Message });
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
            var output = proc?.StandardOutput.ReadToEnd() ?? "";
            var error = stderrTask?.Result ?? "";
            proc?.WaitForExit(5000);
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

    static string GenerateTerminalPage(string company, string trustLevel)
    {
        var safeCompany = WebUtility.HtmlEncode(company);
        var showTerminal = trustLevel == "full";
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<title>" + safeCompany + " - Remote Support</title>"
            + "<style>"
            + "*{margin:0;padding:0;box-sizing:border-box}"
            + "body{font-family:'Segoe UI',system-ui,sans-serif;background:#09090b;color:#fafafa;height:100vh;display:flex;flex-direction:column}"
            + ".pin-screen{display:flex;justify-content:center;align-items:center;height:100vh}"
            + ".pin-box{text-align:center;background:#18181b;padding:48px;border-radius:16px;border:1px solid #27272a}"
            + ".pin-box h2{margin-bottom:12px;font-size:24px}"
            + ".pin-box p{color:#a1a1aa;margin-bottom:24px;font-size:14px}"
            + ".pin-box input{font-size:28px;width:200px;text-align:center;padding:12px;background:#09090b;border:2px solid #27272a;border-radius:8px;color:#fafafa;letter-spacing:8px}"
            + ".pin-box input:focus{outline:none;border-color:#6366f1}"
            + ".pin-box button{margin-top:20px;padding:12px 40px;background:#6366f1;color:white;border:none;border-radius:8px;font-size:16px;cursor:pointer}"
            + ".pin-box .error{color:#ef4444;margin-top:12px;font-size:13px;display:none}"
            + "header{background:#18181b;padding:12px 24px;display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid #27272a}"
            + "header h1{font-size:18px;font-weight:600}"
            + ".tabs{display:flex;gap:0;background:#18181b;border-bottom:1px solid #27272a}"
            + ".tab{padding:10px 24px;cursor:pointer;color:#a1a1aa;font-size:14px;border-bottom:2px solid transparent}"
            + ".tab:hover{color:#fafafa}.tab.active{color:#6366f1;border-color:#6366f1}"
            + ".content{flex:1;overflow:auto;padding:24px}"
            + ".metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:16px;margin-bottom:24px}"
            + ".mc{background:#18181b;border:1px solid #27272a;border-radius:12px;padding:20px}"
            + ".mc .l{font-size:12px;color:#a1a1aa;text-transform:uppercase;letter-spacing:1px}"
            + ".mc .v{font-size:32px;font-weight:700;margin-top:8px}"
            + ".safe{color:#10b981}.warn{color:#f59e0b}.danger{color:#ef4444}"
            + ".actions{display:flex;gap:12px;margin-bottom:24px}"
            + ".actions button{padding:10px 20px;background:#18181b;border:1px solid #27272a;border-radius:8px;color:#fafafa;cursor:pointer;font-size:13px}"
            + ".actions button:hover{background:#27272a}"
            + ".actions .pri{background:#6366f1;border-color:#6366f1}"
            + "#tout{flex:1;background:#09090b;font-family:Consolas,monospace;font-size:13px;padding:16px;overflow-y:auto;white-space:pre-wrap;line-height:1.6;border:1px solid #27272a;border-radius:8px}"
            + "#trow{display:flex;align-items:center;padding:12px 0;gap:8px}"
            + "#trow span{color:#6366f1;font-family:monospace;font-weight:bold}"
            + "#tinp{flex:1;background:#18181b;border:1px solid #27272a;border-radius:6px;color:#fafafa;font-family:monospace;font-size:13px;padding:8px 12px}"
            + "#tinp:focus{outline:none;border-color:#6366f1}"
            + "#slog{font-family:monospace;font-size:12px;line-height:1.8}"
            + "#slog .e{padding:4px 0;border-bottom:1px solid #18181b}"
            + "#slog .t{color:#71717a}"
            + "</style></head><body>"
            + "<div class=\"pin-screen\" id=\"ps\">"
            + "<div class=\"pin-box\"><h2>" + safeCompany + "</h2>"
            + "<p>Enter PIN to access remote support</p>"
            + "<input type=\"text\" id=\"pi\" maxlength=\"10\" autofocus placeholder=\"PIN\">"
            + "<br><button onclick=\"ck()\">Connect</button>"
            + "<div class=\"error\" id=\"pe\">Invalid PIN</div></div></div>"
            + "<div id=\"app\" style=\"display:none;flex-direction:column;height:100vh\">"
            + "<header><h1>" + safeCompany + " - Remote Support</h1>"
            + "<div style=\"display:flex;align-items:center;gap:8px;font-size:13px;color:#a1a1aa\">"
            + "<div style=\"width:8px;height:8px;border-radius:50%;background:#10b981\" id=\"cd\"></div>"
            + "<span id=\"ct\">Connected</span></div></header>"
            + "<div class=\"tabs\">"
            + "<div class=\"tab active\" onclick=\"st('d')\" id=\"td\">Dashboard</div>"
            + (showTerminal ? "<div class=\"tab\" onclick=\"st('t')\" id=\"tt\">Terminal</div>" : "")
            + "<div class=\"tab\" onclick=\"st('l')\" id=\"tl\">Session Log</div></div>"
            + "<div class=\"content\" id=\"co\">"
            + "<div id=\"vd\"><div class=\"metrics\" id=\"mt\"></div>"
            + "<div class=\"actions\">"
            + "<button class=\"pri\" onclick=\"da('scan')\">Run Scan</button>"
            + "<button onclick=\"da('fixdns')\">Fix DNS</button>"
            + "<button onclick=\"da('firewall')\">Enable Firewall</button></div></div>"
            + "<div id=\"vt\" style=\"display:none;height:100%;flex-direction:column\">"
            + "<div id=\"tout\"></div>"
            + "<div id=\"trow\"><span>PS&gt;</span><input type=\"text\" id=\"tinp\" placeholder=\"Type a command...\" onkeydown=\"if(event.key==='Enter')sc()\"></div></div>"
            + "<div id=\"vl\" style=\"display:none\"><div id=\"slog\"></div></div>"
            + "</div></div>"
            + "<script>"
            + "let pn='',ws=null,ch=[],hi=-1;"
            + "function ck(){pn=document.getElementById('pi').value;"
            + "fetch('/api/metrics?pin='+encodeURIComponent(pn)).then(r=>{"
            + "if(r.ok){document.getElementById('ps').style.display='none';"
            + "var a=document.getElementById('app');a.style.display='flex';"
            + "sessionStorage.setItem('pin',pn);sp();al('Connected to '+location.hostname);"
            + (showTerminal ? "cw();" : "")
            + "}else{document.getElementById('pe').style.display='block'}"
            + "}).catch(()=>document.getElementById('pe').style.display='block')}"
            + "function st(t){document.querySelectorAll('.tab').forEach(e=>e.classList.remove('active'));"
            + "var m={d:'td',t:'tt',l:'tl'};if(document.getElementById(m[t]))document.getElementById(m[t]).classList.add('active');"
            + "['d','t','l'].forEach(v=>{var e=document.getElementById('v'+v);if(e)e.style.display=v===t?(v==='t'?'flex':'block'):'none'})}"
            + "function sp(){rm();setInterval(rm,3000)}"
            + "function rm(){fetch('/api/metrics?pin='+encodeURIComponent(pn)).then(r=>r.json()).then(d=>{"
            + "var m=document.getElementById('mt');"
            + "var c=function(v,l,h){return v<l?'safe':v<h?'warn':'danger'};"
            + "var rp=d.ramTotalMb>0?Math.round(d.ramUsedMb/d.ramTotalMb*100):0;"
            + "m.textContent='';"  // Clear safely
            + "var cards=[{l:'CPU',v:d.cpu+'%',s:c(d.cpu,50,80)},{l:'RAM',v:rp+'%',s:c(rp,60,85)},"
            + "{l:'Host',v:d.hostname,s:''},{l:'Scan',v:d.scan?'Done':'Pending',s:''}];"
            + "cards.forEach(function(cd){"
            + "var div=document.createElement('div');div.className='mc';"
            + "var lbl=document.createElement('div');lbl.className='l';lbl.textContent=cd.l;"
            + "var val=document.createElement('div');val.className='v'+(cd.s?' '+cd.s:'');val.textContent=cd.v;"
            + "if(cd.l==='Host'||cd.l==='Scan')val.style.fontSize='16px';"
            + "div.appendChild(lbl);div.appendChild(val);m.appendChild(div)})"
            + "}).catch(()=>{})}"
            + "function da(a){al('Action: '+a);"
            + "fetch('/api/action?pin='+encodeURIComponent(pn)+'&action='+a).then(r=>r.json()).then(d=>{"
            + "al('Result: '+(d.message||JSON.stringify(d)));"
            + "if(a==='scan')setTimeout(rm,3000)}).catch(e=>al('Error: '+e))}"
            + (showTerminal
                ? "function cw(){var p=location.protocol==='https:'?'wss:':'ws:';"
                  + "ws=new WebSocket(p+'//'+location.host+'/shell?pin='+encodeURIComponent(pn));"
                  + "ws.onopen=function(){al('Shell connected');document.getElementById('cd').style.background='#10b981'};"
                  + "ws.onmessage=function(e){var o=document.getElementById('tout');o.textContent+=e.data;o.scrollTop=o.scrollHeight};"
                  + "ws.onclose=function(){al('Shell disconnected');document.getElementById('cd').style.background='#ef4444';"
                  + "setTimeout(cw,3000)}}"
                  + "function sc(){var i=document.getElementById('tinp');"
                  + "if(!i.value.trim()||!ws)return;var cmd=i.value;ch.unshift(cmd);hi=-1;"
                  + "al('$ '+cmd);ws.send(cmd+'\\n');i.value=''}"
                : "")
            + "function al(msg){var log=document.getElementById('slog');"
            + "var d=document.createElement('div');d.className='e';"
            + "var ts=document.createElement('span');ts.className='t';ts.textContent=new Date().toLocaleTimeString()+' ';"
            + "d.appendChild(ts);d.appendChild(document.createTextNode(msg));log.appendChild(d)}"
            + "window.onload=function(){var s=sessionStorage.getItem('pin');"
            + "if(s){document.getElementById('pi').value=s;ck()}"
            + "var inp=document.getElementById('tinp');"
            + "if(inp)inp.addEventListener('keydown',function(e){"
            + "if(e.key==='ArrowUp'&&ch.length){hi=Math.min(hi+1,ch.length-1);e.target.value=ch[hi]}"
            + "if(e.key==='ArrowDown'){hi=Math.max(hi-1,-1);e.target.value=hi>=0?ch[hi]:''}})};"
            + "</script></body></html>";
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
