using System.Text.RegularExpressions;

namespace DiskHog;

sealed class ExtStat
{
    public string Ext;
    public int Category;
    public long Size;
    public long Count;
}

static class Analysis
{
    /// <summary>Builds a matcher for the "name contains" box. Supports * and ? wildcards; a backslash matches against the full path.</summary>
    public static Func<FileNode, bool> NameMatcher(string filter)
    {
        filter = (filter ?? "").Trim();
        if (filter.Length == 0) return null;
        bool usePath = filter.Contains('\\');
        if (filter.Contains('*') || filter.Contains('?'))
        {
            var rx = new Regex("^" + Regex.Escape(filter).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return usePath ? f => rx.IsMatch(f.FullPath) : f => rx.IsMatch(f.Name);
        }
        return usePath
            ? f => f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            : f => f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Largest files (by the current size metric) matching the filters, biggest first.</summary>
    public static List<FileNode> TopFiles(IEnumerable<DirNode> roots, int max, long minSize, long olderThanFileTime,
        Func<FileNode, bool> nameMatch, CancellationToken ct)
    {
        var heap = new PriorityQueue<FileNode, long>();
        TreeWalk.ForEach(roots, n =>
        {
            if (n is not FileNode f || f.IsVirtual || f.IsHardLinkDup) return;
            long v = f.Value;
            if (v < minSize) return;
            if (heap.Count >= max && heap.TryPeek(out _, out long smallest) && v <= smallest) return;
            if (olderThanFileTime > 0 && (f.Modified == 0 || f.Modified > olderThanFileTime)) return;
            if (nameMatch != null && !nameMatch(f)) return;
            heap.Enqueue(f, v);
            if (heap.Count > max) heap.Dequeue();
        }, ct);
        var list = new List<FileNode>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }

    public static List<ExtStat> Extensions(IEnumerable<DirNode> roots, CancellationToken ct)
    {
        var map = new Dictionary<string, ExtStat>(StringComparer.OrdinalIgnoreCase);
        var lookup = map.GetAlternateLookup<ReadOnlySpan<char>>();
        var noExt = new ExtStat { Ext = "(no extension)", Category = FileTypes.Other };
        TreeWalk.ForEach(roots, n =>
        {
            if (n is not FileNode f || f.IsVirtual || f.IsHardLinkDup) return;
            var e = FileTypes.ExtSpan(f.Name);
            ExtStat s;
            if (e.IsEmpty || e.Length > 16) s = noExt;
            else if (!lookup.TryGetValue(e, out s))
            {
                string key = e.ToString().ToLowerInvariant();
                s = new ExtStat { Ext = "." + key, Category = FileTypes.CategoryOf(f.Name) };
                map[key] = s;
            }
            s.Size += f.Value;
            s.Count++;
        }, ct);
        var list = map.Values.ToList();
        if (noExt.Count > 0) list.Add(noExt);
        list.Sort((a, b) => b.Size.CompareTo(a.Size));
        return list;
    }

    public static long[] CategoryTotals(List<ExtStat> exts)
    {
        var totals = new long[FileTypes.Categories.Length];
        foreach (var e in exts) totals[e.Category] += e.Size;
        return totals;
    }
}
