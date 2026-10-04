namespace DiskHog;

static class FileTypes
{
    public sealed record Category(string Name, Color Color);

    public const int Other = 0;

    public static readonly Category[] Categories =
    {
        new("Other",            Color.FromArgb(157, 157, 157)),
        new("Video",            Color.FromArgb(225, 87, 89)),
        new("Audio",            Color.FromArgb(242, 142, 43)),
        new("Images",           Color.FromArgb(237, 201, 72)),
        new("Archives",         Color.FromArgb(176, 122, 161)),
        new("Disk images / VM", Color.FromArgb(156, 117, 95)),
        new("Programs / system",Color.FromArgb(78, 121, 167)),
        new("Documents",        Color.FromArgb(89, 161, 79)),
        new("Code / dev",       Color.FromArgb(118, 183, 178)),
        new("Databases",        Color.FromArgb(255, 157, 167)),
        new("Logs / temp / dumps", Color.FromArgb(186, 176, 172)),
        new("Game data",        Color.FromArgb(160, 203, 232)),
    };

    public static readonly Color UnaccountedColor = Color.FromArgb(90, 90, 90);
    public static readonly Color RestColor = Color.FromArgb(205, 205, 205);

    static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> Lookup;

    static FileTypes()
    {
        void Add(int cat, string exts) { foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Map[e] = cat; }
        Add(1, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts mts vob 3gp ogv rmvb divx");
        Add(2, "mp3 wav flac aac ogg m4a wma opus aiff aif alac mid midi ape wv");
        Add(3, "jpg jpeg png gif bmp tif tiff webp heic heif raw cr2 cr3 nef arw dng orf rw2 psd psb svg ico xcf kra exr hdr tga dds");
        Add(4, "zip rar 7z tar gz bz2 xz tgz cab zst lz4 lzma arj z tbz2 txz");
        Add(5, "iso img vhd vhdx avhdx vmdk vdi qcow2 wim esd swm ova ovf hds nvram vmem vmsn");
        Add(6, "exe dll sys msi msp msu ocx drv efi so pyd cpl scr mui appx appxbundle msix msixbundle cat manifest");
        Add(7, "pdf doc docx xls xlsx xlsm ppt pptx txt rtf odt ods odp csv md epub mobi azw3 one pst ost eml msg");
        Add(8, "cs cpp cc cxx h hpp c py js mjs ts tsx jsx java kt go rs rb php swift json xml yaml yml toml html htm css scss " +
               "pdb obj o ilk ipch pch idb class jar war node wasm lib a nupkg whl map sln csproj vcxproj gradle tlog");
        Add(9, "db sqlite sqlite3 mdb accdb ldf mdf ndf frm ibd myd db-wal db-shm edb jfm sdf realm");
        Add(10, "log tmp temp dmp mdmp hdmp etl evtx bak old cache crdownload part partial download blf regtrans-ms");
        Add(11, "pak vpk unity3d assets resource ress bsa ba2 uasset uexp ubulk utoc ucas forge bundle wad gcf ncf acf big rpf arc sb toc cas bnk pck");
        Lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static ReadOnlySpan<char> ExtSpan(string name)
    {
        int i = name.LastIndexOf('.');
        return i <= 0 || i == name.Length - 1 ? ReadOnlySpan<char>.Empty : name.AsSpan(i + 1);
    }

    /// <summary>Lower-case extension with dot, or "(no extension)".</summary>
    public static string Ext(string name)
    {
        var e = ExtSpan(name);
        return e.IsEmpty ? "(no extension)" : "." + e.ToString().ToLowerInvariant();
    }

    public static int CategoryOf(string name)
    {
        var e = ExtSpan(name);
        if (e.IsEmpty) return Other;
        return Lookup.TryGetValue(e, out int c) ? c : Other;
    }

    public static int CategoryOfExt(string dotExt) =>
        dotExt.Length > 1 && dotExt[0] == '.' && Map.TryGetValue(dotExt.Substring(1), out int c) ? c : Other;

    public static Color ColorOf(string name) => Categories[CategoryOf(name)].Color;
}
