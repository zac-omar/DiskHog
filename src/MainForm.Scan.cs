using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace DiskHog;

sealed partial class MainForm
{
    string _lastReportPath;
    int _tick;
    long _rateFiles;
    DateTime _rateTime = DateTime.Now;
    double _rate;
    bool _busy;
    bool _isRescan;
    bool _skipClosePrompt;
    int _pendingCount;
    Bitmap _icoPlay, _icoPause;

    static readonly string HandoffFile = Path.Combine(Path.GetTempPath(), "DiskHog_handoff.dhog");

    long TotalFiles() => _scanner.Roots.Sum(r => Volatile.Read(ref r.FileCount));
    long TotalDirs() => _scanner.Roots.Sum(r => Volatile.Read(ref r.DirCount));
    long TotalFound() => _scanner.Roots.Sum(r => r.Value - (r.UnaccountedNode?.Value ?? 0));

    async void OnLoadForm(object sender, EventArgs e)
    {
        int h = _split.Height;
        _split.Panel1MinSize = S(120);
        _split.Panel2MinSize = S(120);
        _split.SplitterDistance = _settings.SplitTop > S(120) && _settings.SplitTop < h - S(120) ? _settings.SplitTop : (int)(h * 0.45);
        _icoPlay = Icons.Play(S(16));
        _icoPause = Icons.Pause(S(16));
        PopulateDrives();
        _uiTimer.Start();
        UpdateStatusIdle("Ready");
        _stPath.Text = _isAdmin ? "Tick drives / folders above and press Scan." : "Tick drives / folders above and press Scan.  Tip: 'Restart as admin' lets DiskHog read protected folders too.";

        if (_startupOpen != null && File.Exists(_startupOpen))
        {
            bool ok = await LoadSessionAsync(_startupOpen);
            bool handoff = string.Equals(Path.GetFullPath(_startupOpen), Path.GetFullPath(HandoffFile), StringComparison.OrdinalIgnoreCase);
            if (handoff) { try { File.Delete(_startupOpen); } catch { } }
            if (ok)
            {
                int retried = _isAdmin && handoff ? RetryUnreadable(startScan: false) : 0;
                if (_pendingCount > 0 && (_startupContinue || retried > 0)) ContinueScan();
            }
        }
    }

    // ---------------- drive list ----------------

    HashSet<string> CheckedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem it in _lvDrives.Items)
            if (it.Checked && it.Tag is string p) set.Add(p);
        return set;
    }

    async void PopulateDrives()
    {
        var keep = _lvDrives.Items.Count > 0 ? CheckedPaths() : new HashSet<string>(_settings.CheckedRoots, StringComparer.OrdinalIgnoreCase);
        bool firstRun = keep.Count == 0 && _settings.CheckedRoots.Count == 0;
        _lvDrives.Items.Clear();
        _lvDrives.Items.Add(new ListViewItem("Reading drives…"));

        var infos = await Task.Run(() =>
        {
            var list = new List<(string Path, string Label, string Type, string Fs, long Total, long Free)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string type = d.DriveType switch
                    {
                        DriveType.Fixed => Native.HasSeekPenalty(d.Name) switch { true => "HDD", false => "SSD", _ => "Fixed" },
                        DriveType.Removable => "Removable",
                        DriveType.Network => "Network",
                        DriveType.CDRom => "CD/DVD",
                        DriveType.Ram => "RAM disk",
                        _ => d.DriveType.ToString(),
                    };
                    list.Add((d.Name, d.VolumeLabel, type, d.DriveFormat, d.TotalSize, d.TotalFreeSpace));
                }
                catch { }
            }
            return list;
        });

        string sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        _lvDrives.BeginUpdate();
        _lvDrives.Items.Clear();
        foreach (var d in infos)
        {
            long used = d.Total - d.Free;
            double pct = d.Total > 0 ? 100.0 * used / d.Total : 0;
            var it = new ListViewItem(new[]
            {
                d.Path, d.Label, d.Type, d.Fs, Fmt.Size(d.Total), Fmt.Size(used), Fmt.Size(d.Free), $"{pct:0}%",
            }) { Tag = d.Path };
            if (pct >= 90) it.ForeColor = Color.Firebrick;
            it.Checked = keep.Count > 0 ? keep.Contains(d.Path) : firstRun && string.Equals(d.Path, sysDrive, StringComparison.OrdinalIgnoreCase);
            _lvDrives.Items.Add(it);
        }
        foreach (var f in _settings.ExtraFolders)
        {
            var it = new ListViewItem(new[] { f, "", "Folder", "", "", "", "", "" }) { Tag = f, Checked = keep.Contains(f) };
            _lvDrives.Items.Add(it);
        }
        _lvDrives.EndUpdate();
    }

    void AddFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Pick a folder to scan", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string p = TreeOps.NormalizeRoot(dlg.SelectedPath);
        foreach (ListViewItem existing in _lvDrives.Items)
            if (string.Equals(existing.Tag as string, p, StringComparison.OrdinalIgnoreCase)) { existing.Checked = true; return; }
        _settings.ExtraFolders.Add(p);
        _lvDrives.Items.Add(new ListViewItem(new[] { p, "", "Folder", "", "", "", "", "" }) { Tag = p, Checked = true });
    }

    void RemoveFolder()
    {
        if (_lvDrives.SelectedItems.Count == 0 || _lvDrives.SelectedItems[0].Tag is not string p) return;
        if (!_settings.ExtraFolders.Remove(p))
        {
            MessageBox.Show(this, "Drives can't be removed. Just untick them.", "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _lvDrives.Items.Remove(_lvDrives.SelectedItems[0]);
    }

    static bool IsUnder(string child, string parent)
    {
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
        string p = parent.EndsWith('\\') ? parent : parent + "\\";
        return child.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- scan control ----------------

    async void StartNewScan()
    {
        if (_scanner.IsActive || _busy) return;
        var chosen = CheckedPaths().Select(TreeOps.NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one drive or folder in the list at the top first.", "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var final = new List<string>();
        foreach (var p in chosen)
        {
            if (final.Any(f => IsUnder(p, f))) continue; // already covered by a parent we're scanning
            if (!Directory.Exists(p))
            {
                MessageBox.Show(this, $"Can't find {p} - skipping it.", "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            final.Add(p);
        }
        if (final.Count == 0) return;
        if (_scanner.Roots.Count > 0 &&
            MessageBox.Show(this, "Start a new scan? The current results will be replaced.\n(Use 'Save results' first if you want to keep them.)",
                "DiskHog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        _settings.CheckedRoots = CheckedPaths().ToList();
        _busy = true;
        _stState.Text = "Preparing…";
        UpdateButtons();
        List<RootNode> roots;
        try { roots = await Task.Run(() => final.Select(TreeOps.CreateRoot).ToList()); }
        finally { _busy = false; }

        _scanner.Roots.Clear();
        _scanner.Roots.AddRange(roots);
        _scanner.ResetStats();
        _isRescan = false;
        _pendingCount = 0;
        ClearHighlight();
        AttachViews();
        _rateFiles = 0;
        _rateTime = DateTime.Now;
        _scanner.Start(roots, _settings);
        UpdateButtons();
    }

    void PauseResumeOrContinue()
    {
        switch (_scanner.State)
        {
            case ScanState.Running: _scanner.Pause(); break;
            case ScanState.Paused: _scanner.Resume(); break;
            case ScanState.Idle: ContinueScan(); break;
        }
        UpdateButtons();
        OnUiTick();
    }

    void ContinueScan()
    {
        if (_scanner.IsActive || _busy) return;
        var pending = _scanner.CollectPending();
        if (pending.Count == 0)
        {
            MessageBox.Show(this, "Nothing left to scan - this scan is complete.", "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var r in _scanner.Roots) TreeOps.RemoveUnaccounted(r);
        _isRescan = false;
        _rateFiles = TotalFiles();
        _rateTime = DateTime.Now;
        _scanner.Start(pending, _settings);
        UpdateButtons();
    }

    void StopScan()
    {
        _scanner.Stop();
        UpdateButtons();
    }

    /// <summary>Turns unreadable folders back into pending ones (e.g. after becoming admin). Returns how many.</summary>
    int RetryUnreadable(bool startScan)
    {
        if (_scanner.IsActive) return 0;
        int n = 0;
        TreeWalk.ForEach(_scanner.Roots, x =>
        {
            if (x is DirNode d && d.State == DirState.Error) { d.State = DirState.Pending; d.ErrorCode = 0; n++; }
        });
        if (n == 0) return 0;
        _scanner.ClearErrors();
        _pendingCount = _scanner.CollectPending().Count;
        RefreshErrors();
        if (startScan) ContinueScan();
        UpdateButtons();
        return n;
    }

    void RescanFolder(DirNode d)
    {
        if (_scanner.IsActive || d.IsVirtual) return;
        _isRescan = true;
        _scanner.RemoveErrorsUnder(d.FullPath);
        TreeOps.PrepareRescan(d);
        _tree.Rebuild();
        _map.MarkDirty();
        _rateFiles = TotalFiles();
        _rateTime = DateTime.Now;
        _scanner.Start(new[] { d }, _settings);
        UpdateButtons();
    }

    void OnScanFinished()
    {
        foreach (var r in _scanner.Roots)
        {
            TreeOps.RefreshVolume(r);
            TreeOps.UpdateUnaccounted(r);
        }
        _pendingCount = _scanner.CollectPending().Count;
        Display.Invalidate();
        _tree.Rebuild();
        _map.MarkDirty();
        _lblZoom.Text = _map.ZoomText;
        MarkTabsDirty();
        RefreshActiveTab();
        UpdateButtons();

        bool complete = _pendingCount == 0;
        UpdateStatusIdle(complete ? "Done" : "Stopped");
        if (!complete)
            _stPath.Text = $"Stopped with {_pendingCount:N0} folders left. Press Continue to finish, or 'Save results' to continue another time.";
        else if (_scanner.ErrorCount > 0 && !_isAdmin)
            _stPath.Text = $"Done. {_scanner.ErrorCount:N0} folders couldn't be read - 'Restart as admin' to include them.";
        else
            _stPath.Text = "Done.";

        if (complete && !_isRescan)
        {
            if (_scanner.Elapsed.TotalSeconds > 15) System.Media.SystemSounds.Asterisk.Play();
            if (_settings.AutoSaveReport) AutoReport();
        }
        _isRescan = false;
    }

    async void AutoReport()
    {
        try
        {
            string path = Path.Combine(_settings.ResolvedReportFolder, ReportWriter.DefaultBaseName() + ".md");
            await Task.Run(() => ReportWriter.Write(path, _scanner, _settings, _settings.WriteCsv));
            _lastReportPath = path;
            _miOpenLastReport.Enabled = true;
            _stPath.Text += "   Report saved: " + path;
        }
        catch (Exception ex) { _stPath.Text += "   (Couldn't save the auto report: " + ex.Message + ")"; }
    }

    void UpdateButtons()
    {
        var st = _scanner.State;
        bool active = st != ScanState.Idle;
        bool hasResults = _scanner.Roots.Count > 0;
        _btnScan.Enabled = !active && !_busy;
        _btnPause.Enabled = !_busy && (active ? st != ScanState.Stopping : _pendingCount > 0);
        _btnPause.Text = st == ScanState.Running ? "Pause" : st == ScanState.Paused ? "Resume" : "Continue";
        _btnPause.Image = st == ScanState.Running ? _icoPause ?? Icons.Pause(S(16)) : _icoPlay ?? Icons.Play(S(16));
        _btnPause.ToolTipText = st == ScanState.Idle ? "Continue an unfinished scan" : "Pause / resume the scan (Ctrl+P)";
        _btnStop.Enabled = active && st != ScanState.Stopping;
        _btnSave.Enabled = hasResults && !_busy && st != ScanState.Stopping;
        _btnOpen.Enabled = !active && !_busy;
        _btnReport.Enabled = hasResults && !_busy;
        _lvDrives.Enabled = !active && !_busy;
    }

    void OnUiTick()
    {
        _tick++;
        var st = _scanner.State;
        if (st == ScanState.Idle) return;

        long files = TotalFiles();
        var now = DateTime.Now;
        double dt = (now - _rateTime).TotalSeconds;
        if (dt >= 1)
        {
            _rate = st == ScanState.Running ? Math.Max(0, (files - _rateFiles) / dt) : 0;
            _rateFiles = files;
            _rateTime = now;
        }

        _stState.Text = st switch { ScanState.Running => "Scanning…", ScanState.Paused => "Paused", _ => "Stopping…" };
        _stFiles.Text = $"{files:N0} files, {TotalDirs():N0} folders";
        _stSize.Text = Fmt.Size(TotalFound());
        _stRate.Text = st == ScanState.Running ? $"{_rate:N0} files/s" : "";
        _stErrors.Text = _scanner.ErrorCount > 0 ? $"{_scanner.ErrorCount:N0} unreadable" : "";

        // Progress estimate for whole drives: allocated bytes found vs. used space Windows reports.
        long used = 0, found = 0;
        foreach (var r in _scanner.Roots)
            if (r.IsDriveRoot && r.VolumeTotal > 0) { used += r.VolumeUsed; found += Volatile.Read(ref r.Alloc); }
        string time = Fmt.Duration(_scanner.Elapsed);
        if (used > 0 && !_isRescan)
        {
            double pct = Math.Clamp((double)found / used, 0, 1);
            _stProgress.Style = ProgressBarStyle.Continuous;
            _stProgress.Value = (int)(pct * 1000);
            _stProgress.ToolTipText = $"~{pct * 100:0}% of used space found";
            if (st == ScanState.Running && pct > 0.05 && pct < 0.99)
            {
                var left = TimeSpan.FromSeconds(_scanner.Elapsed.TotalSeconds * (1 - pct) / pct);
                time += $"  (~{Fmt.Duration(left)} left)";
            }
        }
        else _stProgress.Style = ProgressBarStyle.Marquee;
        _stProgress.Visible = true;
        _stTime.Text = time;
        if (_scanner.CurrentDir is { } cd && st == ScanState.Running) _stPath.Text = cd.FullPath;
        else if (st == ScanState.Paused) _stPath.Text = "Paused - press Resume to carry on, or 'Save results' to continue another time.";

        if (st == ScanState.Running && _tick % 8 == 0)
        {
            Display.Invalidate();
            _tree.Rebuild();
            _map.MarkDirty();
            _lblZoom.Text = _map.ZoomText;
            if (_tabs.SelectedTab == _tabErrors) RefreshErrors();
        }
        if (st == ScanState.Running && _tick % 24 == 0 && _tabs.SelectedTab != _tabTree && _tabs.SelectedTab != _tabErrors)
        {
            MarkTabsDirty();
            RefreshActiveTab();
        }
    }

    void UpdateStatusIdle(string state)
    {
        _stState.Text = state;
        _stFiles.Text = _scanner.Roots.Count > 0 ? $"{TotalFiles():N0} files, {TotalDirs():N0} folders" : "";
        _stSize.Text = _scanner.Roots.Count > 0 ? Fmt.Size(TotalFound()) : "";
        _stRate.Text = "";
        _stTime.Text = _scanner.Roots.Count > 0 ? Fmt.Duration(_scanner.Elapsed) : "";
        _stErrors.Text = _scanner.ErrorCount > 0 ? $"{_scanner.ErrorCount:N0} unreadable" : "";
        _stProgress.Visible = false;
    }

    // ---------------- sessions ----------------

    async Task<bool> SaveSessionAsync(string file)
    {
        if (_scanner.State == ScanState.Running) _scanner.Pause();
        UpdateButtons();
        _busy = true;
        UseWaitCursor = true;
        UpdateButtons();
        try
        {
            await Task.Run(() => _scanner.WaitQuiescent(60000));
            _stPath.Text = "Saving results…";
            await Task.Run(() => SessionIO.Save(file, _scanner));
            _stPath.Text = "Saved results to " + file + (_scanner.State == ScanState.Paused ? "   (scan is paused - press Resume to carry on)" : "");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't save results:\n" + ex.Message, "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            UpdateButtons();
        }
    }

    string AskSavePath()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "DiskHog results (*.dhog)|*.dhog",
            FileName = "DiskHog_" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".dhog",
            InitialDirectory = AppSettings.AppDir,
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    async void SaveSession()
    {
        if (_scanner.Roots.Count == 0) return;
        bool wasRunning = _scanner.State == ScanState.Running;
        if (wasRunning) { _scanner.Pause(); UpdateButtons(); }
        string file = AskSavePath();
        if (file == null) { if (wasRunning) { _scanner.Resume(); UpdateButtons(); } return; }
        await SaveSessionAsync(file);
    }

    async void OpenSession()
    {
        if (_scanner.IsActive || _busy) return;
        using var dlg = new OpenFileDialog { Filter = "DiskHog results (*.dhog)|*.dhog|All files|*.*", InitialDirectory = AppSettings.AppDir };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (_scanner.Roots.Count > 0 &&
            MessageBox.Show(this, "Replace the current results with the saved ones?", "DiskHog", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        await LoadSessionAsync(dlg.FileName);
    }

    async Task<bool> LoadSessionAsync(string file)
    {
        _busy = true;
        UseWaitCursor = true;
        UpdateButtons();
        _stPath.Text = "Loading " + file + "…";
        try
        {
            var loaded = await Task.Run(() => SessionIO.Load(file));
            _scanner.Roots.Clear();
            _scanner.Roots.AddRange(loaded.Roots);
            _scanner.RestoreStats(loaded.Files, loaded.Dirs, loaded.Bytes, loaded.Elapsed, loaded.Errors);
            _pendingCount = _scanner.CollectPending().Count;
            ClearHighlight();
            AttachViews();
            UpdateStatusIdle("Loaded");
            _stPath.Text = _pendingCount > 0
                ? $"Loaded an unfinished scan ({_pendingCount:N0} folders left). Press Continue to finish it."
                : $"Loaded results saved {loaded.Saved:yyyy-MM-dd HH:mm}. Rescan folders (right-click) to refresh them.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open results:\n" + ex.Message, "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            UpdateButtons();
        }
    }

    /// <summary>Points every view at the scanner's current roots.</summary>
    void AttachViews()
    {
        Display.Invalidate();
        _tree.SetRoots(_scanner.Roots);
        _map.SetRoots(_scanner.Roots);
        _lvFiles.SetItems(new List<FileNode>());
        _lvTypes.SetItems(new List<ExtStat>());
        _lvHogs.SetItems(new List<HogHit>());
        _lvErrors.SetItems(new List<ScanError>());
        MarkTabsDirty();
        RefreshActiveTab();
    }

    // ---------------- admin / options / closing ----------------

    async void RestartAsAdmin()
    {
        if (_busy) return;
        bool wasActive = _scanner.IsActive;
        if (_scanner.Roots.Count > 0)
        {
            if (!await SaveSessionAsync(HandoffFile)) return;
        }
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        if (_scanner.Roots.Count > 0) psi.Arguments = $"--open \"{HandoffFile}\"" + (wasActive ? " --continue" : "");
        try { Process.Start(psi); }
        catch (Win32Exception)
        {
            // UAC prompt was declined: carry on as we were.
            if (wasActive && _scanner.State == ScanState.Paused) _scanner.Resume();
            UpdateButtons();
            return;
        }
        _skipClosePrompt = true;
        _scanner.Stop();
        Close();
    }

    void ShowOptions()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
        using var f = new Form
        {
            Text = "DiskHog options", Size = new Size(S(560), S(560)), StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font, AutoScaleMode = AutoScaleMode.None,
        };
        var grid = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = copy, ToolbarVisible = false, PropertySort = PropertySort.Categorized };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(42), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(6)) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = S(90), Height = S(28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = S(90), Height = S(28) };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);
        f.Controls.Add(grid);
        f.Controls.Add(bottom);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        copy.Clamp();
        copy.ShowLogicalSize = _settings.ShowLogicalSize;
        copy.CheckedRoots = _settings.CheckedRoots;
        copy.ExtraFolders = _settings.ExtraFolders;
        foreach (var p in typeof(AppSettings).GetProperties())
            if (p.CanWrite && p.GetCustomAttributes(typeof(BrowsableAttribute), false).Length == 0)
                p.SetValue(_settings, p.GetValue(copy));
        _settings.Save();
    }

    void ShowHelp()
    {
        MessageBox.Show(this,
            "DiskHog - find what's eating your disk\n\n" +
            "1. Tick drives (or add folders) at the top and press Scan. Several drives scan in parallel.\n" +
            "2. Pause / Resume any time. Stop keeps what was found; Continue finishes it later.\n" +
            "    'Save results' stores the scan (even unfinished) so you can reopen or continue it another day.\n" +
            "3. Folders tab: everything sorted by size. Click column headers to sort by other things.\n" +
            "    'Newest change inside' helps spot forgotten stuff (big folder, nothing touched in years).\n" +
            "4. The map: each block is a file, area = size, color = type. Double-click zooms in.\n" +
            "5. Largest files: filter by size, age and name (wildcards OK, e.g. *.iso).\n" +
            "6. Known space hogs: temp folders, caches, update leftovers, etc. with advice on what's safe.\n" +
            "7. Export report writes a Markdown file you can read or paste into an AI chat. One is also\n" +
            "    auto-saved after each scan into the 'reports' folder (toggle in Options).\n\n" +
            "Right-click anything for: Show in Explorer, Copy path, Rescan, Properties, Recycle / Delete.\n" +
            "Keys: F5 scan · Ctrl+P pause · Ctrl+S save · Ctrl+O open · Ctrl+E report · Del recycle · Shift+Del delete · Ctrl+C copy path\n\n" +
            "'Size on disk' is what you actually get back by deleting (cloud-only OneDrive files use ~0).\n" +
            "'Unaccounted' is used space no readable file explains (NTFS metadata, restore points, unreadable folders).\n" +
            "Hard-linked files (C:\\Windows\\WinSxS) are counted once so totals match reality.",
            "DiskHog help", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5: if (_btnScan.Enabled) StartNewScan(); return true;
            case Keys.Control | Keys.P: if (_btnPause.Enabled) PauseResumeOrContinue(); return true;
            case Keys.Control | Keys.S: if (_btnSave.Enabled) SaveSession(); return true;
            case Keys.Control | Keys.O: if (_btnOpen.Enabled) OpenSession(); return true;
            case Keys.Control | Keys.E: if (_btnReport.Enabled) ExportReport(false); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!_skipClosePrompt)
        {
            if (_busy) { e.Cancel = true; return; }
            if (_scanner.IsActive)
            {
                var r = MessageBox.Show(this,
                    "A scan is still running.\n\nYes = save the results so you can continue later, then exit\nNo = exit without saving\nCancel = keep going",
                    "DiskHog", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes) { e.Cancel = true; SaveThenClose(); return; }
            }
        }
        _scanner.Stop();
        SaveSettings();
    }

    async void SaveThenClose()
    {
        bool wasRunning = _scanner.State == ScanState.Running;
        if (wasRunning) _scanner.Pause();
        string file = AskSavePath();
        if (file == null) { if (wasRunning) _scanner.Resume(); UpdateButtons(); return; }
        if (await SaveSessionAsync(file)) { _skipClosePrompt = true; Close(); }
    }

    void SaveSettings()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WinX = b.X; _settings.WinY = b.Y; _settings.WinW = b.Width; _settings.WinH = b.Height;
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        _settings.SplitTop = _split.SplitterDistance;
        _settings.ShowLogicalSize = !Display.OnDisk;
        if (_lvDrives.Items.Count > 0 && _lvDrives.Items[0].Tag != null) _settings.CheckedRoots = CheckedPaths().ToList();
        _settings.FilesMinMB = (double)_numMinMB.Value;
        _settings.FilesOlderDays = (int)_numOlderDays.Value;
        _settings.Save();
    }
}
