namespace DiskHog;

sealed partial class MainForm : Form
{
    readonly AppSettings _settings;
    readonly Scanner _scanner = new();
    readonly bool _isAdmin;
    readonly string _startupOpen;
    readonly bool _startupContinue;

    ToolStrip _tool;
    ToolStripButton _btnScan, _btnPause, _btnStop, _btnOpen, _btnSave, _btnOptions, _btnAdmin, _btnHelp;
    ToolStripSplitButton _btnReport;
    ToolStripMenuItem _miReportCsv, _miReportCopy, _miOpenReports, _miOpenLastReport;
    ToolStripComboBox _cmbMetric;

    ListView _lvDrives;

    SplitContainer _split;
    TabControl _tabs;
    TabPage _tabTree, _tabFiles, _tabTypes, _tabHogs, _tabErrors;
    TreeListView _tree;
    VList<FileNode> _lvFiles;
    NumericUpDown _numMinMB, _numOlderDays, _numMaxCount;
    TextBox _txtFilter;
    Label _lblFilesInfo;
    VList<ExtStat> _lvTypes;
    VList<HogHit> _lvHogs;
    VList<ScanError> _lvErrors;
    Label _lblErrorsTip;

    TreemapControl _map;
    Button _btnUp, _btnTop;
    Label _lblZoom;
    FlowLayoutPanel _legend;

    StatusStrip _status;
    ToolStripStatusLabel _stState, _stFiles, _stSize, _stRate, _stTime, _stErrors, _stPath;
    ToolStripProgressBar _stProgress;

    ContextMenuStrip _ctx;
    ToolStripMenuItem _miOpen, _miExplorer, _miCopy, _miShowTree, _miZoom, _miRescan, _miProps, _miRecycle, _miDelete;
    List<Node> _ctxNodes = new();

    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 450 };

    int S(int px) => LogicalToDeviceUnits(px);

    public MainForm(string openFile, bool continueScan)
    {
        _settings = AppSettings.Load();
        _settings.Clamp();
        _isAdmin = ReportWriter.IsAdmin();
        _startupOpen = openFile;
        _startupContinue = continueScan;
        Display.OnDisk = !_settings.ShowLogicalSize;

        Text = "DiskHog — what's eating my disk?" + (_isAdmin ? "  (Administrator)" : "");
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(S(900), S(600));
        Icon = MakeAppIcon();
        RestoreWindow();

        BuildToolbar();
        var drivePanel = BuildDrivePanel();
        BuildMainArea();
        BuildStatus();
        BuildContextMenu();

        Controls.Add(_split);
        Controls.Add(drivePanel);
        Controls.Add(_tool);
        Controls.Add(_status);

        _uiTimer.Tick += (_, _) => OnUiTick();
        _filterDebounce.Tick += (_, _) => { _filterDebounce.Stop(); RefreshFiles(); };
        _scanner.Finished += () => { try { BeginInvoke(OnScanFinished); } catch (InvalidOperationException) { } };

        Load += OnLoadForm;
        FormClosing += OnFormClosing;
        UpdateButtons();
    }

    static Icon MakeAppIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillRectangle(new SolidBrush(Color.FromArgb(78, 121, 167)), 1, 1, 18, 30);
                g.FillRectangle(new SolidBrush(Color.FromArgb(225, 87, 89)), 20, 1, 11, 18);
                g.FillRectangle(new SolidBrush(Color.FromArgb(237, 201, 72)), 20, 20, 11, 11);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch { return null; }
    }

    void RestoreWindow()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1400, 900);
        var bounds = new Rectangle(_settings.WinX, _settings.WinY, _settings.WinW, _settings.WinH);
        if (_settings.WinW >= 600 && _settings.WinH >= 400 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            Bounds = bounds;
        else
        {
            var size = new Size(Math.Min(S(1400), wa.Width - 40), Math.Min(S(920), wa.Height - 40));
            Bounds = new Rectangle(wa.X + (wa.Width - size.Width) / 2, wa.Y + (wa.Height - size.Height) / 2, size.Width, size.Height);
        }
        if (_settings.Maximized) WindowState = FormWindowState.Maximized;
    }

    void BuildToolbar()
    {
        int ico = S(16);
        _tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(ico, ico), Padding = new Padding(S(4), S(2), S(4), S(2)) };
        _btnScan = new ToolStripButton("Scan", Icons.Play(ico)) { ToolTipText = "Scan the ticked drives / folders (F5)" };
        _btnPause = new ToolStripButton("Pause", Icons.Pause(ico)) { ToolTipText = "Pause / resume the scan (Ctrl+P)" };
        _btnStop = new ToolStripButton("Stop", Icons.Stop(ico)) { ToolTipText = "Stop the scan. Results so far are kept and you can continue later." };
        _btnOpen = new ToolStripButton("Open results…", Icons.Folder(ico)) { ToolTipText = "Open a saved scan (.dhog) (Ctrl+O)" };
        _btnSave = new ToolStripButton("Save results…", Icons.Save(ico)) { ToolTipText = "Save this scan, finished or not, to reopen or continue later (Ctrl+S)" };
        _btnReport = new ToolStripSplitButton("Export report…", Icons.Report(ico)) { ToolTipText = "Write a text report you (or an AI) can read (Ctrl+E)" };
        _miReportCsv = new ToolStripMenuItem("Export report + CSV of everything above the CSV size limit…");
        _miReportCopy = new ToolStripMenuItem("Copy report to clipboard (to paste into an AI chat)");
        _miOpenLastReport = new ToolStripMenuItem("Open last report") { Enabled = false };
        _miOpenReports = new ToolStripMenuItem("Open reports folder");
        _btnReport.DropDownItems.AddRange(new ToolStripItem[] { _miReportCopy, _miReportCsv, new ToolStripSeparator(), _miOpenLastReport, _miOpenReports });
        _cmbMetric = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = S(120), ToolTipText = "Size on disk = what you actually get back by deleting. Logical = the file's nominal size." };
        _cmbMetric.Items.AddRange(new object[] { "Size on disk", "Logical size" });
        _cmbMetric.SelectedIndex = Display.OnDisk ? 0 : 1;
        _btnOptions = new ToolStripButton("Options", Icons.Gear(ico));
        _btnAdmin = new ToolStripButton("Restart as admin", Icons.Shield(ico))
        {
            ToolTipText = "Without admin rights some protected folders can't be read and end up in 'Unaccounted'. Current results are carried over.",
            Visible = !_isAdmin,
        };
        _btnHelp = new ToolStripButton("Help") { Alignment = ToolStripItemAlignment.Right };

        _tool.Items.AddRange(new ToolStripItem[]
        {
            _btnScan, _btnPause, _btnStop, new ToolStripSeparator(),
            _btnOpen, _btnSave, _btnReport, new ToolStripSeparator(),
            new ToolStripLabel("Show:"), _cmbMetric, new ToolStripSeparator(),
            _btnOptions, _btnAdmin, _btnHelp,
        });

        _btnScan.Click += (_, _) => StartNewScan();
        _btnPause.Click += (_, _) => PauseResumeOrContinue();
        _btnStop.Click += (_, _) => StopScan();
        _btnOpen.Click += (_, _) => OpenSession();
        _btnSave.Click += (_, _) => SaveSession();
        _btnReport.ButtonClick += (_, _) => ExportReport(false);
        _miReportCsv.Click += (_, _) => ExportReport(true);
        _miReportCopy.Click += (_, _) => CopyReport();
        _miOpenReports.Click += (_, _) => { Directory.CreateDirectory(_settings.ResolvedReportFolder); Shell.Open(_settings.ResolvedReportFolder); };
        _miOpenLastReport.Click += (_, _) => { if (_lastReportPath != null) Shell.Open(_lastReportPath); };
        _cmbMetric.SelectedIndexChanged += (_, _) => SetMetric(_cmbMetric.SelectedIndex == 0);
        _btnOptions.Click += (_, _) => ShowOptions();
        _btnAdmin.Click += (_, _) => RestartAsAdmin();
        _btnHelp.Click += (_, _) => ShowHelp();
    }

    Control BuildDrivePanel()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = S(150), Padding = new Padding(S(6), S(4), S(6), S(4)) };
        _lvDrives = new ListView
        {
            Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, CheckBoxes = true, FullRowSelect = true,
            HideSelection = false, MultiSelect = false,
        };
        _lvDrives.Columns.Add("Drive / folder to scan", S(230));
        _lvDrives.Columns.Add("Label", S(130));
        _lvDrives.Columns.Add("Type", S(70));
        _lvDrives.Columns.Add("File system", S(80));
        _lvDrives.Columns.Add("Capacity", S(85), HorizontalAlignment.Right);
        _lvDrives.Columns.Add("Used", S(85), HorizontalAlignment.Right);
        _lvDrives.Columns.Add("Free", S(85), HorizontalAlignment.Right);
        _lvDrives.Columns.Add("Used %", S(65), HorizontalAlignment.Right);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = S(150), FlowDirection = FlowDirection.TopDown, Padding = new Padding(S(6), 0, 0, 0), WrapContents = false };
        Button Btn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Width = S(138), Height = S(27), Margin = new Padding(0, 0, 0, S(4)) };
            b.Click += onClick;
            buttons.Controls.Add(b);
            return b;
        }
        Btn("Add folder…", (_, _) => AddFolder());
        Btn("Remove folder", (_, _) => RemoveFolder());
        Btn("Refresh drive list", (_, _) => PopulateDrives());
        Btn("Tick all drives", (_, _) => { foreach (ListViewItem it in _lvDrives.Items) if (it.Tag is string p && p.Length <= 3) it.Checked = true; });

        panel.Controls.Add(_lvDrives);
        panel.Controls.Add(buttons);
        return panel;
    }

    void BuildMainArea()
    {
        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = S(6) };

        // ---- tabs
        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabTree = new TabPage("Folders");
        _tabFiles = new TabPage("Largest files");
        _tabTypes = new TabPage("File types");
        _tabHogs = new TabPage("Known space hogs");
        _tabErrors = new TabPage("Unreadable folders");
        _tabs.TabPages.AddRange(new[] { _tabTree, _tabFiles, _tabTypes, _tabHogs, _tabErrors });
        _tabs.SelectedIndexChanged += (_, _) => RefreshActiveTab();

        _tree = new TreeListView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        _tree.NodeSelected += n => { _map.Selected = n; ShowNodeInStatus(n); };
        _tree.NodeActivated += n => { if (!n.IsVirtual) Shell.ShowInExplorer(n.FullPath); };
        _tree.KeyDown += (_, e) => HandleItemKeys(e, _tree.SelectedNode is { } n ? new List<Node> { n } : new List<Node>());
        _tree.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && _tree.SelectedNode is { } n) ShowContext(new List<Node> { n }, _tree, e.Location); };
        _tabTree.Controls.Add(_tree);

        BuildFilesTab();
        BuildTypesTab();
        BuildHogsTab();
        BuildErrorsTab();
        _split.Panel1.Controls.Add(_tabs);

        // ---- treemap
        _map = new TreemapControl { Dock = DockStyle.Fill };
        _map.NodeClicked += n => { _tree.SelectNode(n); ShowNodeInStatus(n); };
        _map.NodeRightClicked += (n, p) => { _tree.SelectNode(n); ShowContext(new List<Node> { n }, _map, p); };
        _map.ZoomChanged += () => { _lblZoom.Text = _map.ZoomText; _btnUp.Enabled = _map.CanZoomOut; _btnTop.Enabled = _map.CanZoomOut; };
        _map.KeyDown += (_, e) => HandleItemKeys(e, _map.Selected is { } n ? new List<Node> { n } : new List<Node>());

        var bar = new Panel { Dock = DockStyle.Top, Height = S(30), Padding = new Padding(S(4), S(3), S(4), S(3)) };
        _btnUp = new Button { Text = "▲ Up", Dock = DockStyle.Left, Width = S(70), Enabled = false };
        _btnTop = new Button { Text = "Top", Dock = DockStyle.Left, Width = S(55), Enabled = false };
        _lblZoom = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(8), 0, 0, 0), Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true, Text = "Nothing scanned yet" };
        var hint = new Label { Dock = DockStyle.Right, Width = S(420), TextAlign = ContentAlignment.MiddleRight, ForeColor = SystemColors.GrayText, Text = "Click = select · double-click = zoom in · Backspace / mouse-back = zoom out · right-click = actions" };
        _btnUp.Click += (_, _) => _map.ZoomOut();
        _btnTop.Click += (_, _) => _map.ZoomTop();
        bar.Controls.Add(_lblZoom);
        bar.Controls.Add(hint);
        bar.Controls.Add(_btnTop);
        bar.Controls.Add(_btnUp);

        _legend = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(4), S(2), S(4), S(2)), WrapContents = true };
        BuildLegend();

        _split.Panel2.Controls.Add(_map);
        _split.Panel2.Controls.Add(bar);
        _split.Panel2.Controls.Add(_legend);
    }

    void BuildLegend()
    {
        int sw = S(14);
        var tip = new ToolTip();
        void Add(string name, Color c, int category)
        {
            var l = new Label
            {
                Text = "     " + name, Image = Icons.Swatch(sw, c), ImageAlign = ContentAlignment.MiddleLeft,
                AutoSize = true, Margin = new Padding(0, S(2), S(10), S(2)), Cursor = category >= 0 ? Cursors.Hand : Cursors.Default,
            };
            if (category >= 0)
            {
                tip.SetToolTip(l, "Click to highlight this category in the map (click again to clear)");
                l.Click += (_, _) => ToggleCategoryHighlight(category);
            }
            _legend.Controls.Add(l);
        }
        for (int i = 1; i < FileTypes.Categories.Length; i++) Add(FileTypes.Categories[i].Name, FileTypes.Categories[i].Color, i);
        Add(FileTypes.Categories[0].Name, FileTypes.Categories[0].Color, 0);
        Add("Unaccounted", FileTypes.UnaccountedColor, -1);
        Add("Many small items", FileTypes.RestColor, -1);
    }

    void BuildFilesTab()
    {
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(34), Padding = new Padding(S(4), S(4), S(4), 0), WrapContents = false };
        Label L(string t) => new Label { Text = t, AutoSize = true, Margin = new Padding(S(6), S(6), S(2), 0) };
        _numMinMB = new NumericUpDown { Minimum = 0, Maximum = 10_000_000, DecimalPlaces = 0, Width = S(80), Value = (decimal)Math.Clamp(_settings.FilesMinMB, 0, 10_000_000) };
        _numOlderDays = new NumericUpDown { Minimum = 0, Maximum = 36500, Width = S(70), Value = Math.Clamp(_settings.FilesOlderDays, 0, 36500) };
        _numMaxCount = new NumericUpDown { Minimum = 10, Maximum = 100000, Increment = 100, Width = S(80), Value = 1000 };
        _txtFilter = new TextBox { Width = S(200), PlaceholderText = "e.g. .iso  or  *.mkv  or  \\Downloads\\" };
        var btn = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(S(8), S(1), 0, 0) };
        _lblFilesInfo = new Label { AutoSize = true, Margin = new Padding(S(10), S(6), 0, 0), ForeColor = SystemColors.GrayText };
        filters.Controls.AddRange(new Control[]
        {
            L("Min size (MB):"), _numMinMB, L("Not modified for (days, 0 = any):"), _numOlderDays,
            L("Name / path contains:"), _txtFilter, L("Show up to:"), _numMaxCount, btn, _lblFilesInfo,
        });
        _numMinMB.ValueChanged += (_, _) => RestartFilterDebounce();
        _numOlderDays.ValueChanged += (_, _) => RestartFilterDebounce();
        _numMaxCount.ValueChanged += (_, _) => RestartFilterDebounce();
        _txtFilter.TextChanged += (_, _) => RestartFilterDebounce();
        btn.Click += (_, _) => RefreshFiles();

        _lvFiles = new VList<FileNode>(
            new("Size", 90, f => Fmt.Size(f.Value), (a, b) => a.Value.CompareTo(b.Value), true),
            new("Name", 300, f => f.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)),
            new("Last modified", 125, f => Fmt.Date(f.Modified), (a, b) => a.Modified.CompareTo(b.Modified)),
            new("Age", 100, f => Fmt.Age(f.Modified), (a, b) => b.Modified.CompareTo(a.Modified)),
            new("Type", 120, f => FileTypes.Categories[FileTypes.CategoryOf(f.Name)].Name, (a, b) => FileTypes.CategoryOf(a.Name).CompareTo(FileTypes.CategoryOf(b.Name))),
            new("Folder", 600, f => f.Parent?.FullPath ?? "", (a, b) => string.Compare(a.Parent?.FullPath, b.Parent?.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, SmallImageList = Icons.CategoryImages(S(16)),
            ImageOf = f => FileTypes.CategoryOf(f.Name),
        };
        _lvFiles.SelectedIndexChanged += (_, _) => { var f = _lvFiles.Selected; if (f != null) { _map.Selected = f; ShowNodeInStatus(f); } };
        _lvFiles.DoubleClick += (_, _) => { var f = _lvFiles.Selected; if (f != null) Shell.ShowInExplorer(f.FullPath); };
        _lvFiles.KeyDown += (_, e) => HandleItemKeys(e, _lvFiles.SelectedAll().Cast<Node>().ToList());
        _lvFiles.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowContext(_lvFiles.SelectedAll().Cast<Node>().ToList(), _lvFiles, e.Location); };
        _tabFiles.Controls.Add(_lvFiles);
        _tabFiles.Controls.Add(filters);
    }

    void BuildTypesTab()
    {
        long Total() => _lvTypes.Data.Sum(x => x.Size);
        _lvTypes = new VList<ExtStat>(
            new("Extension", 140, e => e.Ext, (a, b) => string.Compare(a.Ext, b.Ext, StringComparison.OrdinalIgnoreCase)),
            new("Category", 150, e => FileTypes.Categories[e.Category].Name, (a, b) => a.Category.CompareTo(b.Category)),
            new("Total size", 100, e => Fmt.Size(e.Size), (a, b) => a.Size.CompareTo(b.Size), true),
            new("% of files", 80, e => { long t = Total(); return t > 0 ? (100.0 * e.Size / t).ToString("0.00") + "%" : ""; }, (a, b) => a.Size.CompareTo(b.Size), true),
            new("Files", 100, e => Fmt.Count(e.Count), (a, b) => a.Count.CompareTo(b.Count), true),
            new("Average size", 100, e => Fmt.Size(e.Count > 0 ? e.Size / e.Count : 0), (a, b) => (a.Count > 0 ? a.Size / a.Count : 0).CompareTo(b.Count > 0 ? b.Size / b.Count : 0), true))
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, MultiSelect = false, SmallImageList = Icons.CategoryImages(S(16)),
            ImageOf = e => e.Category,
        };
        _lvTypes.SelectedIndexChanged += (_, _) =>
        {
            var e = _lvTypes.Selected;
            if (e == null) return;
            _highlightCategory = -1;
            string ext = e.Ext;
            _map.Highlight = ext == "(no extension)"
                ? n => n is FileNode && FileTypes.ExtSpan(n.Name).IsEmpty
                : n => n is FileNode && n.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        };
        var tip = new Label { Dock = DockStyle.Top, Height = S(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(6), 0, 0, 0), ForeColor = SystemColors.GrayText, Text = "Select an extension to highlight those files in the map. Click a legend entry below the map to highlight a whole category." };
        var clear = new LinkLabel { Text = "Clear highlight", Dock = DockStyle.Right, Width = S(110), TextAlign = ContentAlignment.MiddleRight };
        clear.LinkClicked += (_, _) => ClearHighlight();
        tip.Controls.Add(clear);
        _tabTypes.Controls.Add(_lvTypes);
        _tabTypes.Controls.Add(tip);
    }

    void BuildHogsTab()
    {
        _lvHogs = new VList<HogHit>(
            new("Size", 90, h => Fmt.Size(h.Value), (a, b) => a.Value.CompareTo(b.Value), true),
            new("What it is", 230, h => h.Rule.Title, (a, b) => string.Compare(a.Rule.Title, b.Rule.Title, StringComparison.CurrentCultureIgnoreCase)),
            new("Verdict", 140, h => KnownHogs.SafetyText(h.Rule.Safety), (a, b) => a.Rule.Safety.CompareTo(b.Rule.Safety)),
            new("Location", 380, h => h.Node.FullPath, (a, b) => string.Compare(a.Node.FullPath, b.Node.FullPath, StringComparison.OrdinalIgnoreCase)),
            new("Advice", 700, h => h.Rule.Advice))
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, MultiSelect = false,
            RowColor = h => KnownHogs.SafetyColor(h.Rule.Safety),
        };
        _lvHogs.SelectedIndexChanged += (_, _) => { var h = _lvHogs.Selected; if (h != null) { _map.Selected = h.Node; ShowNodeInStatus(h.Node); } };
        _lvHogs.DoubleClick += (_, _) => { var h = _lvHogs.Selected; if (h != null) RevealInTree(h.Node); };
        _lvHogs.KeyDown += (_, e) => HandleItemKeys(e, _lvHogs.Selected is { } h ? new List<Node> { h.Node } : new List<Node>());
        _lvHogs.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && _lvHogs.Selected is { } h) ShowContext(new List<Node> { h.Node }, _lvHogs, e.Location); };
        var tip = new Label { Dock = DockStyle.Top, Height = S(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(6), 0, 0, 0), ForeColor = SystemColors.GrayText, Text = "Common places where Windows and apps pile up data. Green = safe to clean, yellow = review first, red = don't delete by hand. Double-click to show in the folder tree." };
        _tabHogs.Controls.Add(_lvHogs);
        _tabHogs.Controls.Add(tip);
    }

    void BuildErrorsTab()
    {
        _lvErrors = new VList<ScanError>(
            new("Folder", 650, e => e.Path, (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)),
            new("Problem", 400, e => e.Message, (a, b) => a.Code.CompareTo(b.Code)))
        { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        _lvErrors.DoubleClick += (_, _) =>
        {
            var e = _lvErrors.Selected;
            var n = e == null ? null : TreeOps.FindByPath(_scanner.Roots, e.Path);
            if (n != null) RevealInTree(n);
        };
        _lblErrorsTip = new Label
        {
            Dock = DockStyle.Top, Height = S(24), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(6), 0, 0, 0), ForeColor = SystemColors.GrayText,
            Text = _isAdmin ? "Folders that couldn't be read even as administrator (usually harmless system folders)."
                            : "Folders Windows wouldn't let us read. Their space shows up as 'Unaccounted'. Use 'Restart as admin' to read most of them.",
        };
        var retry = new LinkLabel { Text = "Retry unreadable folders", Dock = DockStyle.Right, Width = S(170), TextAlign = ContentAlignment.MiddleRight };
        retry.LinkClicked += (_, _) =>
        {
            if (_scanner.IsActive) return;
            if (RetryUnreadable(startScan: true) == 0)
                MessageBox.Show(this, "There are no unreadable folders to retry.", "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        _lblErrorsTip.Controls.Add(retry);
        _tabErrors.Controls.Add(_lvErrors);
        _tabErrors.Controls.Add(_lblErrorsTip);
    }

    void BuildStatus()
    {
        _status = new StatusStrip { SizingGrip = true };
        ToolStripStatusLabel L(int w) => new ToolStripStatusLabel { AutoSize = false, Width = S(w), TextAlign = ContentAlignment.MiddleLeft, BorderSides = ToolStripStatusLabelBorderSides.Right };
        _stState = L(150);
        _stFiles = L(190);
        _stSize = L(120);
        _stRate = L(120);
        _stTime = L(110);
        _stErrors = L(130);
        _stErrors.IsLink = true;
        _stErrors.Click += (_, _) => _tabs.SelectedTab = _tabErrors;
        _stProgress = new ToolStripProgressBar { Width = S(160), Maximum = 1000, Visible = false };
        _stPath = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _status.Items.AddRange(new ToolStripItem[] { _stState, _stFiles, _stSize, _stRate, _stTime, _stErrors, _stProgress, _stPath });
    }

    void BuildContextMenu()
    {
        _ctx = new ContextMenuStrip();
        _miOpen = new ToolStripMenuItem("Open");
        _miExplorer = new ToolStripMenuItem("Show in Explorer") { Font = new Font(_ctx.Font, FontStyle.Bold) };
        _miCopy = new ToolStripMenuItem("Copy path", null, null, Keys.Control | Keys.C);
        _miShowTree = new ToolStripMenuItem("Show in folder tree");
        _miZoom = new ToolStripMenuItem("Zoom map here");
        _miRescan = new ToolStripMenuItem("Rescan this folder");
        _miProps = new ToolStripMenuItem("Properties");
        _miRecycle = new ToolStripMenuItem("Move to Recycle Bin…", null, null, Keys.Delete);
        _miDelete = new ToolStripMenuItem("Delete permanently…", null, null, Keys.Shift | Keys.Delete);
        _ctx.Items.AddRange(new ToolStripItem[]
        {
            _miExplorer, _miOpen, _miCopy, new ToolStripSeparator(),
            _miShowTree, _miZoom, _miRescan, new ToolStripSeparator(),
            _miProps, new ToolStripSeparator(), _miRecycle, _miDelete,
        });
        _miOpen.Click += (_, _) => { if (First is { } n) Shell.Open(n.FullPath); };
        _miExplorer.Click += (_, _) => { if (First is { } n) Shell.ShowInExplorer(n.FullPath); };
        _miCopy.Click += (_, _) => CopyPaths(_ctxNodes);
        _miShowTree.Click += (_, _) => { if (First is { } n) RevealInTree(n); };
        _miZoom.Click += (_, _) => { if (First is { } n) _map.SetZoom(n as DirNode ?? n.Parent); };
        _miRescan.Click += (_, _) => { if (First is DirNode d) RescanFolder(d); };
        _miProps.Click += (_, _) => { if (First is { } n) Shell.ShowProperties(n.FullPath, Handle); };
        _miRecycle.Click += (_, _) => DeleteNodes(_ctxNodes, true);
        _miDelete.Click += (_, _) => DeleteNodes(_ctxNodes, false);
    }

    Node First => _ctxNodes.Count > 0 ? _ctxNodes[0] : null;
}
