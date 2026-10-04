using System.IO.Compression;

namespace DiskHog;

/// <summary>
/// Saves / loads a whole scan (finished or partial) to a compressed .dhog file, so you can reopen results
/// instantly or continue an unfinished scan later.
/// </summary>
static class SessionIO
{
    const int Magic = 0x474F4844; // "DHOG"
    const int Version = 1;

    public sealed class Loaded
    {
        public List<RootNode> Roots = new();
        public List<ScanError> Errors = new();
        public long Files, Dirs, Bytes;
        public TimeSpan Elapsed;
        public DateTime Saved;
    }

    /// <summary>Runs work on a thread with a big stack (very deep folder trees recurse deeply).</summary>
    static void RunBigStack(Action work)
    {
        Exception error = null;
        var t = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } }, 256 * 1024 * 1024);
        t.Start();
        t.Join();
        if (error != null) throw new IOException(error.Message, error);
    }

    public static void Save(string file, Scanner scanner)
    {
        string tmp = file + ".tmp";
        RunBigStack(() =>
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(new BufferedStream(gz, 1 << 20)))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(DateTime.Now.Ticks);
                w.Write(scanner.FilesScanned);
                w.Write(scanner.DirsScanned);
                w.Write(scanner.BytesScanned);
                w.Write(scanner.Elapsed.Ticks);

                w.Write(scanner.Roots.Count);
                foreach (var r in scanner.Roots)
                {
                    w.Write(r.Name);
                    w.Write(r.IsDriveRoot);
                    w.Write(r.Label ?? "");
                    w.Write(r.FileSystem ?? "");
                    w.Write(r.VolumeTotal);
                    w.Write(r.VolumeFree);
                    w.Write(r.ClusterSize);
                    w.Write((byte)(r.SeekPenalty == null ? 0 : r.SeekPenalty.Value ? 2 : 1));
                    w.Write(r.UseFindFallback);
                    w.Write(r.DedupeHardLinks);
                    w.Write(r.ScanStarted.Ticks);
                    w.Write(r.ScanFinished.Ticks);
                    WriteNode(w, r);
                    var links = r.Links.Snapshot();
                    w.Write(links.Count);
                    foreach (var id in links) w.Write(id);
                }

                var errors = scanner.Errors.ToArray();
                w.Write(errors.Length);
                foreach (var e in errors) { w.Write(e.Path ?? ""); w.Write(e.Code); }
            }
            File.Move(tmp, file, true);
        });
    }

    static void WriteNode(BinaryWriter w, Node n)
    {
        if (n is FileNode f)
        {
            w.Write((byte)0);
            w.Write(f.Name);
            w.Write(f.Size);
            w.Write(f.Alloc);
            w.Write(f.Modified);
            w.Write(f.Attr);
            w.Write((byte)f.Flags);
            w.Write(f.FileId);
            return;
        }
        var d = (DirNode)n;
        w.Write((byte)1);
        w.Write(d.Name);
        w.Write(d.Modified);
        w.Write(d.Attr);
        w.Write((byte)d.State);
        w.Write(d.ErrorCode);
        var ch = d.Children;
        if (ch == null) { w.Write(-1); return; }
        int count = 0;
        foreach (var c in ch) if (!c.IsVirtual) count++;
        w.Write(count);
        foreach (var c in ch) if (!c.IsVirtual) WriteNode(w, c);
    }

    public static Loaded Load(string file)
    {
        var result = new Loaded();
        RunBigStack(() =>
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(gz, 1 << 20));
            if (r.ReadInt32() != Magic) throw new InvalidDataException("Not a DiskHog session file.");
            int ver = r.ReadInt32();
            if (ver != Version) throw new InvalidDataException($"Unsupported session version {ver}.");
            result.Saved = new DateTime(r.ReadInt64());
            result.Files = r.ReadInt64();
            result.Dirs = r.ReadInt64();
            result.Bytes = r.ReadInt64();
            result.Elapsed = new TimeSpan(r.ReadInt64());

            int rootCount = r.ReadInt32();
            for (int i = 0; i < rootCount; i++)
            {
                var root = new RootNode
                {
                    Name = r.ReadString(),
                    IsDriveRoot = r.ReadBoolean(),
                    Label = r.ReadString(),
                    FileSystem = r.ReadString(),
                    VolumeTotal = r.ReadInt64(),
                    VolumeFree = r.ReadInt64(),
                    ClusterSize = r.ReadInt64(),
                };
                byte sp = r.ReadByte();
                root.SeekPenalty = sp == 0 ? null : sp == 2;
                root.UseFindFallback = r.ReadBoolean();
                root.DedupeHardLinks = r.ReadBoolean();
                root.ScanStarted = new DateTime(r.ReadInt64());
                root.ScanFinished = new DateTime(r.ReadInt64());
                if (r.ReadByte() != 1) throw new InvalidDataException("Corrupt session file.");
                root.Name = r.ReadString();
                ReadDirBody(r, root);
                int links = r.ReadInt32();
                for (int k = 0; k < links; k++) root.Links.Add(r.ReadInt64());
                result.Roots.Add(root);
            }

            int errCount = r.ReadInt32();
            for (int i = 0; i < errCount; i++)
                result.Errors.Add(new ScanError { Path = r.ReadString(), Code = r.ReadInt32() });
        });
        foreach (var root in result.Roots) TreeOps.UpdateUnaccounted(root);
        return result;
    }

    static void ReadDirBody(BinaryReader r, DirNode d)
    {
        d.Modified = r.ReadInt64();
        d.Attr = r.ReadUInt32();
        d.State = (DirState)r.ReadByte();
        d.ErrorCode = r.ReadInt32();
        int count = r.ReadInt32();
        if (count < 0) return;
        var children = new Node[count];
        long size = 0, alloc = 0, files = 0, dirs = 0;
        for (int i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            if (kind == 0)
            {
                var f = new FileNode
                {
                    Name = r.ReadString(), Parent = d,
                    Size = r.ReadInt64(), Alloc = r.ReadInt64(), Modified = r.ReadInt64(),
                    Attr = r.ReadUInt32(), Flags = (NodeFlags)r.ReadByte(), FileId = r.ReadInt64(),
                };
                children[i] = f;
                size += f.CountedSize;
                alloc += f.CountedAlloc;
                files++;
            }
            else if (kind == 1)
            {
                var sub = new DirNode { Name = r.ReadString(), Parent = d };
                ReadDirBody(r, sub);
                children[i] = sub;
                size += sub.Size;
                alloc += sub.Alloc;
                files += sub.FileCount;
                dirs += sub.DirCount + 1;
            }
            else throw new InvalidDataException("Corrupt session file.");
        }
        d.Children = children;
        d.Size = size;
        d.Alloc = alloc;
        d.FileCount = files;
        d.DirCount = dirs;
    }
}
