using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskHog;

sealed class ScanError
{
    public string Path;
    public int Code;
    public string Message => Fmt.Win32Message(Code);
}

enum ScanState { Idle, Running, Paused, Stopping }

/// <summary>
/// Multithreaded folder scanner. Each root gets its own pool of worker threads (sized by drive type).
/// Invariant that makes pause / stop / save / resume safe: a folder is either fully processed
/// (children attached, totals rolled up) or still Pending. Never half-done.
/// </summary>
sealed unsafe class Scanner
{
    const int BufSize = 64 * 1024;
    const int MaxStoredErrors = 20000;

    sealed class Pool
    {
        public RootNode Root;
        public readonly ConcurrentStack<DirNode> Stack = new();
        public readonly SemaphoreSlim Sem = new(0);
        public int Outstanding;
        public int Threads;
        public int Alive;
        public volatile bool Done;
    }

    public readonly List<RootNode> Roots = new();
    public readonly ConcurrentQueue<ScanError> Errors = new();
    public long FilesScanned, DirsScanned, BytesScanned, ErrorCount;
    public volatile DirNode CurrentDir;

    /// <summary>Raised on a worker thread when every pool has finished or stopped.</summary>
    public event Action Finished;

    readonly ManualResetEventSlim _gate = new(true);
    readonly Stopwatch _sw = new();
    TimeSpan _elapsedBefore;
    CancellationTokenSource _cts = new();
    int _busy;
    int _livePools;
    volatile ScanState _state = ScanState.Idle;

    public ScanState State => _state;
    public bool IsActive => _state != ScanState.Idle;
    public TimeSpan Elapsed => _elapsedBefore + _sw.Elapsed;
    public bool WasCancelled { get; private set; }

    public void ResetStats()
    {
        FilesScanned = DirsScanned = BytesScanned = ErrorCount = 0;
        Errors.Clear();
        _elapsedBefore = TimeSpan.Zero;
        _sw.Reset();
    }

    /// <summary>Restores counters when a saved session is loaded.</summary>
    public void RestoreStats(long files, long dirs, long bytes, TimeSpan elapsed, IEnumerable<ScanError> errors)
    {
        FilesScanned = files; DirsScanned = dirs; BytesScanned = bytes;
        _elapsedBefore = elapsed;
        _sw.Reset();
        Errors.Clear();
        foreach (var e in errors) Errors.Enqueue(e);
        ErrorCount = Errors.Count;
    }

    public void ClearErrors()
    {
        Errors.Clear();
        ErrorCount = 0;
    }

    /// <summary>Drops recorded errors inside a folder that is about to be rescanned.</summary>
    public void RemoveErrorsUnder(string path)
    {
        string prefix = path.EndsWith('\\') ? path : path + "\\";
        var keep = Errors.Where(e => !e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                                     !e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        Errors.Clear();
        foreach (var e in keep) Errors.Enqueue(e);
        ErrorCount = keep.Count;
    }

    /// <summary>All folders that still need to be read (used for resume / continue).</summary>
    public List<DirNode> CollectPending()
    {
        var list = new List<DirNode>();
        TreeWalk.ForEach(Roots, n => { if (n is DirNode d && d.State == DirState.Pending) list.Add(d); });
        return list;
    }

    public void Start(IEnumerable<DirNode> pending, AppSettings settings)
    {
        if (IsActive) throw new InvalidOperationException("Scan already running.");
        var groups = pending.GroupBy(d => d.Root).Where(g => g.Key != null).ToList();
        if (groups.Count == 0) return;

        _cts = new CancellationTokenSource();
        WasCancelled = false;
        _gate.Set();
        _state = ScanState.Running;
        _sw.Start();

        var pools = new List<Pool>();
        foreach (var g in groups)
        {
            var root = g.Key;
            if (root.ScanStarted == default) root.ScanStarted = DateTime.Now;
            root.DedupeHardLinks = settings.DedupeHardLinks &&
                                   string.Equals(root.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
            int threads = root.SeekPenalty switch
            {
                true => settings.HddThreads,
                false => settings.SsdThreads,
                _ => settings.OtherThreads,
            };
            var pool = new Pool { Root = root, Threads = Math.Max(1, threads) };
            var dirs = g.ToArray();
            pool.Outstanding = dirs.Length;
            pool.Stack.PushRange(dirs.Reverse().ToArray());
            pool.Sem.Release(dirs.Length);
            pools.Add(pool);
        }

        _livePools = pools.Count;
        foreach (var pool in pools)
        {
            pool.Alive = pool.Threads;
            for (int i = 0; i < pool.Threads; i++)
            {
                var t = new Thread(() => Worker(pool), 256 * 1024)
                {
                    IsBackground = true,
                    Name = "DiskHog scan " + pool.Root.Name,
                    Priority = ThreadPriority.BelowNormal,
                };
                t.Start();
            }
        }
    }

    public void Pause()
    {
        if (_state != ScanState.Running) return;
        _gate.Reset();
        _sw.Stop();
        _state = ScanState.Paused;
    }

    public void Resume()
    {
        if (_state != ScanState.Paused) return;
        _state = ScanState.Running;
        _sw.Start();
        _gate.Set();
    }

    public void Stop()
    {
        if (!IsActive) return;
        WasCancelled = true;
        _state = ScanState.Stopping;
        _cts.Cancel();
        _gate.Set();
    }

    /// <summary>Waits until no worker is in the middle of a folder (safe point for saving while paused).</summary>
    public bool WaitQuiescent(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _busy) > 0)
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    void Worker(Pool pool)
    {
        byte* buf = (byte*)NativeMemory.Alloc(BufSize);
        var entries = new List<RawEntry>(512);
        var token = _cts.Token;
        try
        {
            while (true)
            {
                try
                {
                    pool.Sem.Wait(token);
                    _gate.Wait(token);
                }
                catch (OperationCanceledException) { break; }

                if (pool.Done || token.IsCancellationRequested) break;
                if (!pool.Stack.TryPop(out var dir)) continue;

                bool completed;
                Interlocked.Increment(ref _busy);
                try { completed = ScanDir(pool, dir, buf, entries, token); }
                catch (Exception ex)
                {
                    // Should never happen, but never let one weird folder kill the scan.
                    dir.State = DirState.Error;
                    dir.ErrorCode = ex.HResult & 0xFFFF;
                    AddError(dir, dir.ErrorCode);
                    completed = true;
                }
                finally { Interlocked.Decrement(ref _busy); }

                if (!completed)
                {
                    // Cancelled mid-folder: leave it Pending so a later resume picks it up.
                    pool.Stack.Push(dir);
                    break;
                }

                if (Interlocked.Decrement(ref pool.Outstanding) == 0)
                {
                    pool.Done = true;
                    pool.Sem.Release(pool.Threads);
                    break;
                }
            }
        }
        finally
        {
            NativeMemory.Free(buf);
            if (Interlocked.Decrement(ref pool.Alive) == 0)
            {
                pool.Root.ScanFinished = DateTime.Now;
                if (Interlocked.Decrement(ref _livePools) == 0) OnAllPoolsDone();
            }
        }
    }

    void OnAllPoolsDone()
    {
        _sw.Stop();
        _elapsedBefore += _sw.Elapsed;
        _sw.Reset();
        _gate.Set();
        _state = ScanState.Idle;
        Finished?.Invoke();
    }

    void AddError(DirNode dir, int code)
    {
        Interlocked.Increment(ref ErrorCount);
        if (Errors.Count < MaxStoredErrors)
            Errors.Enqueue(new ScanError { Path = dir.FullPath, Code = code });
    }

    /// <summary>Returns false only if cancelled before the folder could be committed.</summary>
    bool ScanDir(Pool pool, DirNode dir, byte* buf, List<RawEntry> entries, CancellationToken token)
    {
        var root = pool.Root;
        CurrentDir = dir;
        entries.Clear();
        string path = Native.ToExtended(dir.FullPath);

        int err = 0;
        if (!root.UseFindFallback)
        {
            err = Native.EnumerateById(path, buf, BufSize, entries, token);
            if (err is Native.ERROR_INVALID_PARAMETER or Native.ERROR_INVALID_LEVEL
                    or Native.ERROR_NOT_SUPPORTED or Native.ERROR_INVALID_FUNCTION)
            {
                root.UseFindFallback = true;
                entries.Clear();
            }
        }
        if (root.UseFindFallback)
            err = Native.EnumerateFind(path, entries, root.ClusterSize, token);

        if (err == Native.CANCELLED) return false;
        if (err != 0)
        {
            dir.ErrorCode = err;
            dir.State = DirState.Error;
            AddError(dir, err);
            Interlocked.Increment(ref DirsScanned);
            return true;
        }

        bool dedupe = root.DedupeHardLinks;
        long fSize = 0, fAlloc = 0, fCount = 0, dCount = 0, newest = 0;
        var children = new Node[entries.Count];
        var subdirs = new List<DirNode>();
        for (int i = 0; i < entries.Count; i++)
        {
            ref var e = ref CollectionsMarshal.AsSpan(entries)[i];
            if ((e.Attr & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                var sub = new DirNode { Name = e.Name, Parent = dir, Attr = e.Attr, Modified = e.LastWrite };
                if (e.ReparseTag != 0 && Native.IsNameSurrogate(e.ReparseTag))
                    sub.State = DirState.Link;
                else
                    subdirs.Add(sub);
                children[i] = sub;
                dCount++;
                if (e.LastWrite > newest) newest = e.LastWrite;
            }
            else
            {
                var f = new FileNode
                {
                    Name = e.Name, Parent = dir, Attr = e.Attr,
                    Size = e.Size, Alloc = e.Alloc, Modified = e.LastWrite, FileId = e.FileId,
                };
                if (dedupe && e.FileId != 0 && !root.Links.Add(e.FileId))
                    f.Flags |= NodeFlags.HardLinkDup;
                else
                {
                    fSize += e.Size;
                    fAlloc += e.Alloc;
                }
                children[i] = f;
                fCount++;
                if (e.LastWrite > newest) newest = e.LastWrite;
            }
        }

        // Commit: from here on nothing is interruptible.
        Volatile.Write(ref dir.Children, children);
        dir.State = DirState.Done;
        for (DirNode p = dir; p != null; p = p.Parent)
        {
            Interlocked.Add(ref p.Size, fSize);
            Interlocked.Add(ref p.Alloc, fAlloc);
            Interlocked.Add(ref p.FileCount, fCount);
            Interlocked.Add(ref p.DirCount, dCount);
            long cur;
            while ((cur = Volatile.Read(ref p.Modified)) < newest &&
                   Interlocked.CompareExchange(ref p.Modified, newest, cur) != cur) { }
        }
        Interlocked.Add(ref FilesScanned, fCount);
        Interlocked.Increment(ref DirsScanned);
        Interlocked.Add(ref BytesScanned, fAlloc);

        if (subdirs.Count > 0)
        {
            Interlocked.Add(ref pool.Outstanding, subdirs.Count);
            subdirs.Reverse(); // so the first child is popped first (LIFO)
            pool.Stack.PushRange(subdirs.ToArray());
            pool.Sem.Release(subdirs.Count);
        }
        return true;
    }
}
