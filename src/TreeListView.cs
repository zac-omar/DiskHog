namespace DiskHog;

/// <summary>Folder tree with columns, built on a virtual ListView so it stays fast with huge folders.</summary>
sealed class TreeListView : ListView
{
    sealed class Row { public Node Node; public int Depth; }

    const int ColName = 0, ColSize = 1, ColPct = 2, ColFiles = 3, ColDirs = 4, ColMod = 5, ColNote = 6;

    static readonly Color[] BarColors =
    {
        Color.FromArgb(78, 121, 167), Color.FromArgb(89, 161, 79), Color.FromArgb(242, 142, 43),
        Color.FromArgb(176, 122, 161), Color.FromArgb(118, 183, 178), Color.FromArgb(225, 87, 89),
        Color.FromArgb(156, 117, 95), Color.FromArgb(237, 201, 72),
    };

    readonly List<Row> _rows = new();
    readonly HashSet<DirNode> _expanded = new();
    IList<RootNode> _roots = Array.Empty<RootNode>();
    int _sortCol = ColSize;
    bool _sortDesc = true;
    bool _suppress;
    readonly int _indent, _glyph, _icon;
    readonly ImageList _icons;
    Font _bold;
    Font Bold => _bold ??= new Font(Font, FontStyle.Bold);

    protected override void OnFontChanged(EventArgs e) { _bold = null; base.OnFontChanged(e); }

    public event Action<Node> NodeSelected;
    public event Action<Node> NodeActivated;

    public TreeListView()
    {
        View = System.Windows.Forms.View.Details;
        VirtualMode = true;
        OwnerDraw = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        DoubleBuffered = true;
        SmallImageList = new ImageList { ImageSize = new Size(1, LogicalToDeviceUnits(20)) };
        _indent = LogicalToDeviceUnits(16);
        _glyph = LogicalToDeviceUnits(14);
        _icon = LogicalToDeviceUnits(14);
        _icons = Icons.CategoryImages(_icon);

        Columns.Add("Name", LogicalToDeviceUnits(380));
        Columns.Add("Size", LogicalToDeviceUnits(85), HorizontalAlignment.Right);
        Columns.Add("% of parent", LogicalToDeviceUnits(150));
        Columns.Add("Files", LogicalToDeviceUnits(85), HorizontalAlignment.Right);
        Columns.Add("Folders", LogicalToDeviceUnits(70), HorizontalAlignment.Right);
        Columns.Add("Newest change inside", LogicalToDeviceUnits(135));
        Columns.Add("Note", LogicalToDeviceUnits(320));

        RetrieveVirtualItem += OnRetrieve;
        DrawColumnHeader += (_, e) => e.DrawDefault = true;
        DrawItem += (_, _) => { };
        DrawSubItem += OnDrawSubItem;
        ColumnClick += OnColumnClick;
        SelectedIndexChanged += (_, _) => { if (!_suppress) NodeSelected?.Invoke(SelectedNode); };
    }

    public Node SelectedNode =>
        SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]].Node : null;

    public void SetRoots(IList<RootNode> roots)
    {
        _roots = roots;
        _expanded.Clear();
        foreach (var r in roots) _expanded.Add(r);
        Rebuild();
    }

    /// <summary>Re-flattens and re-sorts the visible rows, keeping expansion and selection.</summary>
    public void Rebuild()
    {
        var sel = SelectedNode;
        _suppress = true;
        try
        {
            _rows.Clear();
            foreach (var r in _roots) AddRows(_rows, r, 0);
            SelectedIndices.Clear();
            VirtualListSize = _rows.Count;
            if (sel != null)
            {
                int i = IndexOf(sel);
                if (i >= 0) SelectedIndices.Add(i);
            }
        }
        finally { _suppress = false; }
        Invalidate();
    }

    void AddRows(List<Row> list, Node n, int depth)
    {
        list.Add(new Row { Node = n, Depth = depth });
        if (n is DirNode d && _expanded.Contains(d))
            foreach (var c in Sorted(d)) AddRows(list, c, depth + 1);
    }

    Node[] Sorted(DirNode d)
    {
        if ((_sortCol == ColSize || _sortCol == ColPct) && _sortDesc) return d.SortedBySize();
        var arr = (Node[])d.GetChildren().Clone();
        Comparison<Node> cmp = _sortCol switch
        {
            ColName => (a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            ColFiles => (a, b) => ((a as DirNode)?.FileCount ?? 0).CompareTo((b as DirNode)?.FileCount ?? 0),
            ColDirs => (a, b) => ((a as DirNode)?.DirCount ?? 0).CompareTo((b as DirNode)?.DirCount ?? 0),
            ColMod => (a, b) => a.Modified.CompareTo(b.Modified),
            _ => (a, b) => a.Value.CompareTo(b.Value),
        };
        Array.Sort(arr, _sortDesc ? (a, b) => cmp(b, a) : cmp);
        return arr;
    }

    int IndexOf(Node n)
    {
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].Node == n) return i;
        return -1;
    }

    bool Expandable(Node n) => n is DirNode d && d.GetChildren().Length > 0;

    void Toggle(int i)
    {
        var row = _rows[i];
        if (row.Node is not DirNode d || !Expandable(d)) return;
        var sel = SelectedNode;
        _suppress = true;
        try
        {
            if (_expanded.Remove(d))
            {
                int j = i + 1;
                while (j < _rows.Count && _rows[j].Depth > row.Depth) j++;
                _rows.RemoveRange(i + 1, j - i - 1);
            }
            else
            {
                _expanded.Add(d);
                var add = new List<Row>();
                foreach (var c in Sorted(d)) AddRows(add, c, row.Depth + 1);
                _rows.InsertRange(i + 1, add);
            }
            SelectedIndices.Clear();
            VirtualListSize = _rows.Count;
            int si = sel != null ? IndexOf(sel) : -1;
            SelectedIndices.Add(si >= 0 ? si : i);
        }
        finally { _suppress = false; }
        Invalidate();
        if (SelectedNode != sel) NodeSelected?.Invoke(SelectedNode);
    }

    void SelectIndex(int i)
    {
        if (i < 0 || i >= _rows.Count) return;
        SelectedIndices.Clear();
        SelectedIndices.Add(i);
        FocusedItem = Items[i];
        EnsureVisible(i);
    }

    /// <summary>Expands down to the node, selects it and scrolls it into view.</summary>
    public void SelectNode(Node n)
    {
        if (n == null) return;
        for (var p = n.Parent; p != null; p = p.Parent) _expanded.Add(p);
        if (IndexOf(n) < 0) Rebuild();
        SelectIndex(IndexOf(n));
    }

    public void ExpandSelected()
    {
        int i = SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;
        if (i >= 0 && _rows[i].Node is DirNode d && !_expanded.Contains(d)) Toggle(i);
    }

    void OnColumnClick(object sender, ColumnClickEventArgs e)
    {
        if (e.Column == ColNote) return;
        if (e.Column == _sortCol) _sortDesc = !_sortDesc;
        else { _sortCol = e.Column; _sortDesc = e.Column != ColName; }
        Rebuild();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var hit = HitTest(e.Location);
            if (hit.Item != null)
            {
                int i = hit.Item.Index;
                int gx = hit.Item.Bounds.X + LogicalToDeviceUnits(2) + _rows[i].Depth * _indent;
                if (e.X >= gx && e.X < gx + _glyph + 2) { Toggle(i); return; }
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit.Item != null && e.Button == MouseButtons.Left)
        {
            var n = _rows[hit.Item.Index].Node;
            if (Expandable(n)) Toggle(hit.Item.Index);
            else NodeActivated?.Invoke(n);
        }
        base.OnMouseDoubleClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int i = SelectedIndices.Count > 0 ? SelectedIndices[0] : -1;
        if (i >= 0 && i < _rows.Count && !e.Control && !e.Alt)
        {
            var row = _rows[i];
            bool expanded = row.Node is DirNode d && _expanded.Contains(d);
            if (e.KeyCode == Keys.Right)
            {
                if (Expandable(row.Node)) { if (!expanded) Toggle(i); else SelectIndex(i + 1); }
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Left)
            {
                if (expanded && Expandable(row.Node)) Toggle(i);
                else if (row.Node.Parent != null) SelectIndex(IndexOf(row.Node.Parent));
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                NodeActivated?.Invoke(row.Node);
                e.Handled = e.SuppressKeyPress = true;
            }
        }
        base.OnKeyDown(e);
    }

    // ---------------- drawing ----------------

    static double Fraction(Node n)
    {
        if (n.Parent != null)
        {
            long pv = n.Parent.Value;
            return pv > 0 ? Math.Clamp((double)n.Value / pv, 0, 1) : 0;
        }
        if (n is RootNode r && r.IsDriveRoot && r.VolumeTotal > 0) return Math.Clamp((double)r.Value / r.VolumeTotal, 0, 1);
        return 1;
    }

    static string NoteFor(Node n)
    {
        if (n.IsVirtual) return "Used space no readable file explains: NTFS metadata, restore points, unreadable folders";
        if (n is RootNode r)
        {
            string s = $"{r.MediaText} · {r.FileSystem}";
            if (r.VolumeTotal > 0) s += $" · {Fmt.Size(r.VolumeFree)} free of {Fmt.Size(r.VolumeTotal)}";
            if (r.State != DirState.Done) s += " · " + r.StateText;
            return s;
        }
        if (n is DirNode d) return d.StateText;
        if (n.IsHardLinkDup) return "hard link - already counted elsewhere";
        uint a = n.Attr;
        if ((a & (Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_RECALL_ON_OPEN | Native.FILE_ATTRIBUTE_OFFLINE)) != 0)
            return "cloud placeholder (online-only, uses little disk)";
        if ((a & Native.FILE_ATTRIBUTE_COMPRESSED) != 0) return "NTFS-compressed";
        if ((a & Native.FILE_ATTRIBUTE_SPARSE_FILE) != 0) return "sparse file";
        return "";
    }

    void OnRetrieve(object sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex >= _rows.Count) { e.Item = new ListViewItem(new string[7]); return; }
        var n = _rows[e.ItemIndex].Node;
        var d = n as DirNode;
        e.Item = new ListViewItem(new[]
        {
            n.Name, Fmt.Size(n.Value), (Fraction(n) * 100).ToString("0.0") + "%",
            d != null ? Fmt.Count(d.FileCount) : "", d != null ? Fmt.Count(d.DirCount) : "",
            Fmt.Date(n.Modified), NoteFor(n),
        });
    }

    void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        int idx = e.ItemIndex;
        if (idx < 0 || idx >= _rows.Count) return;
        var row = _rows[idx];
        var n = row.Node;
        var g = e.Graphics;
        var b = e.Bounds;
        if (e.ColumnIndex == ColName) b = new Rectangle(b.X, b.Y, Columns[0].Width, b.Height);

        bool sel = SelectedIndices.Count > 0 && SelectedIndices[0] == idx;
        bool focused = Focused;
        Color back, fore;
        if (sel) { back = focused ? SystemColors.Highlight : Color.FromArgb(204, 228, 247); fore = focused ? SystemColors.HighlightText : SystemColors.ControlText; }
        else
        {
            back = n.IsVirtual ? Color.FromArgb(238, 238, 238)
                 : n is DirNode { State: DirState.Error } ? Color.FromArgb(255, 236, 236)
                 : SystemColors.Window;
            fore = n is DirNode { State: DirState.Pending } || n.IsHardLinkDup ? SystemColors.GrayText
                 : n is DirNode { State: DirState.Link } ? Color.Teal
                 : SystemColors.WindowText;
        }
        using (var bb = new SolidBrush(back)) g.FillRectangle(bb, b);

        const TextFormatFlags baseFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        switch (e.ColumnIndex)
        {
            case ColName:
            {
                int x = b.X + LogicalToDeviceUnits(2) + row.Depth * _indent;
                int cy = b.Y + b.Height / 2;
                if (Expandable(n))
                {
                    bool exp = _expanded.Contains((DirNode)n);
                    int s = _glyph / 3;
                    int gx = x + _glyph / 2;
                    Point[] tri = exp
                        ? new[] { new Point(gx - s, cy - s / 2), new Point(gx + s, cy - s / 2), new Point(gx, cy + s) }
                        : new[] { new Point(gx - s / 2, cy - s), new Point(gx + s, cy), new Point(gx - s / 2, cy + s) };
                    using var gb = new SolidBrush(sel && focused ? fore : Color.FromArgb(110, 110, 110));
                    g.FillPolygon(gb, tri);
                }
                x += _glyph + 2;
                int img = n.IsDir ? FileTypes.Categories.Length : n.IsVirtual ? 0 : FileTypes.CategoryOf(n.Name);
                _icons.Draw(g, x, cy - _icon / 2, img);
                x += _icon + LogicalToDeviceUnits(4);
                string text = n is RootNode r && r.Label.Length > 0 ? $"{n.Name}  [{r.Label}]" : n.Name;
                TextRenderer.DrawText(g, text, n is RootNode ? Bold : Font, new Rectangle(x, b.Y, Math.Max(0, b.Right - x), b.Height), fore, baseFlags);
                break;
            }
            case ColPct:
            {
                double f = Fraction(n);
                int textW = LogicalToDeviceUnits(48);
                int barMax = Math.Max(0, b.Width - textW - LogicalToDeviceUnits(8));
                var bar = new Rectangle(b.X + LogicalToDeviceUnits(4), b.Y + LogicalToDeviceUnits(4), (int)(barMax * f), b.Height - LogicalToDeviceUnits(8));
                var frame = new Rectangle(bar.X, bar.Y, barMax, bar.Height);
                using (var fb = new SolidBrush(Color.FromArgb(sel && focused ? 60 : 255, 236, 236, 236))) g.FillRectangle(fb, frame);
                if (bar.Width > 0)
                {
                    var c = n.IsVirtual ? FileTypes.UnaccountedColor : BarColors[row.Depth % BarColors.Length];
                    using var cb = new SolidBrush(c);
                    g.FillRectangle(cb, bar);
                }
                TextRenderer.DrawText(g, (f * 100).ToString("0.0") + "%", Font,
                    new Rectangle(b.Right - textW - 2, b.Y, textW, b.Height), fore, baseFlags | TextFormatFlags.Right);
                break;
            }
            default:
            {
                string text = e.SubItem?.Text ?? "";
                if (e.ColumnIndex == ColSize && n.IsHardLinkDup) text = Fmt.Size(n.RawValue) + " (link)";
                var flags = baseFlags | (e.ColumnIndex is ColSize or ColFiles or ColDirs ? TextFormatFlags.Right : TextFormatFlags.Left);
                var r = Rectangle.Inflate(b, -LogicalToDeviceUnits(4), 0);
                TextRenderer.DrawText(g, text, e.ColumnIndex == ColSize && n.IsDir ? Bold : Font, r, fore, flags);
                break;
            }
        }
    }
}
