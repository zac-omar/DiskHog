using System.Drawing.Drawing2D;

namespace DiskHog;

/// <summary>
/// Squarified treemap: every file is a rectangle whose area is proportional to its size, nested inside
/// its folders. Colors = file category. Double-click to zoom in, Backspace / mouse-back to zoom out.
/// </summary>
sealed class TreemapControl : Control
{
    struct Cell
    {
        public RectangleF R;
        public Node Node;
        public byte Kind;
        public int Depth;
        public long RestSize;
        public int RestCount;
        public DirNode RestParent;
    }

    const byte KFile = 0, KDir = 1, KRest = 2;
    const double MinArea = 20; // px²; anything smaller is merged into a "small items" block

    static readonly Color[] DirColors =
    {
        Color.FromArgb(52, 61, 70), Color.FromArgb(72, 83, 95), Color.FromArgb(95, 107, 120),
        Color.FromArgb(120, 132, 145), Color.FromArgb(146, 157, 169), Color.FromArgb(170, 179, 189),
    };

    readonly List<Cell> _cells = new();
    readonly Dictionary<Node, int> _cellOf = new();
    readonly ToolTip _tip = new() { InitialDelay = 200, ReshowDelay = 50, AutoPopDelay = 30000, ShowAlways = true };
    IList<RootNode> _roots = Array.Empty<RootNode>();
    DirNode _zoom;
    Node _selected;
    Func<Node, bool> _highlight;
    Bitmap _bmp;
    bool _dirty = true;
    int _hover = -1;
    readonly Font _font, _bold;
    readonly int _headerH;

    public event Action<Node> NodeClicked;
    public event Action<Node, Point> NodeRightClicked;
    public event Action ZoomChanged;

    public TreemapControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(36, 40, 46);
        _font = new Font("Segoe UI", 8f);
        _bold = new Font("Segoe UI", 8f, FontStyle.Bold);
        _headerH = _font.Height + 2;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bmp?.Dispose(); _font.Dispose(); _bold.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------------- public API ----------------

    public void SetRoots(IList<RootNode> roots)
    {
        _roots = roots ?? Array.Empty<RootNode>();
        _zoom = null;
        MarkDirty();
        ZoomChanged?.Invoke();
    }

    public DirNode EffectiveZoom => _zoom ?? (_roots.Count == 1 ? _roots[0] : null);

    public bool CanZoomOut => EffectiveZoom != null && (EffectiveZoom.Parent != null || _roots.Count > 1);

    public void SetZoom(DirNode d)
    {
        _zoom = d;
        MarkDirty();
        ZoomChanged?.Invoke();
    }

    public void ZoomOut()
    {
        var z = EffectiveZoom;
        if (z == null) return;
        if (z.Parent != null) SetZoom(z.Parent);
        else if (_roots.Count > 1) SetZoom(null);
    }

    public void ZoomTop() => SetZoom(null);

    public string ZoomText
    {
        get
        {
            var z = EffectiveZoom;
            if (z == null) return _roots.Count > 1 ? $"All scanned drives  —  {Fmt.Size(_roots.Sum(r => r.Value))}" : "Nothing scanned yet";
            return $"{z.FullPath}  —  {Fmt.Size(z.Value)}";
        }
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Node Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    /// <summary>Files not matching are drawn faded (null = no highlight).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Node, bool> Highlight
    {
        get => _highlight;
        set { _highlight = value; MarkDirty(); }
    }

    public void MarkDirty()
    {
        _dirty = true;
        Invalidate();
    }

    // ---------------- layout ----------------

    void Relayout()
    {
        _cells.Clear();
        _cellOf.Clear();
        _hover = -1;
        var r = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        if (r.Width < 4 || r.Height < 4) return;
        var z = EffectiveZoom;
        if (z != null) LayoutDir(z, r, 0);
        else if (_roots.Count > 1)
            LayoutChildren(_roots.Cast<Node>().OrderByDescending(n => n.Value).ToArray(), r, 0, null);
    }

    void AddCell(Cell c)
    {
        if (c.Node != null) _cellOf[c.Node] = _cells.Count;
        _cells.Add(c);
    }

    bool HasHeader(RectangleF r) => r.Width >= 50 && r.Height >= _headerH + 12;

    void LayoutDir(DirNode d, RectangleF r, int depth)
    {
        AddCell(new Cell { R = r, Node = d, Kind = KDir, Depth = depth });
        if (r.Width < 6 || r.Height < 6) return;
        var inner = HasHeader(r)
            ? new RectangleF(r.X + 2, r.Y + _headerH, r.Width - 4, r.Height - _headerH - 2)
            : RectangleF.Inflate(r, -1, -1);
        LayoutChildren(d.SortedBySize(), inner, depth + 1, d);
    }

    void LayoutChildren(Node[] sorted, RectangleF r, int depth, DirNode parent)
    {
        if (r.Width < 1 || r.Height < 1) return;
        double total = 0;
        foreach (var c in sorted) { long v = c.Value; if (v > 0) total += v; }
        if (total <= 0) return;
        double scale = r.Width * r.Height / total;

        var areas = new List<double>();
        var nodes = new List<Node>();
        double rest = 0;
        int restCount = 0;
        foreach (var c in sorted)
        {
            long v = c.Value;
            if (v <= 0) continue;
            double a = v * scale;
            if (a >= MinArea) { areas.Add(a); nodes.Add(c); }
            else { rest += a; restCount++; }
        }
        if (rest > 0) { areas.Add(rest); nodes.Add(null); }

        long restBytes = (long)(rest / scale);
        Squarify(areas, r, (i, rect) =>
        {
            var n = nodes[i];
            if (n == null)
                AddCell(new Cell { R = rect, Kind = KRest, Depth = depth, RestSize = restBytes, RestCount = restCount, RestParent = parent });
            else if (n is DirNode sd)
                LayoutDir(sd, rect, depth);
            else
                AddCell(new Cell { R = rect, Node = n, Kind = KFile, Depth = depth });
        });
    }

    /// <summary>Bruls/Huizing/van Wijk squarified layout. Areas should be sorted largest first.</summary>
    static void Squarify(List<double> areas, RectangleF rect, Action<int, RectangleF> emit)
    {
        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        double sum = 0;
        foreach (var a in areas) sum += a;
        if (sum <= 0) return;
        double k = w * h / sum;
        int i = 0, n = areas.Count;
        while (i < n && w > 0.01 && h > 0.01)
        {
            double side = Math.Min(w, h);
            double side2 = side * side;
            double rowSum = 0, rowMax = 0, rowMin = double.MaxValue, worst = double.MaxValue;
            int j = i;
            while (j < n)
            {
                double s = areas[j] * k;
                double ns = rowSum + s, nMax = Math.Max(rowMax, s), nMin = Math.Min(rowMin, s);
                double ns2 = ns * ns;
                double wst = Math.Max(side2 * nMax / ns2, ns2 / (side2 * nMin));
                if (j > i && wst > worst) break;
                worst = wst; rowSum = ns; rowMax = nMax; rowMin = nMin;
                j++;
            }
            double thick = rowSum / side;
            if (w >= h)
            {
                double yy = y;
                for (int q = i; q < j; q++)
                {
                    double hh = areas[q] * k / thick;
                    emit(q, new RectangleF((float)x, (float)yy, (float)thick, (float)hh));
                    yy += hh;
                }
                x += thick; w -= thick;
            }
            else
            {
                double xx = x;
                for (int q = i; q < j; q++)
                {
                    double ww = areas[q] * k / thick;
                    emit(q, new RectangleF((float)xx, (float)y, (float)ww, (float)thick));
                    xx += ww;
                }
                y += thick; h -= thick;
            }
            i = j;
        }
    }

    // ---------------- rendering ----------------

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static bool IsDark(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 140;

    void Render()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(w, h);
        }
        using var g = Graphics.FromImage(_bmp);
        g.Clear(BackColor);
        foreach (var c in _cells) DrawCell(g, c);
        if (_cells.Count == 0)
        {
            string msg = _roots.Count == 0 ? "Pick drives above and press Scan.\nThe map of what's using your space appears here." : "Nothing to show here yet.";
            TextRenderer.DrawText(g, msg, Font, new Rectangle(0, 0, w, h), Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    void DrawCell(Graphics g, Cell c)
    {
        var r = c.R;
        if (r.Width < 0.5f || r.Height < 0.5f) return;
        var ri = Rectangle.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        if (ri.Width < 1) ri.Width = 1;
        if (ri.Height < 1) ri.Height = 1;
        const TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        switch (c.Kind)
        {
            case KDir:
            {
                var d = (DirNode)c.Node;
                Color col = DirColors[Math.Min(c.Depth, DirColors.Length - 1)];
                if (d.State == DirState.Error) col = Mix(col, Color.FromArgb(200, 60, 60), 0.5);
                else if (d.State == DirState.Link) col = Mix(col, Color.Teal, 0.5);
                using (var b = new SolidBrush(col)) g.FillRectangle(b, ri);
                if (d.State == DirState.Pending && ri.Width > 3 && ri.Height > 3)
                {
                    using var hb = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(70, 255, 255, 255), Color.Transparent);
                    g.FillRectangle(hb, ri);
                }
                if (ri.Width > 3 && ri.Height > 3)
                    using (var p = new Pen(Mix(col, Color.Black, 0.45))) g.DrawRectangle(p, ri.X, ri.Y, ri.Width - 1, ri.Height - 1);
                if (HasHeader(r))
                {
                    string name = c.Depth == 0 || d.Parent == null ? d.FullPath : d.Name;
                    string text = $"{name}   {Fmt.Size(d.Value)}";
                    if (d.State == DirState.Pending) text += "   (scanning…)";
                    TextRenderer.DrawText(g, text, _bold, new Rectangle(ri.X + 4, ri.Y + 1, ri.Width - 8, _headerH - 1),
                        IsDark(col) ? Color.White : Color.Black, tf | TextFormatFlags.VerticalCenter);
                }
                break;
            }
            case KFile:
            {
                var n = c.Node;
                Color col = n.IsVirtual ? FileTypes.UnaccountedColor : FileTypes.ColorOf(n.Name);
                if (_highlight != null && !_highlight(n)) col = Mix(col, Color.FromArgb(70, 74, 80), 0.8);
                if (ri.Width >= 3 && ri.Height >= 3)
                {
                    using var lg = new LinearGradientBrush(new Rectangle(ri.X - 1, ri.Y - 1, ri.Width + 2, ri.Height + 2),
                        Mix(col, Color.White, 0.35), Mix(col, Color.Black, 0.30), 45f);
                    g.FillRectangle(lg, ri);
                }
                else using (var b = new SolidBrush(col)) g.FillRectangle(b, ri);
                if (n.IsVirtual)
                {
                    using var hb = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(60, 0, 0, 0), Color.Transparent);
                    g.FillRectangle(hb, ri);
                }
                if (ri.Width >= 4 && ri.Height >= 4)
                    using (var p = new Pen(Mix(col, Color.Black, 0.5))) g.DrawRectangle(p, ri.X, ri.Y, ri.Width - 1, ri.Height - 1);
                if (ri.Width > 56 && ri.Height > 15)
                {
                    var fc = IsDark(col) ? Color.White : Color.Black;
                    int lh = _font.Height;
                    var tr = new Rectangle(ri.X + 3, ri.Y + 2, ri.Width - 6, lh);
                    TextRenderer.DrawText(g, n.Name, _font, tr, fc, tf);
                    if (ri.Height > 2 * lh + 4)
                        TextRenderer.DrawText(g, Fmt.Size(n.Value), _font, new Rectangle(tr.X, tr.Y + lh, tr.Width, lh), fc, tf);
                }
                break;
            }
            case KRest:
            {
                using (var hb = new HatchBrush(HatchStyle.Percent10, Color.FromArgb(178, 178, 178), FileTypes.RestColor))
                    g.FillRectangle(hb, ri);
                if (ri.Width > 70 && ri.Height > 15)
                    TextRenderer.DrawText(g, $"{c.RestCount:N0} small items", _font,
                        new Rectangle(ri.X + 3, ri.Y + 2, ri.Width - 6, _font.Height), Color.Black, tf);
                break;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_dirty)
        {
            _dirty = false;
            Relayout();
            Render();
        }
        if (_bmp != null) e.Graphics.DrawImageUnscaled(_bmp, 0, 0);

        if (_hover >= 0 && _hover < _cells.Count)
        {
            var hr = Rectangle.Round(_cells[_hover].R);
            using var hp = new Pen(Color.FromArgb(200, 255, 255, 255), 1);
            e.Graphics.DrawRectangle(hp, hr.X, hr.Y, Math.Max(0, hr.Width - 1), Math.Max(0, hr.Height - 1));
        }

        // Selection: outline the node, or its nearest visible ancestor if it's too small to have its own block.
        for (var n = _selected; n != null; n = n.Parent)
        {
            if (!_cellOf.TryGetValue(n, out int ci)) continue;
            var sr = Rectangle.Round(_cells[ci].R);
            using var p1 = new Pen(Color.Yellow, 3);
            using var p2 = new Pen(Color.Black, 1);
            e.Graphics.DrawRectangle(p1, sr.X + 1, sr.Y + 1, Math.Max(0, sr.Width - 3), Math.Max(0, sr.Height - 3));
            e.Graphics.DrawRectangle(p2, sr.X + 3, sr.Y + 3, Math.Max(0, sr.Width - 7), Math.Max(0, sr.Height - 7));
            break;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        _dirty = true;
        base.OnResize(e);
    }

    // ---------------- mouse ----------------

    int HitCell(Point p)
    {
        for (int i = _cells.Count - 1; i >= 0; i--)
            if (_cells[i].R.Contains(p)) return i;
        return -1;
    }

    Node NodeAt(int i) => i < 0 ? null : _cells[i].Kind == KRest ? _cells[i].RestParent : _cells[i].Node;

    string Describe(int i)
    {
        var c = _cells[i];
        var z = EffectiveZoom;
        long zv = z?.Value ?? _roots.Sum(r => r.Value);
        if (c.Kind == KRest)
            return $"{c.RestCount:N0} small items (each too small to draw)\nin {c.RestParent?.FullPath}\nTogether: {Fmt.Size(c.RestSize)}";
        var n = c.Node;
        string pct = zv > 0 ? $"{100.0 * n.Value / zv:0.0}% of view" : "";
        if (n.IsVirtual)
            return $"{n.Name}\n{Fmt.Size(n.Value)}  ({pct})\nNTFS metadata, restore points / shadow copies, and folders that couldn't be read.";
        if (n is DirNode d)
            return $"{d.FullPath}\n{Fmt.Size(d.Value)}  ({pct})\n{d.FileCount:N0} files, {d.DirCount:N0} folders\nNewest change inside: {Fmt.Date(d.Modified)}" +
                   (d.State != DirState.Done ? "\n" + d.StateText : "") + "\n\nDouble-click to zoom in";
        return $"{n.FullPath}\n{Fmt.Size(n.Value)}  ({pct})\n{FileTypes.Categories[FileTypes.CategoryOf(n.Name)].Name}  ·  modified {Fmt.Date(n.Modified)} ({Fmt.Age(n.Modified)})" +
               (n.IsHardLinkDup ? "\nHard link (counted elsewhere)" : "");
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitCell(e.Location);
        if (i == _hover) return;
        _hover = i;
        Invalidate();
        if (i < 0) _tip.Hide(this);
        else _tip.Show(Describe(i), this, e.X + 18, e.Y + 22);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        var n = NodeAt(HitCell(e.Location));
        if (n == null) return;
        if (e.Button == MouseButtons.Left) NodeClicked?.Invoke(n);
        else if (e.Button == MouseButtons.Right) NodeRightClicked?.Invoke(n, e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var n = NodeAt(HitCell(e.Location));
        if (n == null) return;
        if (n == EffectiveZoom) { ZoomOut(); return; }
        var target = n as DirNode ?? n.Parent;
        if (target != null && target != EffectiveZoom) SetZoom(target);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.XButton1) ZoomOut();
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Back || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Back) { ZoomOut(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
