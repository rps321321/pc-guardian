using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PCGuardian;

internal sealed class NetworkForm : Form
{
    readonly TabControl _tabs;
    readonly DataGridView _gridTraffic;
    readonly DataGridView _gridConns;
    readonly Label _lblBandwidth;
    readonly Label _lblStatus;
    readonly BandwidthMonitor _bwMonitor;
    readonly System.Windows.Forms.Timer _connTimer;
    readonly ConcurrentDictionary<string, string> _dnsCache = new();

    // Row tints
    static readonly Color TintRemoteAccess = Color.FromArgb(38, 239, 68, 68);
    static readonly Color TintSystem       = Color.FromArgb(18, 113, 113, 122);
    static readonly Color TintUnknown      = Color.FromArgb(22, 245, 158, 11);

    // --- P/Invoke for connections tab ---

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool order,
        int af, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct TcpRow
    {
        public uint state, localAddr, localPort, remoteAddr, remotePort, pid;
    }

    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;

    static readonly string[] TcpStates =
    [
        "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED",
        "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK",
        "TIME_WAIT", "DELETE_TCB"
    ];

    // -----------------------------------------------------------------------
    //  Constructor
    // -----------------------------------------------------------------------

    public NetworkForm()
    {
        Text = "Network Traffic \u2014 PC Guardian";
        Size = new(900, 600);
        MinimumSize = new(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Bandwidth bar (top) ---
        _lblBandwidth = new Label
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextPrimary,
            Font = Theme.CardTitle,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(14, 0, 0, 0),
            Text = "  Measuring bandwidth\u2026",
        };

        // --- Status bar (bottom) ---
        _lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextMuted,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(14, 0, 0, 0),
        };

        // --- Tabs ---
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = Theme.CardTitle,
        };
        // Dark style the tab control
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += DrawTab;
        _tabs.Padding = new(12, 4);

        // --- Traffic tab (bandwidth per app) ---
        _gridTraffic = CreateGrid();
        _gridTraffic.Columns.AddRange(
        [
            Col("Process", 120),
            Col("Category", 90),
            Col("\u2193 In", 80),
            Col("\u2191 Out", 80),
            Col("Connections", 75),
            Col("Total In", 80),
            Col("Total Out", 80),
        ]);
        _gridTraffic.CellFormatting += OnCellFormattingTraffic;

        var tabTraffic = new TabPage("  \u26A1 Bandwidth  ") { BackColor = Theme.BgPrimary };
        tabTraffic.Controls.Add(_gridTraffic);
        _tabs.TabPages.Add(tabTraffic);

        // --- Connections tab (raw TCP) ---
        _gridConns = CreateGrid();
        _gridConns.Columns.AddRange(
        [
            Col("Process", 110), Col("Category", 85), Col("Local Port", 70),
            Col("Remote Address", 110), Col("Remote Port", 65),
            Col("Hostname", 140), Col("Status", 80),
        ]);
        _gridConns.CellFormatting += OnCellFormattingConns;

        var tabConns = new TabPage("  \uD83D\uDD17 Connections  ") { BackColor = Theme.BgPrimary };
        tabConns.Controls.Add(_gridConns);
        _tabs.TabPages.Add(tabConns);

        // Layout: status bottom, bandwidth top, tabs fill
        Controls.Add(_tabs);
        Controls.Add(_lblBandwidth);
        Controls.Add(_lblStatus);

        // --- Bandwidth monitor ---
        _bwMonitor = new BandwidthMonitor(2000);
        _bwMonitor.Updated += () =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(RefreshTraffic); } catch { }
        };

        // --- Connection refresh timer (for connections tab) ---
        _connTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _connTimer.Tick += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated && _tabs.SelectedIndex == 1)
                RefreshConnections();
        };
        _connTimer.Start();
    }

    // -----------------------------------------------------------------------
    //  Traffic tab refresh
    // -----------------------------------------------------------------------

    void RefreshTraffic()
    {
        try
        {
            var traffic = _bwMonitor.GetTraffic();
            var (totIn, totOut, totConns, totProcs) = _bwMonitor.GetTotals();

            _lblBandwidth.Text = $"  \u2193 {BandwidthMonitor.FormatRate(totIn)}    " +
                                 $"\u2191 {BandwidthMonitor.FormatRate(totOut)}    " +
                                 $"\u00B7  {totConns} connections  \u00B7  {totProcs} apps";

            // Color the rates
            _lblBandwidth.ForeColor = totIn + totOut > 10_485_760 // >10 MB/s
                ? Theme.Warning
                : Theme.TextPrimary;

            _gridTraffic.SuspendLayout();
            _gridTraffic.Rows.Clear();

            foreach (var t in traffic)
            {
                _gridTraffic.Rows.Add(
                    t.Name,
                    t.Category,
                    BandwidthMonitor.FormatRate(t.InRate),
                    BandwidthMonitor.FormatRate(t.OutRate),
                    t.Connections > 0 ? t.Connections.ToString() : "\u2014",
                    BandwidthMonitor.FormatBytes(t.TotalIn),
                    BandwidthMonitor.FormatBytes(t.TotalOut));
            }

            _gridTraffic.ResumeLayout();
            _lblStatus.Text = $"  Live \u00B7 Updated: {DateTime.Now:h:mm:ss tt} \u00B7 Refreshing every 2s";
        }
        catch { }
    }

    // -----------------------------------------------------------------------
    //  Connections tab refresh
    // -----------------------------------------------------------------------

    void RefreshConnections()
    {
        try
        {
            var entries = GetTcpEntries();

            _gridConns.SuspendLayout();
            _gridConns.Rows.Clear();

            var procs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dests = new HashSet<string>();

            foreach (var e in entries)
            {
                procs.Add(e.ProcessName);
                if (e.RemoteAddr is not ("0.0.0.0" or "127.0.0.1"))
                    dests.Add(e.RemoteAddr);

                string hostname = ResolveHostname(e.RemoteAddr);
                string category = ProcessMonitor.Categorize(e.ProcessName);
                string state = e.State < (uint)TcpStates.Length ? TcpStates[e.State] : e.State.ToString();

                _gridConns.Rows.Add(
                    e.ProcessName, category, e.LocalPort.ToString(),
                    e.RemoteAddr,
                    e.RemotePort == 0 ? "*" : e.RemotePort.ToString(),
                    hostname, state);
            }

            _gridConns.ResumeLayout();
        }
        catch { }
    }

    // -----------------------------------------------------------------------
    //  Grid factory
    // -----------------------------------------------------------------------

    static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Theme.BgPrimary,
            GridColor = Theme.Border,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            Dock = DockStyle.Fill,
        };
        grid.DefaultCellStyle.BackColor = Theme.BgCard;
        grid.DefaultCellStyle.ForeColor = Theme.TextPrimary;
        grid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        grid.DefaultCellStyle.Font = Theme.CardBody;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(15, 15, 18);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextSecondary;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.CardTitle;
        grid.ColumnHeadersHeight = 36;
        grid.RowTemplate.Height = 28;
        grid.EnableHeadersVisualStyles = false;
        return grid;
    }

    static DataGridViewTextBoxColumn Col(string header, int weight) => new()
    {
        HeaderText = header, Name = header,
        MinimumWidth = 40, FillWeight = weight,
    };

    // -----------------------------------------------------------------------
    //  Tab drawing (dark theme)
    // -----------------------------------------------------------------------

    void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var tab = _tabs.TabPages[e.Index];
        bool selected = _tabs.SelectedIndex == e.Index;

        using var bgBrush = new SolidBrush(selected ? Theme.BgCard : Theme.BgPrimary);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        using var fgBrush = new SolidBrush(selected ? Theme.Accent : Theme.TextSecondary);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(tab.Text, Theme.CardTitle, fgBrush, e.Bounds, sf);
    }

    // -----------------------------------------------------------------------
    //  Cell formatting
    // -----------------------------------------------------------------------

    void OnCellFormattingTraffic(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;

        var row = _gridTraffic.Rows[e.RowIndex];
        var category = row.Cells["Category"].Value?.ToString() ?? "";

        // Tint by category
        Color tint = category switch
        {
            "Remote Access" => TintRemoteAccess,
            "System" or "Windows" => TintSystem,
            "Other" => TintUnknown,
            _ => Color.Empty,
        };
        if (tint != Color.Empty)
            row.DefaultCellStyle.BackColor = Blend(Theme.BgCard, tint);

        // Color the rate columns
        var colName = _gridTraffic.Columns[e.ColumnIndex].Name;
        if (colName is "\u2193 In" or "\u2191 Out")
        {
            var text = e.Value?.ToString() ?? "";
            if (text.Contains("MB/s") || text.Contains("GB/s"))
                e.CellStyle!.ForeColor = Theme.Warning;
            else if (text.Contains("KB/s"))
                e.CellStyle!.ForeColor = Theme.Safe;
            else
                e.CellStyle!.ForeColor = Theme.TextMuted;
        }
    }

    void OnCellFormattingConns(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var category = _gridConns.Rows[e.RowIndex].Cells["Category"].Value?.ToString() ?? "";
        Color tint = category switch
        {
            "Remote Access" => TintRemoteAccess,
            "System" or "Windows" => TintSystem,
            "Other" => TintUnknown,
            _ => Color.Empty,
        };
        if (tint != Color.Empty)
            _gridConns.Rows[e.RowIndex].DefaultCellStyle.BackColor = Blend(Theme.BgCard, tint);
    }

    static Color Blend(Color bg, Color overlay)
    {
        float a = overlay.A / 255f;
        return Color.FromArgb(255,
            (int)(overlay.R * a + bg.R * (1 - a)),
            (int)(overlay.G * a + bg.G * (1 - a)),
            (int)(overlay.B * a + bg.B * (1 - a)));
    }

    // -----------------------------------------------------------------------
    //  TCP table for connections tab
    // -----------------------------------------------------------------------

    sealed record TcpEntry(string ProcessName, string LocalAddr, int LocalPort,
                           string RemoteAddr, int RemotePort, uint State);

    static int ToPort(uint raw) => ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);

    static string ProcName(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return $"PID {pid}"; }
    }

    List<TcpEntry> GetTcpEntries()
    {
        var list = new List<TcpEntry>();
        int size = 0;

        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return list;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return list;

            int count = Marshal.ReadInt32(buf);
            if (count is <= 0 or > 100_000) return list;

            int rowSize = Marshal.SizeOf<TcpRow>();
            var ptr = buf + 4;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var row = Marshal.PtrToStructure<TcpRow>(ptr);
                    list.Add(new TcpEntry(
                        ProcName(row.pid),
                        new IPAddress(row.localAddr).ToString(),
                        ToPort(row.localPort),
                        new IPAddress(row.remoteAddr).ToString(),
                        ToPort(row.remotePort),
                        row.state));
                }
                catch { }
                ptr += rowSize;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }

        return list;
    }

    // -----------------------------------------------------------------------
    //  DNS resolution (cached, async)
    // -----------------------------------------------------------------------

    string ResolveHostname(string ip)
    {
        if (ip is "0.0.0.0" or "127.0.0.1")
            return ip == "127.0.0.1" ? "localhost" : "*";

        if (_dnsCache.TryGetValue(ip, out var cached))
            return cached;

        _dnsCache[ip] = ip;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var entry = await Dns.GetHostEntryAsync(ip, cts.Token);
                if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != ip)
                    _dnsCache[ip] = entry.HostName;
            }
            catch { }
        });

        return ip;
    }

    // -----------------------------------------------------------------------
    //  Cleanup
    // -----------------------------------------------------------------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _connTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connTimer?.Stop();
            _connTimer?.Dispose();
            _bwMonitor?.Dispose();
            _dnsCache.Clear();
        }
        base.Dispose(disposing);
    }
}
