using System.Net;
using System.Net.Sockets;
using System.Text;

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
    volatile Report? _latestReport;
    string? _pin;
    int _port;

    public int Port => _port;
    public bool IsRunning => _running;

    public string LocalUrl => $"http://{GetLocalIp()}:{_port}";

    public void Start(int port = 7777, string? pin = null)
    {
        if (_running) return;
        _port = port;
        _pin = string.IsNullOrWhiteSpace(pin) ? null : pin.Trim();

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
        _thread = new Thread(Listen) { IsBackground = true, Name = "ITServer" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
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
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            // PIN check
            if (_pin != null)
            {
                var query = req.QueryString["pin"];
                if (query != _pin)
                {
                    // Show PIN entry page
                    var pinHtml = GeneratePinPage();
                    WriteResponse(resp, pinHtml, 200);
                    return;
                }
            }

            // Serve report
            if (_latestReport != null)
            {
                var html = ReportGenerator.ToHtml(_latestReport);

                // Add a header banner for IT
                html = html.Replace("<div class=\"header\">",
                    "<div class=\"it-banner\">" +
                    "\uD83D\uDD12 Shared by PC Guardian &middot; " +
                    $"Live from {WebUtility.HtmlEncode(Environment.MachineName)} &middot; " +
                    $"Scanned {_latestReport.Timestamp:h:mm tt}" +
                    "</div><div class=\"header\">");

                // Inject IT banner style
                html = html.Replace("</style>",
                    ".it-banner { background: #1e40af; color: white; padding: 10px 20px; " +
                    "font-size: 13px; border-radius: 8px; margin-bottom: 16px; }</style>");

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

    public void Dispose()
    {
        Stop();
    }
}
