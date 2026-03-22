using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PCGuardian;

internal sealed class NetworkForm : Form
{
    readonly DataGridView _grid;
    readonly Label _lblSummary;
    readonly Label _lblStatus;
    readonly System.Windows.Forms.Timer _refreshTimer;
    readonly ConcurrentDictionary<string, string> _dnsCache = new();

    // Row tints by category
    static readonly Color TintRemoteAccess = Color.FromArgb(38, 239, 68, 68);
    static readonly Color TintSystem       = Color.FromArgb(18, 113, 113, 122);
    static readonly Color TintUnknown      = Color.FromArgb(22, 245, 158, 11);

    // -----------------------------------------------------------------------
    //  P/Invoke — GetExtendedTcpTable (same as ScanEngine, duplicated since private)
    // -----------------------------------------------------------------------

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
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
        Text = "Network Monitor \u2014 PC Guardian";
        Size = new(850, 550);
        MinimumSize = new(650, 400);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Summary bar ---
        _lblSummary = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextSecondary,
            Font = Theme.CardTitle,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(14, 0, 0, 0),
            Text = "Loading connections\u2026",
        };

        // --- Status bar ---
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

        // --- Grid ---
        _grid = CreateGrid();
        _grid.Columns.AddRange(
        [
            Col("Process", 110), Col("Category", 85), Col("Local Port", 70),
            Col("Remote Address", 110), Col("Remote Port", 65),
            Col("Hostname", 140), Col("Status", 80),
        ]);
        _grid.CellFormatting += OnCellFormatting;

        // Layout order matters for Dock
        Controls.Add(_grid);
        Controls.Add(_lblSummary);
        Controls.Add(_lblStatus);

        // --- Refresh timer ---
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated) Refresh();
        };
        _refreshTimer.Start();

        Refresh();
    }

    // -----------------------------------------------------------------------
    //  Grid factory (matches ActivityForm style)
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
        HeaderText = header,
        Name = header,
        MinimumWidth = 40,
        FillWeight = weight,
    };

    // -----------------------------------------------------------------------
    //  Data refresh
    // -----------------------------------------------------------------------

    new void Refresh()
    {
        try
        {
            var entries = GetTcpEntries();

            _grid.SuspendLayout();
            _grid.Rows.Clear();

            var processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var destinations = new HashSet<string>();

            foreach (var e in entries)
            {
                processes.Add(e.ProcessName);
                if (e.RemoteAddr is not ("0.0.0.0" or "127.0.0.1"))
                    destinations.Add(e.RemoteAddr);

                string hostname = ResolveHostname(e.RemoteAddr);
                string category = ProcessMonitor.Categorize(e.ProcessName);
                string state = e.State < (uint)TcpStates.Length ? TcpStates[e.State] : e.State.ToString();

                _grid.Rows.Add(
                    e.ProcessName,
                    category,
                    e.LocalPort.ToString(),
                    e.RemoteAddr,
                    e.RemotePort == 0 ? "*" : e.RemotePort.ToString(),
                    hostname,
                    state);
            }

            _grid.ResumeLayout();

            _lblSummary.Text = $"  {entries.Count} connections \u00B7 {processes.Count} processes \u00B7 {destinations.Count} unique destinations";
            _lblStatus.Text = $"  Last updated: {DateTime.Now:h:mm:ss tt} \u00B7 Auto-refreshing every 3s";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = $"  Error: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------------
    //  TCP table via P/Invoke
    // -----------------------------------------------------------------------

    sealed record TcpEntryInfo(
        string ProcessName, string LocalAddr, int LocalPort,
        string RemoteAddr, int RemotePort, uint State);

    static int ToPort(uint raw) =>
        ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);

    static string ProcName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return $"PID {pid}"; }
    }

    static List<TcpEntryInfo> GetTcpEntries()
    {
        var list = new List<TcpEntryInfo>();
        int size = 0;

        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return list;

        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return list;

            int count = Marshal.ReadInt32(buf);
            if (count <= 0 || count > 100_000) return list;

            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var ptr = buf + 4;

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
                    list.Add(new TcpEntryInfo(
                        ProcName(row.dwOwningPid),
                        new IPAddress(row.dwLocalAddr).ToString(),
                        ToPort(row.dwLocalPort),
                        new IPAddress(row.dwRemoteAddr).ToString(),
                        ToPort(row.dwRemotePort),
                        row.dwState));
                }
                catch { /* skip malformed row */ }
                ptr += rowSize;
            }
        }
        catch { /* P/Invoke failure */ }
        finally { Marshal.FreeHGlobal(buf); }

        return list;
    }

    // -----------------------------------------------------------------------
    //  DNS hostname resolution (cached, async with timeout)
    // -----------------------------------------------------------------------

    string ResolveHostname(string ip)
    {
        if (ip is "0.0.0.0" or "127.0.0.1")
            return ip == "127.0.0.1" ? "localhost" : "*";

        if (_dnsCache.TryGetValue(ip, out var cached))
            return cached;

        // Return IP immediately; kick off async resolution
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
            catch
            {
                // Resolution failed — keep the IP in cache
            }
        });

        return ip;
    }

    // -----------------------------------------------------------------------
    //  Cell formatting — color-code rows by category
    // -----------------------------------------------------------------------

    void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0) return;

        var categoryCell = _grid.Rows[e.RowIndex].Cells["Category"];
        var category = categoryCell.Value?.ToString() ?? "";

        Color tint = category switch
        {
            "Remote Access" => TintRemoteAccess,
            "System" or "Windows" => TintSystem,
            "Other" => TintUnknown,
            _ => Color.Empty,
        };

        if (tint != Color.Empty)
            _grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = BlendColor(Theme.BgCard, tint);
    }

    static Color BlendColor(Color bg, Color overlay)
    {
        float a = overlay.A / 255f;
        return Color.FromArgb(
            255,
            (int)(overlay.R * a + bg.R * (1 - a)),
            (int)(overlay.G * a + bg.G * (1 - a)),
            (int)(overlay.B * a + bg.B * (1 - a)));
    }

    // -----------------------------------------------------------------------
    //  Cleanup
    // -----------------------------------------------------------------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            _dnsCache.Clear();
        }
        base.Dispose(disposing);
    }
}
