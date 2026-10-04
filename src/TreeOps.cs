namespace DiskHog;

static class TreeOps
{
    public const string UnaccountedName = "<Unaccounted: system / protected / metadata>";

    public static string NormalizeRoot(string path)
    {
        path = Path.GetFullPath(path.Trim());
        string vol = Path.GetPathRoot(path) ?? path;
        if (string.Equals(path.TrimEnd('\\'), vol.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return vol.EndsWith('\\') ? vol : vol + "\\";
        return path.TrimEnd('\\');
    }

    public static RootNode CreateRoot(string path)
    {
        path = NormalizeRoot(path);
        string vol = Path.GetPathRoot(path) ?? path;
        var r = new RootNode { Name = path, State = DirState.Pending };
        r.IsDriveRoot = string.Equals(path, vol, StringComparison.OrdinalIgnoreCase);
        RefreshVolume(r);
        r.ClusterSize = Native.GetClusterSize(vol);
        r.SeekPenalty = Native.HasSeekPenalty(vol);
        try
        {
            r.Attr = (uint)File.GetAttributes(path);
            r.Modified = Directory.GetLastWriteTimeUtc(path).ToFileTimeUtc();
        }
        catch { }
        return r;
    }

    public static void RefreshVolume(RootNode r)
    {
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(r.Name));
            r.Label = di.VolumeLabel ?? "";
            r.FileSystem = di.DriveFormat ?? "";
            r.VolumeTotal = di.TotalSize;
            r.VolumeFree = di.TotalFreeSpace;
        }
        catch { }
    }

    /// <summary>True if any folder under this directory hasn't been read yet.</summary>
    public static bool HasPending(DirNode d)
    {
        bool any = false;
        try
        {
            using var cts = new CancellationTokenSource();
            TreeWalk.ForEach(new[] { d }, n =>
            {
                if (n is DirNode x && x.State == DirState.Pending) { any = true; cts.Cancel(); }
            }, cts.Token);
        }
        catch (OperationCanceledException) { }
        return any;
    }

    static void AddToAncestors(DirNode start, long size, long alloc, long files, long dirs)
    {
        for (var p = start; p != null; p = p.Parent)
        {
            Interlocked.Add(ref p.Size, size);
            Interlocked.Add(ref p.Alloc, alloc);
            Interlocked.Add(ref p.FileCount, files);
            Interlocked.Add(ref p.DirCount, dirs);
        }
    }

    public static void RemoveUnaccounted(RootNode r)
    {
        var u = r.UnaccountedNode;
        if (u == null) return;
        r.UnaccountedNode = null;
        var ch = r.Children;
        if (ch != null) Volatile.Write(ref r.Children, ch.Where(c => c != u).ToArray());
        r.Size -= u.Size;
        r.Alloc -= u.Alloc;
        Display.Invalidate();
    }

    /// <summary>
    /// For a fully scanned drive: used space reported by Windows minus what the scan found. This is NTFS
    /// metadata ($MFT etc.), System Volume Information (restore points / shadow copies) and unreadable folders.
    /// Shown as a grey block so the numbers add up to what Explorer reports.
    /// </summary>
    public static void UpdateUnaccounted(RootNode r)
    {
        RemoveUnaccounted(r);
        if (!r.IsDriveRoot || r.State == DirState.Pending || r.VolumeTotal <= 0 || HasPending(r)) return;
        long unacc = r.VolumeUsed - r.Alloc;
        if (unacc <= 0) return;
        var u = new FileNode
        {
            Name = UnaccountedName, Parent = r, Size = unacc, Alloc = unacc,
            Flags = NodeFlags.Virtual, Modified = 0,
        };
        r.UnaccountedNode = u;
        var ch = r.Children ?? Array.Empty<Node>();
        Volatile.Write(ref r.Children, ch.Append(u).ToArray());
        r.Size += unacc;
        r.Alloc += unacc;
        Display.Invalidate();
    }

    static void ForgetHardLinks(Node n, RootNode root)
    {
        if (root == null) return;
        if (n is FileNode f) { if (!f.IsHardLinkDup && f.FileId != 0) root.Links.Remove(f.FileId); return; }
        TreeWalk.ForEach(new[] { (DirNode)n }, x =>
        {
            if (x is FileNode ff && !ff.IsHardLinkDup && ff.FileId != 0) root.Links.Remove(ff.FileId);
        });
    }

    /// <summary>Resets a folder to Pending (and backs its totals out of its ancestors) so it can be scanned again.</summary>
    public static void PrepareRescan(DirNode d)
    {
        var root = d.Root;
        if (root != null) RemoveUnaccounted(root);
        ForgetHardLinks(d, root);
        AddToAncestors(d.Parent, -d.Size, -d.Alloc, -d.FileCount, -d.DirCount);
        Volatile.Write(ref d.Children, null);
        d.Size = d.Alloc = d.FileCount = d.DirCount = 0;
        d.ErrorCode = 0;
        d.State = DirState.Pending;
        Display.Invalidate();
    }

    /// <summary>Removes a node from the tree after it was deleted from disk.</summary>
    public static void RemoveNode(Node n)
    {
        var parent = n.Parent;
        if (parent == null) return;
        ForgetHardLinks(n, n.Root);
        long files = n is DirNode dn ? dn.FileCount : 1;
        long dirs = n is DirNode dn2 ? dn2.DirCount + 1 : 0;
        Volatile.Write(ref parent.Children, parent.GetChildren().Where(c => c != n).ToArray());
        AddToAncestors(parent, -n.CountedSize, -n.CountedAlloc, -files, -dirs);
        Display.Invalidate();
    }

    /// <summary>Finds the node for a full path inside the scanned roots (case-insensitive), or null.</summary>
    public static Node FindByPath(IEnumerable<RootNode> roots, string path)
    {
        foreach (var r in roots)
        {
            string rp = r.Name.TrimEnd('\\');
            if (!path.StartsWith(rp, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = path.Substring(rp.Length).Trim('\\');
            Node cur = r;
            if (rest.Length > 0)
            {
                foreach (var seg in rest.Split('\\'))
                {
                    cur = (cur as DirNode)?.FindChild(seg);
                    if (cur == null) break;
                }
            }
            if (cur != null) return cur;
        }
        return null;
    }
}
