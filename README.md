# DiskHog: what's eating my disk?

Run **`DiskHog\DiskHog.exe`**.

## Basic use
1. Tick the drives you want (C: is ticked the first time) or use **Add folder…**, then press **Scan**.
   Several drives are scanned at the same time.
2. **Pause / Resume** at any time. **Stop** keeps what was found so far, and **Continue** finishes it later.
   **Save results…** stores a scan, finished or not, in a `.dhog` file so you can reopen or continue it another day.
3. Browse the results:
   - **Folders**: a tree sorted by size. "Newest change inside" helps spot forgotten folders (big, untouched for years).
   - **Map** (bottom): every block is a file; bigger block = bigger file, color = file type. Double-click to zoom in; Backspace or the mouse back button zooms out.
   - **Largest files**: filter by minimum size, "not modified for N days", and name (wildcards OK: `*.iso`, `\Downloads\`).
   - **File types**: total size per extension. Click one to highlight those files on the map.
   - **Known space hogs**: temp folders, caches, update leftovers, hiberfil.sys and so on, each marked
     green (safe to clean), yellow (review first) or red (don't delete by hand), with advice.
   - **Unreadable folders**: folders Windows wouldn't let it read. **Restart as admin** fixes most of them, and your current results carry over.
4. Right-click anything for: Show in Explorer, Copy path, Rescan this folder, Properties, Move to Recycle Bin, Delete permanently.

## Logs / reports for an AI
- After each complete scan a Markdown report is auto-saved in `DiskHog\reports\` (you can turn this off in Options).
- **Export report ▾** has three options:
  - save a report anywhere
  - also save a CSV of every item above a size limit
  - **copy the report to the clipboard** to paste into an AI chat ("what in here is safe to delete?")

## Size terms
- **Size on disk** (default) is what you actually get back by deleting. Online-only OneDrive files count as ~0.
  Switch to **Logical size** in the toolbar to see nominal file sizes.
- **Unaccounted** is used space that no readable file explains: NTFS metadata, restore points and shadow copies,
  and folders that couldn't be read.
- Hard-linked files (common in `C:\Windows\WinSxS`) are counted once, so totals match real usage.
- Junctions and symlinks are not followed, so nothing is counted twice.

## Keyboard
F5 scan · Ctrl+P pause/resume · Ctrl+S save · Ctrl+O open · Ctrl+E export report ·
Del = Recycle Bin · Shift+Del = delete permanently · Ctrl+C = copy path · Enter = show in Explorer

## Command line (for scheduled reports)
```
DiskHog.exe --scan C:\ D:\ --report "C:\somewhere\report.md" [--csv] [--save results.dhog] [--logical]
DiskHog.exe --load results.dhog --report report.md          (report from a saved scan)
DiskHog.exe --load results.dhog --continue --save results.dhog   (finish an unfinished scan)
```
No window is shown in this mode. Write drive roots as `C:\` without quotes.

## Rebuilding
Source is in `src\`. Run `build.bat` (needs the .NET 10 SDK) to rebuild `DiskHog\DiskHog.exe`.
