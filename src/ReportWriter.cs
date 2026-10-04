using System.Security.Principal;
using System.Text;

namespace DiskHog;

/// <summary>
/// Writes a plain-text Markdown report designed to be read by a person or pasted into an AI chat,
/// plus an optional CSV of every item above a size threshold.
/// </summary>
static class ReportWriter
{
    public static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    public static string DefaultBaseName() => "DiskHog_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

    /// <summary>Writes the report(s). Returns the paths written.</summary>
    public static List<string> Write(string mdPath, Scanner sc, AppSettings st, bool writeCsv, CancellationToken ct = default)
    {
        var written = new List<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(mdPath)!);
        using (var w = new StreamWriter(mdPath, false, new UTF8Encoding(false)))
            WriteMarkdown(w, sc, st, ct);
        written.Add(mdPath);
        if (writeCsv)
        {
            string csv = Path.ChangeExtension(mdPath, ".csv");
            using (var w = new StreamWriter(csv, false, new UTF8Encoding(true)))
                WriteCsv(w, sc.Roots, (long)st.CsvMinMB * 1024 * 1024, ct);
            written.Add(csv);
        }
        return written;
    }

    static string Pct(long part, long whole) => whole <= 0 ? "-" : (100.0 * part / whole).ToString("0.0") + "%";

    static void WriteMarkdown(StreamWriter w, Scanner sc, AppSettings st, CancellationToken ct)
    {
        var roots = sc.Roots.ToList();
        var pending = sc.CollectPending();
        bool admin = IsAdmin();
        long minFolder = (long)st.ReportMinFolderMB * 1024 * 1024;

        w.WriteLine("# DiskHog disk usage report");
        w.WriteLine();
        w.WriteLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"- Size metric: {(Display.OnDisk ? "size on disk (allocated bytes - what actually frees up if deleted)" : "logical file size")}");
        w.WriteLine($"- Units: binary (1 KB = 1024 B, 1 MB = 1024 KB, 1 GB = 1024 MB, 1 TB = 1024 GB)");
        w.WriteLine($"- Running as administrator: {(admin ? "yes" : "no - some protected folders may be unreadable (see Unaccounted / Unreadable)")}");
        w.WriteLine(pending.Count == 0
            ? "- Scan status: complete"
            : $"- Scan status: INCOMPLETE - {pending.Count:N0} folders not scanned yet (scan was paused/stopped); totals are lower than reality");
        w.WriteLine($"- Scan time: {Fmt.Duration(sc.Elapsed)}  |  files: {roots.Sum(r => r.FileCount):N0}  |  folders: {roots.Sum(r => r.DirCount):N0}  |  unreadable folders: {sc.ErrorCount:N0}");
        w.WriteLine();
        w.WriteLine("Notes for the reader (human or AI):");
        w.WriteLine("- \"Newest\" on a folder = the most recent modification of anything inside it. Big folders with an old \"newest\" date are likely forgotten.");
        w.WriteLine("- Hard-linked files (common in C:\\Windows\\WinSxS) are counted once, so folder totals reflect real disk usage.");
        w.WriteLine("- \"Unaccounted\" = used space Windows reports that no readable file explains: NTFS metadata, restore points / shadow copies (System Volume Information), and folders that couldn't be read.");
        w.WriteLine("- Junctions / symbolic links are not followed (prevents double counting).");
        w.WriteLine();

        // ---- Roots summary
        w.WriteLine("## Drives / folders scanned");
        w.WriteLine();
        w.WriteLine("| Root | Label | FS | Media | Capacity | Used (Windows) | Free | Found by scan | Unaccounted | Files | Folders |");
        w.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in roots)
        {
            long found = r.Value - (r.UnaccountedNode?.Value ?? 0);
            string cap = r.VolumeTotal > 0 ? Fmt.Size(r.VolumeTotal) : "-";
            string used = r.VolumeTotal > 0 ? $"{Fmt.Size(r.VolumeUsed)} ({Pct(r.VolumeUsed, r.VolumeTotal)})" : "-";
            string free = r.VolumeTotal > 0 ? Fmt.Size(r.VolumeFree) : "-";
            string unacc = r.UnaccountedNode != null ? Fmt.Size(r.UnaccountedNode.Value) : r.IsDriveRoot ? "n/a (incomplete)" : "n/a (folder scan)";
            w.WriteLine($"| {r.Name} | {r.Label} | {r.FileSystem} | {r.MediaText} | {cap} | {used} | {free} | {Fmt.Size(found)} | {unacc} | {r.FileCount:N0} | {r.DirCount:N0} |");
        }
        w.WriteLine();

        // ---- Known hogs
        var hogs = KnownHogs.Find(roots).Where(h => h.Value >= 50L * 1024 * 1024).ToList();
        w.WriteLine("## Known space hogs found (>= 50 MB)");
        w.WriteLine();
        if (hogs.Count == 0) w.WriteLine("None found above 50 MB.");
        else
        {
            w.WriteLine("| Size | Location | What it is | Verdict | Advice |");
            w.WriteLine("|---|---|---|---|---|");
            foreach (var h in hogs)
                w.WriteLine($"| {Fmt.Size(h.Value)} | {h.Node.FullPath} | {h.Rule.Title} | {KnownHogs.SafetyText(h.Rule.Safety)} | {h.Rule.Advice} |");
        }
        w.WriteLine();

        // ---- Folder tree
        w.WriteLine($"## Folder tree (items >= {Fmt.Size(minFolder)}, up to {st.ReportTreeDepth} levels)");
        w.WriteLine();
        w.WriteLine("Each line: size | % of parent | files inside | newest change inside | path. Smaller siblings are summarised on a \"(+N smaller items)\" line.");
        w.WriteLine();
        w.WriteLine("```");
        // Small folder scans get a proportionally smaller threshold so the tree isn't empty.
        foreach (var r in roots)
            WriteTree(w, r, 0, r.Value, Math.Min(minFolder, Math.Max(1L << 20, r.Value / 200)), st.ReportTreeDepth, ct);
        w.WriteLine("```");
        w.WriteLine();

        // ---- Largest files
        if (st.ReportTopFiles > 0)
        {
            var top = Analysis.TopFiles(roots, st.ReportTopFiles, 1, 0, null, ct);
            w.WriteLine($"## Largest files (top {top.Count})");
            w.WriteLine();
            w.WriteLine("| # | Size | Last modified | Path |");
            w.WriteLine("|---|---|---|---|");
            for (int i = 0; i < top.Count; i++)
                w.WriteLine($"| {i + 1} | {Fmt.Size(top[i].Value)} | {Fmt.DateShort(top[i].Modified)} | {top[i].FullPath} |");
            w.WriteLine();

            long cutoff = DateTime.UtcNow.AddDays(-st.StaleDays).ToFileTimeUtc();
            var stale = Analysis.TopFiles(roots, Math.Max(50, st.ReportTopFiles / 2), 10L * 1024 * 1024, cutoff, null, ct);
            w.WriteLine($"## Big files not modified in {st.StaleDays}+ days (top {stale.Count}, >= 10 MB)");
            w.WriteLine();
            w.WriteLine("| Size | Last modified | Age | Path |");
            w.WriteLine("|---|---|---|---|");
            foreach (var f in stale)
                w.WriteLine($"| {Fmt.Size(f.Value)} | {Fmt.DateShort(f.Modified)} | {Fmt.Age(f.Modified)} | {f.FullPath} |");
            w.WriteLine();
        }

        // ---- File types
        var exts = Analysis.Extensions(roots, ct);
        long allFiles = exts.Sum(e => e.Size);
        var cats = Analysis.CategoryTotals(exts);
        w.WriteLine("## Space by file category");
        w.WriteLine();
        w.WriteLine("| Category | Size | % of files |");
        w.WriteLine("|---|---|---|");
        foreach (var i in Enumerable.Range(0, cats.Length).OrderByDescending(i => cats[i]))
            if (cats[i] > 0) w.WriteLine($"| {FileTypes.Categories[i].Name} | {Fmt.Size(cats[i])} | {Pct(cats[i], allFiles)} |");
        w.WriteLine();
        w.WriteLine("## Top file extensions");
        w.WriteLine();
        w.WriteLine("| Extension | Category | Size | % | Files | Avg size |");
        w.WriteLine("|---|---|---|---|---|---|");
        foreach (var e in exts.Take(40))
            w.WriteLine($"| {e.Ext} | {FileTypes.Categories[e.Category].Name} | {Fmt.Size(e.Size)} | {Pct(e.Size, allFiles)} | {e.Count:N0} | {Fmt.Size(e.Count > 0 ? e.Size / e.Count : 0)} |");
        w.WriteLine();

        // ---- Errors
        var errors = sc.Errors.ToArray();
        w.WriteLine($"## Unreadable folders ({sc.ErrorCount:N0} total{(errors.Length > 50 ? ", first 50 shown" : "")})");
        w.WriteLine();
        if (errors.Length == 0) w.WriteLine("None.");
        else
        {
            if (!admin) w.WriteLine("Tip: run DiskHog as administrator to read most of these.\n");
            w.WriteLine("| Path | Error |");
            w.WriteLine("|---|---|");
            foreach (var e in errors.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Take(50)) w.WriteLine($"| {e.Path} | {e.Message} |");
        }
        w.WriteLine();
        if (pending.Count > 0)
        {
            w.WriteLine($"## Folders not scanned yet (first {Math.Min(50, pending.Count)} of {pending.Count:N0})");
            w.WriteLine();
            foreach (var p in pending.Take(50)) w.WriteLine("- " + p.FullPath);
            w.WriteLine();
        }
    }

    static void WriteTree(StreamWriter w, Node n, int depth, long parentValue, long min, int maxDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long v = n.Value;
        string indent = new string(' ', depth * 2);
        string extra = "";
        if (n is DirNode d)
        {
            extra = $"files={d.FileCount:N0}  newest={Fmt.DateShort(d.Modified)}";
            if (d.State != DirState.Done) extra += $"  [{d.StateText}]";
        }
        else if (n.IsVirtual) extra = "(not a real file)";
        else extra = $"[file]  modified={Fmt.DateShort(n.Modified)}";
        string pct = depth == 0 ? "100%" : Pct(v, parentValue);
        w.WriteLine($"{indent}{Fmt.Size(v),10}  {pct,6}  {extra}  {(n.IsVirtual ? n.Name : n.FullPath)}");

        if (n is not DirNode dir || depth >= maxDepth) return;
        int hidden = 0;
        long hiddenSize = 0;
        foreach (var c in dir.SortedBySize())
        {
            long cv = c.Value;
            if (cv >= min && cv > 0) WriteTree(w, c, depth + 1, v, min, maxDepth, ct);
            else if (cv > 0) { hidden++; hiddenSize += cv; }
        }
        if (hidden > 0)
            w.WriteLine($"{indent}  {Fmt.Size(hiddenSize),10}  {Pct(hiddenSize, v),6}  (+{hidden:N0} smaller items)");
    }

    static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    static void WriteCsv(StreamWriter w, List<RootNode> roots, long min, CancellationToken ct)
    {
        w.WriteLine("type,path,size_on_disk_bytes,logical_size_bytes,files_inside,folders_inside,last_modified,extension,note");
        TreeWalk.ForEach(roots, n =>
        {
            if (n.Value < min || n.Value <= 0) return;
            if (n is DirNode d)
                w.WriteLine($"folder,{Csv(d.FullPath)},{d.Alloc},{d.Size},{d.FileCount},{d.DirCount},{Fmt.Date(d.Modified)},,{Csv(d.StateText)}");
            else if (n.IsVirtual)
                w.WriteLine($"unaccounted,{Csv(n.FullPath)},{n.Alloc},{n.Size},,,,,");
            else
                w.WriteLine($"file,{Csv(n.FullPath)},{n.Alloc},{n.Size},,,{Fmt.Date(n.Modified)},{Csv(FileTypes.Ext(n.Name))},{(n.IsHardLinkDup ? "hard link (counted elsewhere)" : "")}");
        }, ct);
    }
}
