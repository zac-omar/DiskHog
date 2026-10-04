using System.ComponentModel;
using System.Text.Json;

namespace DiskHog;

public sealed class AppSettings
{
    [Category("1. Scanning"), DisplayName("Threads per SSD"),
     Description("Parallel folder readers for solid-state drives. 8 is a good default; more rarely helps.")]
    public int SsdThreads { get; set; } = 8;

    [Category("1. Scanning"), DisplayName("Threads per HDD"),
     Description("Parallel folder readers for spinning hard drives. 2 keeps the disk head from thrashing.")]
    public int HddThreads { get; set; } = 2;

    [Category("1. Scanning"), DisplayName("Threads for other drives"),
     Description("Used for network shares, USB sticks and anything whose type can't be detected.")]
    public int OtherThreads { get; set; } = 4;

    [Category("1. Scanning"), DisplayName("Count hard links once"),
     Description("Windows hard-links many files (especially C:\\Windows\\WinSxS). With this on, each physical file is only " +
                 "counted once, so totals match real disk usage. Recommended: on.")]
    public bool DedupeHardLinks { get; set; } = true;

    [Category("2. Reports"), DisplayName("Auto-save report after each scan"),
     Description("Writes a Markdown report (easy for you or an AI to read) into the reports folder whenever a scan finishes.")]
    public bool AutoSaveReport { get; set; } = true;

    [Category("2. Reports"), DisplayName("Reports folder"),
     Description("Leave empty to use a 'reports' folder next to DiskHog.exe.")]
    public string ReportFolder { get; set; } = "";

    [Category("2. Reports"), DisplayName("Folder tree: minimum size (MB)"),
     Description("Folders smaller than this are summarised instead of listed in the report's folder tree.")]
    public int ReportMinFolderMB { get; set; } = 500;

    [Category("2. Reports"), DisplayName("Folder tree: max depth"),
     Description("How many levels deep the report's folder tree goes.")]
    public int ReportTreeDepth { get; set; } = 8;

    [Category("2. Reports"), DisplayName("Largest files: how many"),
     Description("How many of the biggest files to list in the report.")]
    public int ReportTopFiles { get; set; } = 200;

    [Category("2. Reports"), DisplayName("Stale files: older than (days)"),
     Description("The report lists big files not modified for at least this many days (forgotten stuff).")]
    public int StaleDays { get; set; } = 365;

    [Category("2. Reports"), DisplayName("Also write CSV"),
     Description("Also export every file and folder above the CSV minimum size as a .csv spreadsheet.")]
    public bool WriteCsv { get; set; } = false;

    [Category("2. Reports"), DisplayName("CSV: minimum size (MB)"),
     Description("Items smaller than this are left out of the CSV export.")]
    public int CsvMinMB { get; set; } = 10;

    // ---- remembered UI state (hidden from the options grid) ----
    [Browsable(false)] public bool ShowLogicalSize { get; set; }
    [Browsable(false)] public List<string> CheckedRoots { get; set; } = new();
    [Browsable(false)] public List<string> ExtraFolders { get; set; } = new();
    [Browsable(false)] public int WinX { get; set; } = -1;
    [Browsable(false)] public int WinY { get; set; } = -1;
    [Browsable(false)] public int WinW { get; set; }
    [Browsable(false)] public int WinH { get; set; }
    [Browsable(false)] public bool Maximized { get; set; }
    [Browsable(false)] public int SplitTop { get; set; }
    [Browsable(false)] public double FilesMinMB { get; set; } = 100;
    [Browsable(false)] public int FilesOlderDays { get; set; }

    public static string AppDir => AppContext.BaseDirectory;
    static string FilePath => Path.Combine(AppDir, "DiskHog.settings.json");

    public string ResolvedReportFolder =>
        string.IsNullOrWhiteSpace(ReportFolder) ? Path.Combine(AppDir, "reports") : ReportFolder;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Clamp()
    {
        SsdThreads = Math.Clamp(SsdThreads, 1, 64);
        HddThreads = Math.Clamp(HddThreads, 1, 64);
        OtherThreads = Math.Clamp(OtherThreads, 1, 64);
        ReportMinFolderMB = Math.Max(0, ReportMinFolderMB);
        ReportTreeDepth = Math.Clamp(ReportTreeDepth, 1, 64);
        ReportTopFiles = Math.Clamp(ReportTopFiles, 0, 100000);
        StaleDays = Math.Max(1, StaleDays);
        CsvMinMB = Math.Max(0, CsvMinMB);
    }
}
