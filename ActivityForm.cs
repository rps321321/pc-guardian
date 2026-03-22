namespace PCGuardian;

internal sealed class ActivityForm : Form
{
    readonly Database _db;
    readonly TabControl _tabs;
    readonly TextBox _txtSearch;
    readonly Label _lblStatus;
    readonly System.Windows.Forms.Timer _refreshTimer;

    // One grid per tab
    readonly DataGridView _gridActive;
    readonly DataGridView _gridTimeline;
    readonly DataGridView _gridPrograms;
    readonly DataGridView _gridScans;

    // Category row tints (subtle alpha over BgCard)
    static readonly Dictionary<string, Color> CategoryTints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Browser"]      = Color.FromArgb(18, 59, 130, 246),
        ["Development"]  = Color.FromArgb(18, 16, 185, 129),
        ["Communication"]= Color.FromArgb(18, 168, 85, 247),
        ["Gaming"]       = Color.FromArgb(18, 245, 158, 11),
        ["System"]       = Color.FromArgb(18, 113, 113, 122),
        ["Security"]     = Color.FromArgb(18, 239, 68, 68),
    };

    public ActivityForm(Database db)
    {
        _db = db;

        Text = "Activity Log \u2014 PC Guardian";
        Size = new(900, 650);
        MinimumSize = new(750, 500);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Theme.BgPrimary;
        ForeColor = Theme.TextPrimary;
        Icon = SystemIcons.Shield;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // --- Search bar ---
        var pnlTop = new Panel
        {
            Height = 42,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(15, 15, 18),
            Padding = new(12, 8, 12, 6),
        };
        _txtSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = Theme.CardBody,
            BackColor = Theme.BgCard,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Search across all columns...",
        };
        _txtSearch.TextChanged += (_, _) => ApplyFilter();
        pnlTop.Controls.Add(_txtSearch);

        // --- Status bar ---
        _lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Color.FromArgb(15, 15, 18),
            ForeColor = Theme.TextMuted,
            Font = Theme.Small,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new(12, 0, 0, 0),
            Text = "Ready",
        };

        // --- Grids ---
        _gridActive = CreateGrid();
        _gridActive.Columns.AddRange(
        [
            Col("Program", 160), Col("Category", 100), Col("PID", 60),
            Col("Memory", 80), Col("Running Since", 130), Col("Path", 250),
        ]);

        _gridTimeline = CreateGrid();
        _gridTimeline.Columns.AddRange(
        [
            Col("Time", 140), Col("Event", 70), Col("Program", 160),
            Col("Category", 100), Col("PID", 60), Col("Memory", 80),
        ]);

        _gridPrograms = CreateGrid();
        _gridPrograms.Columns.AddRange(
        [
            Col("Program", 160), Col("Category", 100), Col("Company", 130),
            Col("First Seen", 130), Col("Last Seen", 130), Col("Total Sessions", 90),
        ]);

        _gridScans = CreateGrid();
        _gridScans.Columns.AddRange(
        [
            Col("Date/Time", 160), Col("Status", 100),
            Col("Safe", 70), Col("Warning", 70), Col("Danger", 70),
        ]);

        // --- Tabs ---
        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new(130, 32),
            SizeMode = TabSizeMode.Fixed,
            Padding = new(12, 4),
            Font = Theme.CardTitle,
        };
        _tabs.DrawItem += PaintTab;
        _tabs.SelectedIndexChanged += (_, _) => LoadCurrentTab();

        AddTab("Active Now", _gridActive);
        AddTab("Timeline", _gridTimeline);
        AddTab("All Programs", _gridPrograms);
        AddTab("Scan History", _gridScans);

        // --- Layout (order matters for Dock) ---
        Controls.Add(_tabs);
        Controls.Add(pnlTop);
        Controls.Add(_lblStatus);

        // --- Auto-refresh timer for Active Now ---
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (_tabs.SelectedIndex == 0) LoadActiveNow();
        };
        _refreshTimer.Start();

        // --- Cell formatting ---
        _gridTimeline.CellFormatting += OnTimelineCellFormatting;
        _gridScans.CellFormatting += OnScanCellFormatting;
        _gridActive.CellFormatting += OnActiveCellFormatting;

        // Initial load
        LoadCurrentTab();
    }

    // =================================================================
    // Tab helpers
    // =================================================================

    void AddTab(string title, DataGridView grid)
    {
        var page = new TabPage(title)
        {
            BackColor = Theme.BgPrimary,
            ForeColor = Theme.TextPrimary,
        };
        page.Controls.Add(grid);
        _tabs.TabPages.Add(page);
    }

    void PaintTab(object? sender, DrawItemEventArgs e)
    {
        var page = _tabs.TabPages[e.Index];
        var bounds = _tabs.GetTabRect(e.Index);
        bool selected = _tabs.SelectedIndex == e.Index;

        using var bgBrush = new SolidBrush(selected ? Theme.BgCard : Theme.BgPrimary);
        e.Graphics.FillRectangle(bgBrush, bounds);

        using var fgBrush = new SolidBrush(selected ? Theme.Accent : Theme.TextSecondary);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(page.Text, Theme.CardTitle, fgBrush, bounds, sf);

        if (selected)
        {
            using var pen = new Pen(Theme.Accent, 2);
            e.Graphics.DrawLine(pen, bounds.Left + 8, bounds.Bottom - 1, bounds.Right - 8, bounds.Bottom - 1);
        }
    }

    // =================================================================
    // Grid factory
    // =================================================================

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
        grid.RowTemplate.Height = 30;
        grid.EnableHeadersVisualStyles = false;
        return grid;
    }

    static DataGridViewTextBoxColumn Col(string header, int width) => new()
    {
        HeaderText = header,
        Name = header,
        MinimumWidth = 40,
        FillWeight = width,
    };

    // =================================================================
    // Data loading
    // =================================================================

    void LoadCurrentTab()
    {
        switch (_tabs.SelectedIndex)
        {
            case 0: LoadActiveNow(); break;
            case 1: LoadTimeline(); break;
            case 2: LoadPrograms(); break;
            case 3: LoadScanHistory(); break;
        }
    }

    void LoadActiveNow()
    {
        try
        {
            var rows = _db.QueryActiveNow();
            PopulateGrid(_gridActive, rows);
            UpdateStatus(rows.Count, "active process(es)");
        }
        catch (Exception ex)
        {
            UpdateStatus(0, $"Error: {ex.Message}");
        }
    }

    void LoadTimeline()
    {
        try
        {
            var rows = _db.QueryTimeline(200);
            PopulateGrid(_gridTimeline, rows);
            UpdateStatus(rows.Count, "event(s)");
        }
        catch (Exception ex)
        {
            UpdateStatus(0, $"Error: {ex.Message}");
        }
    }

    void LoadPrograms()
    {
        try
        {
            var rows = _db.QueryPrograms();
            PopulateGrid(_gridPrograms, rows);
            UpdateStatus(rows.Count, "program(s)");
        }
        catch (Exception ex)
        {
            UpdateStatus(0, $"Error: {ex.Message}");
        }
    }

    void LoadScanHistory()
    {
        try
        {
            var rows = _db.QueryScanHistory(50);
            PopulateGrid(_gridScans, rows);
            UpdateStatus(rows.Count, "scan(s)");
        }
        catch (Exception ex)
        {
            UpdateStatus(0, $"Error: {ex.Message}");
        }
    }

    static void PopulateGrid(DataGridView grid, List<string[]> rows)
    {
        grid.SuspendLayout();
        grid.Rows.Clear();
        foreach (var row in rows)
            grid.Rows.Add(row);
        grid.ResumeLayout();
    }

    void UpdateStatus(int count, string label)
    {
        if (IsDisposed || !IsHandleCreated) return;
        _lblStatus.Text = $"  {count} {label}";
    }

    // =================================================================
    // Search / filter
    // =================================================================

    void ApplyFilter()
    {
        var grid = _tabs.SelectedIndex switch
        {
            0 => _gridActive,
            1 => _gridTimeline,
            2 => _gridPrograms,
            3 => _gridScans,
            _ => null,
        };
        if (grid is null) return;

        var query = _txtSearch.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            foreach (DataGridViewRow row in grid.Rows)
                row.Visible = true;
            return;
        }

        grid.CurrentCell = null; // Clear selection to allow hiding current row
        foreach (DataGridViewRow row in grid.Rows)
        {
            bool match = false;
            foreach (DataGridViewCell cell in row.Cells)
            {
                if (cell.Value?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                {
                    match = true;
                    break;
                }
            }
            row.Visible = match;
        }

        int visible = grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);
        _lblStatus.Text = $"  {visible} row(s) matching \"{query}\"";
    }

    // =================================================================
    // Cell formatting
    // =================================================================

    void OnActiveCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _gridActive.Columns[e.ColumnIndex].HeaderText != "Category") return;

        var category = e.Value?.ToString() ?? "";
        if (CategoryTints.TryGetValue(category, out var tint))
            _gridActive.Rows[e.RowIndex].DefaultCellStyle.BackColor = BlendColor(Theme.BgCard, tint);
    }

    void OnTimelineCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _gridTimeline.Columns[e.ColumnIndex].HeaderText != "Event") return;

        var value = e.Value?.ToString() ?? "";
        if (value.Equals("START", StringComparison.OrdinalIgnoreCase))
        {
            e.CellStyle!.ForeColor = Theme.Safe;
        }
    }

    void OnScanCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _gridScans.Columns[e.ColumnIndex].HeaderText != "Status") return;

        var value = e.Value?.ToString() ?? "";
        e.CellStyle!.ForeColor = value switch
        {
            "Safe" or "All Good" => Theme.Safe,
            "Warning" or "Worth Checking" => Theme.Warning,
            "Danger" or "Needs Attention" => Theme.Danger,
            _ => Theme.TextPrimary,
        };
        e.CellStyle.Font = Theme.CardTitle;
    }

    // =================================================================
    // Helpers
    // =================================================================

    static Color BlendColor(Color bg, Color overlay)
    {
        float a = overlay.A / 255f;
        return Color.FromArgb(
            255,
            (int)(overlay.R * a + bg.R * (1 - a)),
            (int)(overlay.G * a + bg.G * (1 - a)),
            (int)(overlay.B * a + bg.B * (1 - a)));
    }

    // =================================================================
    // Cleanup
    // =================================================================

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
        }
        base.Dispose(disposing);
    }
}
