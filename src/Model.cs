using System.Text;

namespace DiskHog;

[Flags]
enum NodeFlags : byte { None = 0, HardLinkDup = 1, Virtual = 2 }

enum DirState : byte { Pending, Done, Error, Link }

/// <summary>Global view settings shared by every panel.</summary>
static class Display
{
    /// <summary>true = "size on disk" (allocated), false = logical file size.</summary>
    public static bool OnDisk = true;
    /// <summary>Bumped whenever sizes/children change so cached sort orders get rebuilt.</summary>
    public static int SortVersion;
    public static void Invalidate() => Interlocked.Increment(ref SortVersion);
}

abstract class Node
{
    public string Name;
    public DirNode Parent;
    public long Size;      // logical bytes (dirs: sum of counted children)
    public long Alloc;     // allocated bytes on disk (dirs: sum of counted children)
    public long Modified;  // FILETIME UTC. Files: last write. Dirs: newest last-write of anything inside.
    public uint Attr;
    public NodeFlags Flags;

    public abstract bool IsDir { get; }
    public bool IsVirtual => (Flags & NodeFlags.Virtual) != 0;
    public bool IsHardLinkDup => (Flags & NodeFlags.HardLinkDup) != 0;

    /// <summary>What this node contributes to its parent's total (hard-link duplicates contribute 0).</summary>
    public long CountedSize => IsHardLinkDup ? 0 : Volatile.Read(ref Size);
    public long CountedAlloc => IsHardLinkDup ? 0 : Volatile.Read(ref Alloc);

    /// <summary>Size in the currently selected metric.</summary>
    public long Value => Display.OnDisk ? CountedAlloc : CountedSize;

    /// <summary>Size in the currently selected metric, ignoring hard-link de-duplication (for display).</summary>
    public long RawValue => Display.OnDisk ? Volatile.Read(ref Alloc) : Volatile.Read(ref Size);

    public RootNode Root
    {
        get
        {
            Node n = this;
            while (n.Parent != null) n = n.Parent;
            return n as RootNode;
        }
    }

    public int Depth
    {
        get { int d = 0; for (var p = Parent; p != null; p = p.Parent) d++; return d; }
    }

    public string FullPath
    {
        get
        {
            if (IsVirtual) return (Parent?.FullPath ?? "") + " " + Name;
            if (Parent == null) return Name;
            int count = 0;
            for (Node n = this; n != null; n = n.Parent) count++;
            var parts = new string[count];
            int i = count;
            for (Node n = this; n != null; n = n.Parent) parts[--i] = n.Name;
            var sb = new StringBuilder(256);
            foreach (var part in parts)
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\\') sb.Append('\\');
                sb.Append(part);
            }
            return sb.ToString();
        }
    }

    public bool IsDescendantOf(Node ancestor)
    {
        for (var p = Parent; p != null; p = p.Parent)
            if (p == ancestor) return true;
        return false;
    }

    public override string ToString() => Name;
}

sealed class FileNode : Node
{
    public long FileId;
    public override bool IsDir => false;
}

class DirNode : Node
{
    public Node[] Children;       // null until scanned; assigned once by the scanner (atomically)
    public long FileCount;        // files in whole subtree
    public long DirCount;         // folders in whole subtree
    public DirState State;
    public int ErrorCode;

    Node[] _sorted;
    int _sortedVer = -1;

    public override bool IsDir => true;

    public Node[] GetChildren() => Volatile.Read(ref Children) ?? Array.Empty<Node>();

    /// <summary>Children sorted by size (current metric), largest first. Cached until Display.Invalidate().</summary>
    public Node[] SortedBySize()
    {
        var ch = Volatile.Read(ref Children);
        if (ch == null || ch.Length == 0) return Array.Empty<Node>();
        var s = _sorted;
        if (s != null && _sortedVer == Display.SortVersion && s.Length == ch.Length) return s;
        s = (Node[])ch.Clone();
        var keys = new long[s.Length];
        for (int i = 0; i < s.Length; i++) keys[i] = -s[i].Value;
        Array.Sort(keys, s);
        _sorted = s;
        _sortedVer = Display.SortVersion;
        return s;
    }

    public Node FindChild(string name)
    {
        foreach (var c in GetChildren())
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public string StateText => State switch
    {
        DirState.Pending => "not scanned yet",
        DirState.Error => "unreadable: " + Fmt.Win32Message(ErrorCode),
        DirState.Link => "link / junction (not followed)",
        _ => "",
    };
}

/// <summary>A scanned drive or folder at the top of the tree.</summary>
sealed class RootNode : DirNode
{
    public bool IsDriveRoot;
    public string Label = "";
    public string FileSystem = "";
    public long VolumeTotal;
    public long VolumeFree;
    public long ClusterSize = 4096;
    public bool? SeekPenalty;
    public volatile bool UseFindFallback;
    public bool DedupeHardLinks;
    public HardLinkSet Links = new();
    public DateTime ScanStarted;
    public DateTime ScanFinished;
    public FileNode UnaccountedNode;

    public long VolumeUsed => VolumeTotal - VolumeFree;

    public string MediaText => SeekPenalty switch { true => "HDD", false => "SSD", _ => "?" };
}

/// <summary>Thread-safe set of NTFS file IDs seen so far, so hard-linked files are only counted once.</summary>
sealed class HardLinkSet
{
    const int ShardCount = 64;
    readonly HashSet<long>[] _shards = new HashSet<long>[ShardCount];

    public HardLinkSet()
    {
        for (int i = 0; i < ShardCount; i++) _shards[i] = new HashSet<long>();
    }

    static HashSet<long> ShardOf(HashSet<long>[] s, long id) => s[(int)(((ulong)id * 0x9E3779B97F4A7C15UL) >> 58)];

    /// <summary>Returns true if this ID had not been seen before.</summary>
    public bool Add(long id) { var h = ShardOf(_shards, id); lock (h) return h.Add(id); }
    public void Remove(long id) { var h = ShardOf(_shards, id); lock (h) h.Remove(id); }

    public List<long> Snapshot()
    {
        var all = new List<long>();
        foreach (var h in _shards) lock (h) all.AddRange(h);
        return all;
    }

    public void Clear()
    {
        foreach (var h in _shards) lock (h) h.Clear();
    }
}

static class Fmt
{
    static readonly string[] Units = { "KB", "MB", "GB", "TB", "PB" };

    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double v = bytes;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < Units.Length - 1);
        return v >= 100 ? $"{v:0} {Units[i]}" : v >= 10 ? $"{v:0.0} {Units[i]}" : $"{v:0.00} {Units[i]}";
    }

    public static string Count(long n) => n.ToString("N0");

    public static DateTime? ToLocal(long fileTime)
    {
        if (fileTime <= 0) return null;
        try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); } catch { return null; }
    }

    public static string Date(long fileTime) => ToLocal(fileTime)?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public static string DateShort(long fileTime) => ToLocal(fileTime)?.ToString("yyyy-MM-dd") ?? "";

    public static string Age(long fileTime)
    {
        var d = ToLocal(fileTime);
        if (d == null) return "";
        var days = (DateTime.Now - d.Value).TotalDays;
        if (days < 1) return "today";
        if (days < 60) return $"{days:0} days ago";
        if (days < 730) return $"{days / 30.4:0} months ago";
        return $"{days / 365.25:0.#} years ago";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string Win32Message(int code)
    {
        if (code == 0) return "";
        try { return new System.ComponentModel.Win32Exception(code).Message + $" ({code})"; }
        catch { return "error " + code; }
    }
}

static class TreeWalk
{
    /// <summary>Visits every node under (and including) the given directories without recursion.</summary>
    public static void ForEach(IEnumerable<DirNode> starts, Action<Node> visit, CancellationToken ct = default)
    {
        var stack = new Stack<DirNode>();
        foreach (var s in starts) { visit(s); stack.Push(s); }
        int n = 0;
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var c in d.GetChildren())
            {
                visit(c);
                if (c is DirNode cd) stack.Push(cd);
            }
            if ((++n & 4095) == 0) ct.ThrowIfCancellationRequested();
        }
    }
}
