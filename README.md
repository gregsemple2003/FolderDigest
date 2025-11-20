# FolderDigest

**A small WPF/.NET 8 utility that produces a “gitingest‑style” flat text digest of a folder’s source files.**
Designed for quickly sharing a project’s structure and contents (minus heavy/dev stuff) in a single pasteable text block.

<img width="899" height="847" alt="image" src="https://github.com/user-attachments/assets/1410ca0f-05a1-4852-82c4-18fa1e8e42d3" />

---

## Table of contents

* [What it does](#what-it-does)
* [Features](#features)
* [Using the app](#using-the-app)
* [Rules & heuristics (what gets included or skipped)](#rules--heuristics-what-gets-included-or-skipped)
* [Keyboard & mouse tips](#keyboard--mouse-tips)
* [Settings & persistence](#settings--persistence)
* [Programmatic use (library API)](#programmatic-use-library-api)
* [License](#license)

---

## What it does

FolderDigest scans a chosen folder, filters out heavy/dev directories and obvious binaries by default, and outputs a **single text digest** that looks like this:

```
# Directory Digest
Root: C:\FolderDigest
Generated: 2025-11-09 13:12:39

--- START FILE: App.xaml (278 bytes) ---
<Application x:Class="FolderDigest.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             ...
</Application>
--- END FILE: App.xaml ---

--- START FILE: MainWindow.xaml.cs (… bytes) ---
…file contents…
--- END FILE: MainWindow.xaml.cs ---
```

Each included file appears exactly once, wrapped with `--- START FILE:` / `--- END FILE:` markers and the original contents in between.

---

## Features

* **Point‑and‑click folder picker** with a **recent folders** drop‑down.
* Live **filter bar** for the file list (`token1 token2` means AND; prefix `-` to exclude a token).
* Per‑file **checkbox selection** (bulk toggle across selected rows).
* Options:

  * **Include hidden files**
  * **Include binary files**
  * **Max file size (MB)** (defaults to 1 MB)
* **Generate**, then **Copy** to clipboard or **Save…** to a file.
* **Fast traversal** that skips common dev/heavy folders automatically.
* **Binary detection heuristic** to avoid dumping non‑text files by accident.
* **Persistent settings**: window size/position, pane split ratio, grid sort, per‑folder exclusions, and recent folders.

---

## Using the app

1. **Pick a folder**

   * Type a path or click **Browse…**.
   * The small **▾** button opens **Recent folders** (MRU). You can clear the list there.
2. **Review candidate files**

   * The grid shows files that pass the current options (hidden/binary/size).
   * Use the **Filter** box to narrow the view without altering checkmarks.
3. **Select exactly what to include**

   * Check/uncheck files. Selections are remembered per folder.
   * Select multiple rows and click any selected row’s checkbox to **bulk toggle**.
4. Set options:

   * **Include hidden** / **Include binary**
   * **Max file size (MB)** for inclusion (default 1 MB).
5. Click **Generate Digest**

   * Status shows *“X files included, Y skipped.”*
6. **Copy** to clipboard or **Save…** as a `.txt`.

A resizable splitter lets you adjust the list vs. digest preview panes. Sort preferences and pane sizes persist.

---

## Rules & heuristics (what gets included or skipped)

### Directory pruning

The traversal skips common development/heavy folders by name, e.g.:

```
.git, .svn, .hg, .vs, .idea, .vscode,
node_modules, bin, obj, packages, dist, build, out, target,
.mypy_cache, __pycache__, .venv, .tox, .gradle, .dart_tool, coverage
```

*(See `SkipDirNames` in `DirectoryDigester.cs` for the source of truth.)*

### Size filter

Files larger than **Max file size** (defaults to **1 MB**) are skipped.

### Hidden files

Included **only if** “Include hidden files” is checked.

### Binary files

By default binaries are skipped. Two checks are used:

1. **Extension list** (e.g., images, archives, office docs, fonts, media, executables, DBs, etc.).
2. **Content sniffing**: reads up to 8 KB and treats as binary if:

   * any **NUL** byte is present, or
   * **> 1.5%** of bytes are control characters (excluding tab/CR/LF).

You can force inclusion by checking **Include binary files**.

### Encoding

* Tries text with **BOM detection** first.
* Falls back to **UTF‑8** (invalid sequences replaced).
* If reading fails, the file is counted as **skipped**.

---

## Keyboard & mouse tips

* **Esc** in the **Filter** box clears it.
* **Bulk toggle**: select multiple rows, then click the checkbox in any selected row.
* Grid virtualization keeps the list snappy for large folders.

---

## Settings & persistence

Settings are stored as JSON: **`FolderDigest.settings.json`** in the app’s **starting working directory**.
It remembers:

* Last folder, recent folders (MRU)
* Window position/size and the **files vs digest** pane split ratio
* Grid sort column/direction
* **Per‑folder selections**: only **excluded** relative paths are stored to keep the file small

Example (abbreviated):

```json
{
  "LastFolder": "C:\\FolderDigest",
  "SortColumn": "RelativePath",
  "SortDirection": "Ascending",
  "Selections": {
    "C:\\MyProject": { "Excluded": [ "bin/Release/app.exe", "dist/bundle.js" ] }
  },
  "RecentFolders": [ "C:\\MyProject", "D:\\Work\\Repo" ]
}
```

> Note: The file is saved alongside the executable’s working directory; launching from different locations can create separate settings files.

---

## Programmatic use (library API)

The UI is a thin layer over a reusable digester:

```csharp
using FolderDigest;

// Options
var opts = new DirectoryDigesterOptions
{
    IncludeHidden = false,
    IncludeBinaries = false,
    MaxFileSizeBytes = 1_000_000 // 1 MB
};

// Optional: only include a specific set of relative paths
ISet<string>? only = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "src/Program.cs",
    "README.md"
};

// Build the digest
string digest = DirectoryDigester.BuildDigest(@"C:\Path\To\Root", opts, only);

// Stats (for UI/telemetry)
int included = DirectoryDigester.LastFileCount;
int skipped  = DirectoryDigester.LastSkippedCount;
```

Helpers you can reuse:

* `IEnumerable<string> DirectoryDigester.EnumerateFiles(string root, bool includeHidden)`
* `bool DirectoryDigester.LooksBinary(string path)`

---

## License

No license file is included yet. If you intend to share or accept contributions, consider adding a `LICENSE` (e.g., MIT) at the repo root.
