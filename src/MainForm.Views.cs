namespace DiskHog;

sealed partial class MainForm
{
    bool _filesDirty = true, _typesDirty = true, _hogsDirty = true;
    CancellationTokenSource _filesCts, _typesCts;
    int _highlightCategory = -1;

    void MarkTabsDirty() => _filesDirty = _typesDirty = _hogsDirty = true;

    void RefreshActiveTab()
    {
        var t = _tabs.SelectedTab;
        if (t == _tabFiles && _filesDirty) RefreshFiles();
        else if (t == _tabTypes && _typesDirty) RefreshTypes();
        else if (t == _tabHogs && _hogsDirty) RefreshHogs();
        else if (t == _tabErrors) RefreshErrors();
    }

    void RestartFilterDebounce()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    async void RefreshFiles()
    {
        _filesCts?.Cancel();
        var cts = _filesCts = new CancellationTokenSource();
        if (_scanner.Roots.Count == 0) { _lvFiles.SetItems(new List<FileNode>()); _lblFilesInfo.Text = ""; return; }
        long min = (long)(_numMinMB.Value * 1024 * 1024);
        int days = (int)_numOlderDays.Value;
        int max = (int)_numMaxCount.Value;
        long older = days > 0 ? DateTime.UtcNow.AddDays(-days).ToFileTimeUtc() : 0;
        var match = Analysis.NameMatcher(_txtFilter.Text);
        var roots = _scanner.Roots.ToArray();
        _lblFilesInfo.Text = "Searching…";
        try
        {
            var list = await Task.Run(() => Analysis.TopFiles(roots, max, min, older, match, cts.Token));
            if (cts.IsCancellationRequested) return;
            _lvFiles.SetItems(list);
            _lblFilesInfo.Text = $"{list.Count:N0} files, {Fmt.Size(list.Sum(f => f.Value))} together" +
                                 (list.Count >= max ? "  (limit reached - raise 'Show up to' or the min size)" : "");
            _filesDirty = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _lblFilesInfo.Text = ex.Message; }
    }

    async void RefreshTypes()
    {
        _typesCts?.Cancel();
        var cts = _typesCts = new CancellationTokenSource();
        var roots = _scanner.Roots.ToArray();
        try
        {
            var list = await Task.Run(() => Analysis.Extensions(roots, cts.Token));
            if (cts.IsCancellationRequested) return;
            _lvTypes.SetItems(list);
            _typesDirty = false;
        }
        catch (OperationCanceledException) { }
    }

    async void RefreshHogs()
    {
        var roots = _scanner.Roots.ToArray();
        var list = await Task.Run(() => KnownHogs.Find(roots));
        _lvHogs.SetItems(list);
        _hogsDirty = false;
    }

    void RefreshErrors()
    {
        if (_lvErrors.Data.Count != _scanner.Errors.Count) _lvErrors.SetItems(_scanner.Errors.ToList());
        _tabErrors.Text = _scanner.ErrorCount > 0 ? $"Unreadable folders ({_scanner.ErrorCount:N0})" : "Unreadable folders";
    }

    void SetMetric(bool onDisk)
    {
        if (Display.OnDisk == onDisk) return;
        Display.OnDisk = onDisk;
        _settings.ShowLogicalSize = !onDisk;
        Display.Invalidate();
        _tree.Rebuild();
        _map.MarkDirty();
        _lblZoom.Text = _map.ZoomText;
        MarkTabsDirty();
        RefreshActiveTab();
        if (!_scanner.IsActive && _scanner.Roots.Count > 0) _stSize.Text = Fmt.Size(TotalFound());
    }

    void ShowNodeInStatus(Node n)
    {
        if (n == null || _scanner.State == ScanState.Running) return;
        _stPath.Text = n.IsVirtual ? $"{n.Name}  —  {Fmt.Size(n.Value)}" : $"{n.FullPath}  —  {Fmt.Size(n.Value)}";
    }

    void RevealInTree(Node n)
    {
        if (n == null) return;
        _tabs.SelectedTab = _tabTree;
        _tree.SelectNode(n);
        _tree.Focus();
        _map.Selected = n;
    }

    void ToggleCategoryHighlight(int category)
    {
        if (_highlightCategory == category) { ClearHighlight(); return; }
        _highlightCategory = category;
        _map.Highlight = n => n is FileNode && !n.IsVirtual && FileTypes.CategoryOf(n.Name) == category;
    }

    void ClearHighlight()
    {
        _highlightCategory = -1;
        _lvTypes.SelectedIndices.Clear();
        _map.Highlight = null;
    }

    // ---------------- item actions ----------------

    void HandleItemKeys(KeyEventArgs e, List<Node> nodes)
    {
        if (nodes.Count == 0) return;
        if (e.KeyCode == Keys.Delete && !e.Control && !e.Alt)
        {
            DeleteNodes(nodes, !e.Shift);
            e.Handled = e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.C)
        {
            CopyPaths(nodes);
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    void ShowContext(List<Node> nodes, Control owner, Point at)
    {
        nodes = nodes.Where(n => n != null).ToList();
        if (nodes.Count == 0) return;
        _ctxNodes = nodes;
        var n = nodes[0];
        bool real = !n.IsVirtual;
        bool single = nodes.Count == 1;
        _miOpen.Enabled = real && single;
        _miExplorer.Enabled = real && single;
        _miCopy.Enabled = nodes.Any(x => !x.IsVirtual);
        _miShowTree.Enabled = single;
        _miZoom.Enabled = single && (n is DirNode || n.Parent != null);
        _miRescan.Enabled = single && real && n is DirNode && !_scanner.IsActive;
        _miProps.Enabled = single && real;
        bool canDelete = nodes.All(x => !x.IsVirtual && x.Parent != null);
        _miRecycle.Enabled = _miDelete.Enabled = canDelete;
        _miRecycle.Text = single ? "Move to Recycle Bin…" : $"Move {nodes.Count} items to Recycle Bin…";
        _miDelete.Text = single ? "Delete permanently…" : $"Delete {nodes.Count} items permanently…";
        _ctx.Show(owner, at);
    }

    void CopyPaths(List<Node> nodes)
    {
        var text = string.Join(Environment.NewLine, nodes.Where(n => !n.IsVirtual).Select(n => n.FullPath));
        if (text.Length > 0) { try { Clipboard.SetText(text); } catch { } }
    }

    static bool IsRiskyPath(Node n)
    {
        string p = n.FullPath;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] system =
        {
            win,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };
        if (system.Any(s => s.Length > 0 && (p.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase) || p.Equals(s, StringComparison.OrdinalIgnoreCase))))
            return true;
        if (n.Parent is RootNode { IsDriveRoot: true }) return true; // top-level folders / files of a drive (Users, pagefile.sys, ...)
        return false;
    }

    /// <summary>True if the node is still attached to one of the current roots.</summary>
    bool IsAttached(Node n)
    {
        for (Node c = n; c != null; c = c.Parent)
        {
            if (c.Parent == null) return c is RootNode r && _scanner.Roots.Contains(r);
            if (Array.IndexOf(c.Parent.GetChildren(), c) < 0) return false;
        }
        return false;
    }

    void DeleteNodes(List<Node> nodes, bool recycle)
    {
        nodes = nodes.Where(n => n != null && !n.IsVirtual && n.Parent != null).Distinct().ToList();
        nodes = nodes.Where(n => !nodes.Any(o => o != n && n.IsDescendantOf(o))).ToList();
        if (nodes.Count == 0) return;
        if (_busy) return;
        if (_scanner.IsActive && nodes.Any(n => n is DirNode d && TreeOps.HasPending(d)))
        {
            MessageBox.Show(this, "Part of this is still being scanned. Let the scan finish (or stop it) first.", "DiskHog",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        long total = nodes.Sum(n => n.Value);
        string list = string.Join("\n", nodes.Take(12).Select(n => $"   {Fmt.Size(n.Value),10}   {n.FullPath}"));
        if (nodes.Count > 12) list += $"\n   … and {nodes.Count - 12} more";
        bool risky = nodes.Any(IsRiskyPath);
        string msg = (recycle ? "Move to the Recycle Bin" : "PERMANENTLY delete") + $" {nodes.Count:N0} item(s), {Fmt.Size(total)}?\n\n{list}";
        msg += recycle ? "\n\nNote: the Recycle Bin keeps using disk space until you empty it." : "\n\nThis cannot be undone.";
        if (risky)
            msg += "\n\nWARNING: some of this is in a system / program location or at the top of a drive. Deleting it can break " +
                   "Windows or installed apps. Prefer uninstalling programs or Disk Cleanup for these.";
        if (MessageBox.Show(this, msg, "Confirm delete", MessageBoxButtons.YesNo,
                risky || !recycle ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var paths = nodes.Select(n => n.FullPath).ToList();
        int rc = Shell.Delete(paths, recycle, Handle, out bool aborted);

        int removed = 0;
        long freed = 0;
        var partial = new List<DirNode>();
        foreach (var n in nodes)
        {
            string p = n.FullPath;
            bool gone = n is DirNode ? !Directory.Exists(p) : !File.Exists(p);
            if (gone) { freed += n.Value; TreeOps.RemoveNode(n); removed++; }
            else if (n is DirNode d) partial.Add(d);
        }

        // Keep the map's zoom on something that still exists.
        var z = _map.EffectiveZoom;
        if (z != null && !IsAttached(z))
        {
            var p = z.Parent;
            while (p != null && !IsAttached(p)) p = p.Parent;
            _map.SetZoom(p);
        }

        Display.Invalidate();
        _tree.Rebuild();
        _map.MarkDirty();
        _lblZoom.Text = _map.ZoomText;
        _lvFiles.SetItems(_lvFiles.Data.Where(IsAttached).ToList());
        MarkTabsDirty();
        RefreshActiveTab();
        if (!_scanner.IsActive) UpdateStatusIdle(_stState.Text);
        _stPath.Text = $"{(recycle ? "Recycled" : "Deleted")} {removed:N0} item(s), {Fmt.Size(freed)}." +
                       (recycle && removed > 0 ? " Empty the Recycle Bin to actually free the space." : "");

        if (partial.Count > 0 && !_scanner.IsActive)
        {
            // Some files inside were in use / protected: re-read those folders so the numbers are right.
            _isRescan = true;
            foreach (var d in partial) TreeOps.PrepareRescan(d);
            _scanner.Start(partial, _settings);
            UpdateButtons();
        }
        if (rc != 0 && !aborted && removed < nodes.Count)
            MessageBox.Show(this, $"Some items couldn't be deleted (error {rc}). They may be in use or protected.", "DiskHog",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    async void CopyReport()
    {
        if (_scanner.Roots.Count == 0 || _busy) return;
        string tmp = Path.Combine(Path.GetTempPath(), "DiskHog_clipboard.md");
        try
        {
            UseWaitCursor = true;
            await Task.Run(() => ReportWriter.Write(tmp, _scanner, _settings, false));
            Clipboard.SetText(File.ReadAllText(tmp));
            _stPath.Text = "Report copied to the clipboard - paste it into an AI chat and ask what's safe to delete.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't copy the report:\n" + ex.Message, "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    async void ExportReport(bool withCsv)
    {
        if (_scanner.Roots.Count == 0 || _busy) return;
        string folder = _settings.ResolvedReportFolder;
        try { Directory.CreateDirectory(folder); } catch { }
        using var dlg = new SaveFileDialog
        {
            Filter = "Markdown report (*.md)|*.md|Text file (*.txt)|*.txt",
            FileName = ReportWriter.DefaultBaseName() + ".md",
            InitialDirectory = folder,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _busy = true;
        UseWaitCursor = true;
        UpdateButtons();
        try
        {
            string file = dlg.FileName;
            var written = await Task.Run(() => ReportWriter.Write(file, _scanner, _settings, withCsv));
            _lastReportPath = file;
            _miOpenLastReport.Enabled = true;
            if (MessageBox.Show(this, "Report saved:\n" + string.Join("\n", written) + "\n\nOpen it now?", "DiskHog",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Shell.Open(file);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't write the report:\n" + ex.Message, "DiskHog", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            UpdateButtons();
        }
    }
}
