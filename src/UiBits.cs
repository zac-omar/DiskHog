using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DiskHog;

/// <summary>Small toolbar icons drawn in code (no image files needed).</summary>
static class Icons
{
    static Bitmap Make(int size, Action<Graphics, float> draw)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        draw(g, size / 16f);
        return bmp;
    }

    public static Bitmap Play(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Color.FromArgb(46, 160, 67));
        g.FillPolygon(b, new[] { new PointF(3 * k, 2 * k), new PointF(14 * k, 8 * k), new PointF(3 * k, 14 * k) });
    });

    public static Bitmap Pause(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Color.FromArgb(230, 145, 30));
        g.FillRectangle(b, 3 * k, 2 * k, 3.5f * k, 12 * k);
        g.FillRectangle(b, 9.5f * k, 2 * k, 3.5f * k, 12 * k);
    });

    public static Bitmap Stop(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Color.FromArgb(210, 60, 60));
        g.FillRectangle(b, 3 * k, 3 * k, 10 * k, 10 * k);
    });

    public static Bitmap Folder(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Color.FromArgb(240, 190, 70));
        using var d = new SolidBrush(Color.FromArgb(214, 160, 40));
        g.FillRectangle(d, 1 * k, 3 * k, 6 * k, 3 * k);
        g.FillRectangle(b, 1 * k, 5 * k, 14 * k, 9 * k);
    });

    public static Bitmap Save(int s) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(Color.FromArgb(60, 110, 180));
        using var w = new SolidBrush(Color.White);
        g.FillRectangle(b, 2 * k, 2 * k, 12 * k, 12 * k);
        g.FillRectangle(w, 4.5f * k, 2 * k, 7 * k, 4.5f * k);
        g.FillRectangle(w, 4 * k, 9 * k, 8 * k, 5 * k);
    });

    public static Bitmap Report(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Color.FromArgb(90, 90, 90), 1 * k);
        using var w = new SolidBrush(Color.White);
        g.FillRectangle(w, 3 * k, 1.5f * k, 10 * k, 13 * k);
        g.DrawRectangle(p, 3 * k, 1.5f * k, 10 * k, 13 * k);
        for (int i = 0; i < 4; i++) g.DrawLine(p, 5 * k, (4.5f + i * 2.5f) * k, 11 * k, (4.5f + i * 2.5f) * k);
    });

    public static Bitmap Gear(int s) => Make(s, (g, k) =>
    {
        using var p = new Pen(Color.FromArgb(100, 100, 110), 2.2f * k);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            g.DrawLine(p, 8 * k + (float)Math.Cos(a) * 4 * k, 8 * k + (float)Math.Sin(a) * 4 * k,
                          8 * k + (float)Math.Cos(a) * 6.5f * k, 8 * k + (float)Math.Sin(a) * 6.5f * k);
        }
        g.DrawEllipse(p, 4.5f * k, 4.5f * k, 7 * k, 7 * k);
    });

    public static Bitmap Shield(int s) => Make(s, (g, k) =>
    {
        using var b1 = new SolidBrush(Color.FromArgb(40, 110, 200));
        using var b2 = new SolidBrush(Color.FromArgb(250, 200, 40));
        var pts = new[] { new PointF(8 * k, 1 * k), new PointF(14 * k, 3 * k), new PointF(13 * k, 10 * k), new PointF(8 * k, 15 * k), new PointF(3 * k, 10 * k), new PointF(2 * k, 3 * k) };
        g.FillPolygon(b1, pts);
        g.FillPolygon(b2, new[] { new PointF(8 * k, 1 * k), new PointF(14 * k, 3 * k), new PointF(13 * k, 10 * k), new PointF(8 * k, 15 * k) });
    });

    public static Bitmap Swatch(int s, Color c) => Make(s, (g, k) =>
    {
        using var b = new SolidBrush(c);
        using var p = new Pen(ControlPaint.Dark(c), 1);
        g.FillRectangle(b, 2 * k, 2 * k, 12 * k, 12 * k);
        g.DrawRectangle(p, 2 * k, 2 * k, 12 * k, 12 * k);
    });

    /// <summary>Image list with one colored square per file category (index = category) + folder at the end.</summary>
    public static ImageList CategoryImages(int size)
    {
        var il = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var c in FileTypes.Categories) il.Images.Add(Swatch(size, c.Color));
        il.Images.Add(Folder(size));
        return il;
    }
}

/// <summary>A fast virtual-mode list with sortable columns.</summary>
sealed class VList<T> : ListView where T : class
{
    public sealed record Col(string Header, int Width, Func<T, string> Text, Comparison<T> Sort = null, bool Right = false);

    readonly Col[] _cols;
    List<T> _items = new();
    int _sortCol = -1;
    bool _desc;

    public Func<T, Color?> RowColor;
    public Func<T, int> ImageOf;

    public VList(params Col[] cols)
    {
        _cols = cols;
        View = System.Windows.Forms.View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = true;
        DoubleBuffered = true;
        GridLines = false;
        foreach (var c in cols)
            Columns.Add(c.Header, LogicalToDeviceUnits(c.Width), c.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        RetrieveVirtualItem += OnRetrieve;
        ColumnClick += (_, e) => SortBy(e.Column, e.Column == _sortCol ? !_desc : DefaultDesc(e.Column));
    }

    bool DefaultDesc(int col) => _cols[col].Right; // numbers sort big-first by default

    public IReadOnlyList<T> Data => _items;

    public void SetItems(List<T> items, bool keepSort = true)
    {
        var sel = SelectedItems0();
        SelectedIndices.Clear();
        _items = items ?? new List<T>();
        if (keepSort && _sortCol >= 0) ApplySort();
        VirtualListSize = _items.Count;
        if (sel != null)
        {
            int i = _items.IndexOf(sel);
            if (i >= 0) SelectedIndices.Add(i);
        }
        Invalidate();
    }

    public void SortBy(int col, bool desc)
    {
        if (_cols[col].Sort == null) return;
        _sortCol = col;
        _desc = desc;
        var sel = SelectedItems0();
        ApplySort();
        SelectedIndices.Clear();
        if (sel != null) { int i = _items.IndexOf(sel); if (i >= 0) { SelectedIndices.Add(i); EnsureVisible(i); } }
        Invalidate();
    }

    void ApplySort()
    {
        var cmp = _cols[_sortCol].Sort;
        if (cmp == null) return;
        _items.Sort(_desc ? (a, b) => cmp(b, a) : cmp);
    }

    T SelectedItems0() => SelectedIndices.Count > 0 && SelectedIndices[0] < _items.Count ? _items[SelectedIndices[0]] : null;

    public T Selected => SelectedItems0();

    public List<T> SelectedAll()
    {
        var list = new List<T>();
        foreach (int i in SelectedIndices) if (i < _items.Count) list.Add(_items[i]);
        return list;
    }

    void OnRetrieve(object sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex >= _items.Count) { e.Item = new ListViewItem(new string[_cols.Length]); return; }
        var t = _items[e.ItemIndex];
        var texts = new string[_cols.Length];
        for (int i = 0; i < _cols.Length; i++)
        {
            try { texts[i] = _cols[i].Text(t) ?? ""; } catch { texts[i] = ""; }
        }
        var item = new ListViewItem(texts);
        if (ImageOf != null) item.ImageIndex = ImageOf(t);
        var c = RowColor?.Invoke(t);
        if (c.HasValue) item.BackColor = c.Value;
        e.Item = item;
    }
}

static class Shell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// Deletes several items in one shell operation (shows Windows' own progress / error dialogs).
    /// Recycle mode warns before permanently deleting anything too big for the Recycle Bin.
    /// </summary>
    public static int Delete(IList<string> paths, bool recycle, IntPtr owner, out bool aborted)
    {
        const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = 3, // FO_DELETE
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = (ushort)(FOF_NOCONFIRMATION | (recycle ? FOF_ALLOWUNDO | FOF_WANTNUKEWARNING : 0)),
        };
        int rc = SHFileOperationW(ref op);
        aborted = op.fAnyOperationsAborted;
        return rc;
    }

    public static void ShowProperties(string path, IntPtr owner)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        ShellExecuteEx(ref info);
    }

    public static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public static void ShowInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
