namespace DiskHog;

enum HogSafety { Safe, Caution, Keep, Info }

sealed record HogRule(string Pattern, string Title, HogSafety Safety, string Advice);

sealed class HogHit
{
    public HogRule Rule;
    public Node Node;
    public long Value;
}

/// <summary>Well-known places where Windows and apps quietly pile up data, with advice on what's safe to remove.</summary>
static class KnownHogs
{
    const HogSafety S = HogSafety.Safe, C = HogSafety.Caution, K = HogSafety.Keep, I = HogSafety.Info;
    const string BrowserAdvice = "Browser cache. Clear it from the browser (Clear browsing data > Cached files) or delete while the browser is closed.";
    const string ShaderAdvice = "Graphics shader cache. Safe to delete; it is rebuilt automatically (games may stutter briefly the first time).";

    static readonly HogRule[] Rules =
    {
        new(@"{WIN}\Temp", "Windows temp folder", S, "Delete the contents (files in use are skipped). Settings > System > Storage > Temporary files also cleans it."),
        new(@"{USERS}\AppData\Local\Temp", "User temp folder", S, "Delete the contents; anything still in use will be skipped. Often grows to many GB."),
        new(@"{WIN}\SoftwareDistribution\Download", "Windows Update download cache", S, "Settings > System > Storage > Temporary files > 'Windows Update Cleanup', or Disk Cleanup > Clean up system files."),
        new(@"{WIN}\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", "Delivery Optimization cache", S, "Disk Cleanup > Clean up system files > 'Delivery Optimization Files'."),
        new(@"{SYS}\Windows.old", "Previous Windows installation", C, "Only needed to roll back a Windows upgrade. Remove with Disk Cleanup > Clean up system files > 'Previous Windows installation(s)'."),
        new(@"{SYS}\$WINDOWS.~BT", "Windows upgrade leftovers", S, "Remove with Disk Cleanup > Clean up system files > 'Temporary Windows installation files'."),
        new(@"{SYS}\$WINDOWS.~WS", "Windows upgrade leftovers", S, "Remove with Disk Cleanup > Clean up system files."),
        new(@"{ANY}\$Recycle.Bin", "Recycle Bin", S, "Empty the Recycle Bin. 'Deleted' files keep using space until you do."),
        new(@"{SYS}\hiberfil.sys", "Hibernation file", C, "If you don't use Hibernate / Fast Startup, run 'powercfg /h off' in an admin terminal and Windows removes it."),
        new(@"{SYS}\pagefile.sys", "Page file (virtual memory)", K, "Managed by Windows. Can be shrunk or moved to another drive: Advanced system settings > Performance > Advanced > Virtual memory."),
        new(@"{SYS}\swapfile.sys", "Swap file", K, "Managed by Windows."),
        new(@"{WIN}\Installer", "Windows Installer cache", K, "Do NOT delete by hand - programs need it to update, repair and uninstall."),
        new(@"{WIN}\WinSxS", "Windows component store", K, "Do NOT delete by hand. Shrink safely (admin terminal): Dism /Online /Cleanup-Image /StartComponentCleanup"),
        new(@"{SYS}\ProgramData\Package Cache", "Installer package cache (Visual Studio etc.)", K, "Deleting can break repair / uninstall / updates. Uninstall programs normally instead."),
        new(@"{WIN}\LiveKernelReports", "Kernel crash / hang reports", S, "Old .dmp files here are safe to delete."),
        new(@"{WIN}\Minidump", "Crash minidumps", S, "Safe to delete."),
        new(@"{WIN}\MEMORY.DMP", "Full crash dump", S, "Safe to delete unless you're debugging a crash."),
        new(@"{USERS}\AppData\Local\CrashDumps", "App crash dumps", S, "Safe to delete."),
        new(@"{SYS}\ProgramData\Microsoft\Windows\WER", "Windows Error Reporting files", S, "Safe to delete, or Disk Cleanup > 'Windows error reports'."),
        new(@"{WIN}\Logs\CBS", "Windows servicing logs", S, "Old CbsPersist_*.log / .cab files are safe to delete."),
        new(@"{SYS}\System Volume Information", "Restore points / shadow copies", C, "Usually unreadable (counted in Unaccounted). Limit with System Protection > Configure > Max usage, or 'vssadmin list shadowstorage'."),
        new(@"{USERS}\Downloads", "Downloads folder", C, "Review it - old installers, ISOs and archives are commonly forgotten here."),
        new(@"{USERS}\Videos\Captures", "Game Bar recordings", C, "Screen / game recordings. Review and delete what you don't need."),
        new(@"{USERS}\Videos\NVIDIA", "NVIDIA ShadowPlay recordings", C, "Instant-replay / recordings. Review and delete what you don't need."),
        new(@"{USERS}\OneDrive", "OneDrive folder", I, "Right-click > 'Free up space' to keep files online-only."),
        new(@"{USERS}\AppData\Local\Google\Chrome\User Data\*\Cache", "Chrome cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Google\Chrome\User Data\*\Code Cache", "Chrome code cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Google\Chrome\User Data\*\Service Worker\CacheStorage", "Chrome site cache storage", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Microsoft\Edge\User Data\*\Cache", "Edge cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Microsoft\Edge\User Data\*\Code Cache", "Edge code cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Microsoft\Edge\User Data\*\Service Worker\CacheStorage", "Edge site cache storage", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\BraveSoftware\Brave-Browser\User Data\*\Cache", "Brave cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Mozilla\Firefox\Profiles\*\cache2", "Firefox cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Roaming\Opera Software\Opera GX Stable\Cache", "Opera GX cache", S, BrowserAdvice),
        new(@"{USERS}\AppData\Local\Microsoft\Windows\INetCache", "Internet cache (legacy)", S, "Safe to delete."),
        new(@"{USERS}\AppData\Local\Microsoft\Windows\Explorer", "Thumbnail / icon cache", S, "Disk Cleanup > 'Thumbnails'. Rebuilt automatically."),
        new(@"{USERS}\AppData\Roaming\discord\Cache", "Discord cache", S, "Delete while Discord is closed."),
        new(@"{USERS}\AppData\Roaming\discord\Code Cache", "Discord code cache", S, "Delete while Discord is closed."),
        new(@"{USERS}\AppData\Local\Spotify\Storage", "Spotify cache", S, "Spotify > Settings > Storage > Clear cache."),
        new(@"{USERS}\AppData\Local\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalCache\Spotify\Data", "Spotify cache (Store app)", S, "Spotify > Settings > Storage > Clear cache."),
        new(@"{USERS}\AppData\Roaming\Microsoft\Teams", "Classic Teams cache", S, "Safe to delete if you no longer use classic Teams."),
        new(@"{USERS}\AppData\Roaming\Code\Cache", "VS Code cache", S, "Safe to delete while VS Code is closed."),
        new(@"{USERS}\AppData\Roaming\Code\CachedData", "VS Code cached data", S, "Safe to delete while VS Code is closed."),
        new(@"{USERS}\AppData\Local\JetBrains", "JetBrains IDE caches", S, "Folders for IDE versions you no longer use are safe to delete."),
        new(@"{USERS}\AppData\Roaming\Adobe\Common\Media Cache Files", "Adobe media cache", S, "Clear in Premiere / After Effects: Preferences > Media Cache."),
        new(@"{USERS}\AppData\Roaming\Adobe\Common\Peak Files", "Adobe peak files", S, "Safe to delete; regenerated when needed."),
        new(@"{USERS}\AppData\Local\NVIDIA\DXCache", "NVIDIA DirectX shader cache", S, ShaderAdvice),
        new(@"{USERS}\AppData\Local\NVIDIA\GLCache", "NVIDIA OpenGL shader cache", S, ShaderAdvice),
        new(@"{USERS}\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache", "NVIDIA shader cache", S, ShaderAdvice),
        new(@"{USERS}\AppData\Local\D3DSCache", "DirectX shader cache", S, ShaderAdvice),
        new(@"{USERS}\AppData\Local\AMD\DxCache", "AMD shader cache", S, ShaderAdvice),
        new(@"{USERS}\AppData\Local\AMD\DxcCache", "AMD shader cache", S, ShaderAdvice),
        new(@"{SYS}\ProgramData\NVIDIA Corporation\Downloader", "NVIDIA driver downloads", S, "Old driver installers. Safe to delete."),
        new(@"{SYS}\NVIDIA", "NVIDIA driver extraction leftovers", S, "Safe to delete."),
        new(@"{SYS}\AMD", "AMD driver installer leftovers", S, "Safe to delete."),
        new(@"{USERS}\AppData\Local\npm-cache", "npm cache", S, "Run: npm cache clean --force"),
        new(@"{USERS}\AppData\Local\pip\Cache", "pip cache", S, "Run: pip cache purge"),
        new(@"{USERS}\AppData\Local\uv\cache", "uv cache", S, "Run: uv cache clean"),
        new(@"{USERS}\AppData\Local\Yarn\Cache", "Yarn cache", S, "Run: yarn cache clean"),
        new(@"{USERS}\.nuget\packages", "NuGet package cache", S, "Run: dotnet nuget locals all --clear (packages re-download when needed)."),
        new(@"{USERS}\.gradle\caches", "Gradle cache", S, "Safe to delete; re-downloaded when needed."),
        new(@"{USERS}\.m2\repository", "Maven repository", S, "Safe to delete; re-downloaded when needed."),
        new(@"{USERS}\.cargo\registry", "Rust cargo registry", S, "Safe to delete; re-downloaded when needed."),
        new(@"{USERS}\.cache", "Tool caches (often AI models)", C, "Commonly holds HuggingFace / ML models. Review what's inside."),
        new(@"{USERS}\.ollama\models", "Ollama AI models", I, "Remove models you don't use: ollama list / ollama rm <name>"),
        new(@"{USERS}\AppData\Local\UnrealEngine\Common\DerivedDataCache", "Unreal Engine derived data cache", S, "Safe to delete; rebuilt when needed."),
        new(@"{USERS}\AppData\Local\Docker\wsl", "Docker Desktop disk image", C, "Run 'docker system prune -a', then compact the .vhdx (it never shrinks by itself)."),
        new(@"{SYS}\ProgramData\Docker", "Docker data (Windows containers)", C, "Clean up with 'docker system prune -a'."),
        new(@"{USERS}\AppData\Local\Packages\*\LocalState\ext4.vhdx", "WSL Linux disk image", C, "Grows but never shrinks. Clean up inside Linux, then: wsl --shutdown and compact it (Optimize-VHD or diskpart 'compact vdisk')."),
        new(@"{USERS}\AppData\Roaming\Apple Computer\MobileSync\Backup", "iPhone / iPad backups", C, "Delete old device backups in iTunes / Apple Devices > Manage Backups."),
        new(@"{USERS}\Apple\MobileSync\Backup", "iPhone / iPad backups (Store iTunes)", C, "Delete old device backups in iTunes / Apple Devices > Manage Backups."),
        new(@"{SYS}\Program Files (x86)\Steam\steamapps\common", "Steam games", I, "Uninstall games you don't play from Steam (Library > game > Manage > Uninstall)."),
        new(@"{SYS}\Program Files (x86)\Steam\steamapps\shadercache", "Steam shader cache", S, ShaderAdvice),
        new(@"{SYS}\Program Files (x86)\Steam\steamapps\downloading", "Steam partial downloads", C, "Leftover / paused downloads. Safe to delete if nothing is downloading."),
        new(@"{SYS}\Program Files (x86)\Steam\steamapps\workshop", "Steam Workshop content", I, "Unsubscribe from mods you don't use."),
        new(@"{ANY}\SteamLibrary\steamapps\common", "Steam library games", I, "Uninstall games you don't play from Steam."),
        new(@"{ANY}\SteamLibrary\steamapps\shadercache", "Steam shader cache", S, ShaderAdvice),
        new(@"{ANY}\SteamLibrary\steamapps\downloading", "Steam partial downloads", C, "Leftover / paused downloads. Safe to delete if nothing is downloading."),
        new(@"{SYS}\Program Files\Epic Games", "Epic Games installs", I, "Uninstall games you don't play from the Epic launcher."),
        new(@"{ANY}\XboxGames", "Xbox / Game Pass games", I, "Uninstall from the Xbox app."),
    };

    public static List<HogHit> Find(IList<RootNode> roots)
    {
        string sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (string.IsNullOrEmpty(win)) win = sys + @"\Windows";
        var drives = roots.Select(r => (Path.GetPathRoot(r.Name) ?? "").TrimEnd('\\'))
                          .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var hits = new List<HogHit>();
        var seen = new HashSet<Node>();
        foreach (var rule in Rules)
        {
            var patterns = new List<string>();
            if (rule.Pattern.StartsWith("{ANY}"))
                patterns.AddRange(drives.Select(d => d + rule.Pattern.Substring(5)));
            else
                patterns.Add(rule.Pattern.Replace("{USERS}", sys + @"\Users\*").Replace("{WIN}", win).Replace("{SYS}", sys));

            foreach (var pat in patterns)
            {
                var segs = pat.Split('\\');
                foreach (var root in roots)
                foreach (var node in Match(root, segs))
                {
                    if (node.IsVirtual || !seen.Add(node)) continue;
                    long v = node.Value;
                    if (v <= 0) continue;
                    hits.Add(new HogHit { Rule = rule, Node = node, Value = v });
                }
            }
        }
        hits.Sort((a, b) => b.Value.CompareTo(a.Value));
        return hits;
    }

    static bool SegMatch(string pattern, string name) =>
        pattern == "*" || string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    static List<Node> Match(RootNode root, string[] pat)
    {
        var result = new List<Node>();
        var rsegs = root.Name.TrimEnd('\\').Split('\\');
        if (rsegs.Length > pat.Length) return result;
        for (int i = 0; i < rsegs.Length; i++)
            if (!SegMatch(pat[i], rsegs[i])) return result;

        var cur = new List<Node> { root };
        for (int i = rsegs.Length; i < pat.Length && cur.Count > 0; i++)
        {
            var next = new List<Node>();
            foreach (var n in cur)
            {
                if (n is not DirNode d) continue;
                if (pat[i] == "*") next.AddRange(d.GetChildren().Where(c => c.IsDir));
                else { var c = d.FindChild(pat[i]); if (c != null) next.Add(c); }
            }
            cur = next;
        }
        result.AddRange(cur);
        return result;
    }

    public static string SafetyText(HogSafety s) => s switch
    {
        HogSafety.Safe => "Safe to clean",
        HogSafety.Caution => "Review first",
        HogSafety.Keep => "Don't delete manually",
        _ => "Info",
    };

    public static Color SafetyColor(HogSafety s) => s switch
    {
        HogSafety.Safe => Color.FromArgb(225, 245, 225),
        HogSafety.Caution => Color.FromArgb(255, 243, 214),
        HogSafety.Keep => Color.FromArgb(252, 225, 225),
        _ => Color.FromArgb(232, 238, 248),
    };
}
