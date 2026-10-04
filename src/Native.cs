using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskHog;

/// <summary>One raw directory entry as returned by the OS.</summary>
struct RawEntry
{
    public string Name;
    public uint Attr;
    public long Size;       // logical size (EndOfFile)
    public long Alloc;      // bytes actually allocated on disk
    public long LastWrite;  // FILETIME (UTC)
    public long FileId;     // NTFS file reference number (0 if unknown)
    public uint ReparseTag;
}

static unsafe class Native
{
    public const uint FILE_ATTRIBUTE_READONLY = 0x1;
    public const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
    public const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x200;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_ATTRIBUTE_COMPRESSED = 0x800;
    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INVALID_LEVEL = 124;
    public const int CANCELLED = -1;

    const uint FILE_LIST_DIRECTORY = 0x1;
    const uint SYNCHRONIZE = 0x100000;
    const uint SHARE_ALL = 0x7;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const int FileIdBothDirectoryInfo = 10;
    const int FileIdBothDirectoryRestartInfo = 11;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int infoClass, void* buffer, uint bufferSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern IntPtr FindFirstFileExW(string lpFileName, int infoLevel, out WIN32_FIND_DATAW data,
        int searchOp, IntPtr searchFilter, int additionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern bool FindNextFileW(IntPtr hFind, out WIN32_FIND_DATAW data);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern bool FindClose(IntPtr hFind);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize,
        void* outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern bool LookupPrivilegeValueW(string system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct STORAGE_PROPERTY_QUERY { public int PropertyId; public int QueryType; public int Additional; }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVICE_SEEK_PENALTY_DESCRIPTOR { public uint Version; public uint Size; public byte IncursSeekPenalty; }

    /// <summary>Converts a normal path to the \\?\ form so paths longer than 260 chars work.</summary>
    public static string ToExtended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    /// <summary>
    /// Fast path: one handle per directory, entries pulled in 64 KB batches. Gives allocation size
    /// (real size on disk, handles compression / sparse / cloud placeholders) and the file ID for
    /// hard-link de-duplication without opening any file. Returns 0 or a Win32 error code.
    /// </summary>
    public static int EnumerateById(string extPath, byte* buf, int bufSize, List<RawEntry> output, CancellationToken ct)
    {
        using var h = CreateFileW(extPath, FILE_LIST_DIRECTORY | SYNCHRONIZE, SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return Marshal.GetLastWin32Error();

        int infoClass = FileIdBothDirectoryRestartInfo;
        while (true)
        {
            if (!GetFileInformationByHandleEx(h, infoClass, buf, (uint)bufSize))
            {
                int err = Marshal.GetLastWin32Error();
                return err == ERROR_NO_MORE_FILES ? 0 : err;
            }
            infoClass = FileIdBothDirectoryInfo;

            // FILE_ID_BOTH_DIR_INFO layout (x64): NextEntryOffset@0, LastWriteTime@24, EndOfFile@40,
            // AllocationSize@48, FileAttributes@56, FileNameLength@60, EaSize(reparse tag)@64,
            // FileId@96, FileName@104
            byte* p = buf;
            while (true)
            {
                uint next = *(uint*)p;
                int nameLen = (int)(*(uint*)(p + 60) / 2);
                char* name = (char*)(p + 104);
                bool dot = (nameLen == 1 && name[0] == '.') || (nameLen == 2 && name[0] == '.' && name[1] == '.');
                if (!dot)
                {
                    uint attr = *(uint*)(p + 56);
                    output.Add(new RawEntry
                    {
                        Name = new string(name, 0, nameLen),
                        Attr = attr,
                        LastWrite = *(long*)(p + 24),
                        Size = *(long*)(p + 40),
                        Alloc = *(long*)(p + 48),
                        ReparseTag = (attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? *(uint*)(p + 64) : 0,
                        FileId = *(long*)(p + 96),
                    });
                }
                if (next == 0) break;
                p += next;
            }
            if (ct.IsCancellationRequested) return CANCELLED;
        }
    }

    /// <summary>Fallback for file systems that don't support the fast path (FAT, exFAT, some network shares).</summary>
    public static int EnumerateFind(string extPath, List<RawEntry> output, long clusterSize, CancellationToken ct)
    {
        string pattern = extPath.EndsWith('\\') ? extPath + "*" : extPath + "\\*";
        IntPtr hFind = FindFirstFileExW(pattern, 1 /*FindExInfoBasic*/, out var d, 0, IntPtr.Zero, 2 /*LARGE_FETCH*/);
        if (hFind == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            return err == 2 /*FILE_NOT_FOUND (empty)*/ ? 0 : err;
        }
        try
        {
            if (clusterSize <= 0) clusterSize = 4096;
            int n = 0;
            do
            {
                string nm = d.cFileName;
                if (nm == "." || nm == "..") continue;
                long size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                bool isDir = (d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                output.Add(new RawEntry
                {
                    Name = nm,
                    Attr = d.dwFileAttributes,
                    LastWrite = d.ftLastWriteTime,
                    Size = isDir ? 0 : size,
                    Alloc = isDir ? 0 : (size + clusterSize - 1) / clusterSize * clusterSize,
                    ReparseTag = (d.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? d.dwReserved0 : 0,
                    FileId = 0,
                });
                if ((++n & 1023) == 0 && ct.IsCancellationRequested) return CANCELLED;
            } while (FindNextFileW(hFind, out d));
            int last = Marshal.GetLastWin32Error();
            return last == ERROR_NO_MORE_FILES ? 0 : last;
        }
        finally { FindClose(hFind); }
    }

    /// <summary>Junctions, symlinks and mount points are "name surrogates"; following them would double count.</summary>
    public static bool IsNameSurrogate(uint reparseTag) => (reparseTag & 0x20000000) != 0;

    public static long GetClusterSize(string root)
    {
        try
        {
            if (GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _)) return (long)spc * bps;
        }
        catch { }
        return 4096;
    }

    /// <summary>true = spinning HDD, false = SSD, null = unknown.</summary>
    public static bool? HasSeekPenalty(string driveRoot)
    {
        try
        {
            if (driveRoot.Length < 2 || driveRoot[1] != ':') return null;
            using var h = CreateFileW($@"\\.\{driveRoot[0]}:", 0, 3, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var q = new STORAGE_PROPERTY_QUERY { PropertyId = 7 /*StorageDeviceSeekPenaltyProperty*/, QueryType = 0 };
            DEVICE_SEEK_PENALTY_DESCRIPTOR d;
            if (!DeviceIoControl(h, 0x2D1400 /*IOCTL_STORAGE_QUERY_PROPERTY*/, &q, (uint)sizeof(STORAGE_PROPERTY_QUERY),
                    &d, (uint)sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR), out _, IntPtr.Zero))
                return null;
            return d.IncursSeekPenalty != 0;
        }
        catch { return null; }
    }

    /// <summary>
    /// When running as admin, SeBackupPrivilege lets us list folders whose ACLs would otherwise deny us
    /// (other users' profiles, System Volume Information, ...). Read-only; nothing is modified.
    /// </summary>
    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8 /*ADJUST_PRIVILEGES|QUERY*/, out var tok)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, name, out long luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /*SE_PRIVILEGE_ENABLED*/ };
            if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // 1300 = not all assigned
        }
        finally { CloseHandle(tok); }
    }
}
