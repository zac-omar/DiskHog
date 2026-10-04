namespace DiskHog;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception, fatal: true);

        // As admin, the backup privilege lets us list folders whose permissions would otherwise block us (read-only).
        if (ReportWriter.IsAdmin()) Native.EnablePrivilege("SeBackupPrivilege");

        if (args.Contains("--scan") || args.Contains("--load"))
        {
            Environment.ExitCode = Headless(args);
            return;
        }

        string open = null;
        bool cont = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--open" && i + 1 < args.Length) open = args[++i];
            else if (args[i] == "--continue") cont = true;
            else if (args[i].EndsWith(".dhog", StringComparison.OrdinalIgnoreCase)) open = args[i];
        }

        Application.Run(new MainForm(open, cont));
    }

    /// <summary>
    /// No-window mode for scheduled tasks / scripts:
    ///   DiskHog.exe --scan C:\ D:\ [--report out.md] [--csv] [--save results.dhog] [--logical]
    ///   DiskHog.exe --load results.dhog [--report out.md] [--csv]
    /// Exit code 0 = OK, 1 = error (see DiskHog_crash.log).
    /// </summary>
    static int Headless(string[] args)
    {
        try
        {
            var settings = AppSettings.Load();
            settings.Clamp();
            var scanPaths = new List<string>();
            string report = null, save = null, load = null;
            bool csv = settings.WriteCsv, continueScan = false;
            double stopAfter = 0;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--scan":
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) scanPaths.Add(args[++i].Trim().Trim('"'));
                        break;
                    case "--report" when i + 1 < args.Length: report = args[++i]; break;
                    case "--save" when i + 1 < args.Length: save = args[++i]; break;
                    case "--load" when i + 1 < args.Length: load = args[++i]; break;
                    case "--csv": csv = true; break;
                    case "--logical": Display.OnDisk = false; break;
                    case "--continue": continueScan = true; break;
                    case "--stop-after" when i + 1 < args.Length:
                        stopAfter = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }

            var sc = new Scanner();
            var pending = new List<DirNode>();
            if (load != null)
            {
                var loaded = SessionIO.Load(load);
                sc.Roots.AddRange(loaded.Roots);
                sc.RestoreStats(loaded.Files, loaded.Dirs, loaded.Bytes, loaded.Elapsed, loaded.Errors);
                if (continueScan)
                {
                    foreach (var r in sc.Roots) TreeOps.RemoveUnaccounted(r);
                    pending = sc.CollectPending();
                }
            }
            else
            {
                foreach (var p in scanPaths.Select(TreeOps.NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (Directory.Exists(p)) sc.Roots.Add(TreeOps.CreateRoot(p));
                if (sc.Roots.Count == 0) throw new ArgumentException("Nothing to scan. Usage: --scan C:\\ D:\\SomeFolder [--report file.md]");
                pending.AddRange(sc.Roots);
            }

            if (pending.Count > 0)
            {
                using var done = new ManualResetEventSlim();
                sc.Finished += () => done.Set();
                sc.Start(pending, settings);
                if (stopAfter > 0 && !done.Wait(TimeSpan.FromSeconds(stopAfter))) sc.Stop();
                done.Wait();
                foreach (var r in sc.Roots)
                {
                    TreeOps.RefreshVolume(r);
                    TreeOps.UpdateUnaccounted(r);
                }
            }

            report ??= Path.Combine(settings.ResolvedReportFolder, ReportWriter.DefaultBaseName() + ".md");
            ReportWriter.Write(report, sc, settings, csv);
            if (save != null) SessionIO.Save(save, sc);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(AppSettings.AppDir, "DiskHog_crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] headless\n{ex}\n\n"); }
            catch { }
            return 1;
        }
    }

    static void ReportCrash(Exception ex, bool fatal)
    {
        try
        {
            string log = Path.Combine(AppSettings.AppDir, "DiskHog_crash.log");
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {(fatal ? "FATAL" : "error")}\n{ex}\n\n");
        }
        catch { }
        try
        {
            MessageBox.Show("Something went wrong:\n\n" + ex?.Message + "\n\nDetails were written to DiskHog_crash.log next to the program.",
                "DiskHog", MessageBoxButtons.OK, fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
        }
        catch { }
    }
}
