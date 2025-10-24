using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FolderDigest
{
    /// <summary>
    /// Stores simple user settings in the current working directory as JSON.
    /// Remembers last selected folder and, per folder, which files are excluded.
    /// Default is included unless listed in Excluded.
    /// </summary>
    public sealed class UserSettings
    {
        public string? LastFolder { get; set; }

        // Window geometry persistence
        public double WindowX { get; set; } = 100;
        public double WindowY { get; set; } = 100;
        public double WindowWidth { get; set; } = 900;
        public double WindowHeight { get; set; } = 600;

        // DataGrid sorting persistence
        public string? SortColumn { get; set; } = "RelativePath";
        public string SortDirection { get; set; } = "Ascending"; // "Ascending", "Descending", or "None"

        // Per-folder selection state: only store Excluded paths (relative to the folder).
        public Dictionary<string, FolderSelectionState> Selections { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        // Capture the starting CWD once so file dialogs don't accidentally move it later.
        private static readonly string BaseCwd = GetStartingCwd();
        private static string SettingsPath => Path.Combine(BaseCwd, "FolderDigest.settings.json");

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    return settings ?? new UserSettings();
                }
            }
            catch
            {
                // swallow and fall back to defaults
            }
            return new UserSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // swallow: persistence should never crash the app
            }
        }

        public bool IsIncluded(string folder, string relativePath)
        {
            var norm = NormalizeRel(relativePath);
            if (Selections.TryGetValue(folder, out var state))
                return !state.Excluded.Contains(norm);
            return true; // default: included
        }

        public void SetIncluded(string folder, string relativePath, bool include)
        {
            var norm = NormalizeRel(relativePath);
            if (!Selections.TryGetValue(folder, out var state))
            {
                state = new FolderSelectionState();
                Selections[folder] = state;
            }

            if (!include) state.Excluded.Add(norm);
            else state.Excluded.Remove(norm);

            // Persist immediately as requested
            Save();
        }

        public void PruneMissing(string folder, IReadOnlyCollection<string> currentRelativePaths)
        {
            if (!Selections.TryGetValue(folder, out var state) || state.Excluded.Count == 0) return;

            var set = new HashSet<string>(currentRelativePaths, StringComparer.OrdinalIgnoreCase);
            state.Excluded.RemoveWhere(p => !set.Contains(p));
            Save();
        }

        private static string NormalizeRel(string rel)
            => rel.Replace('\\', Path.DirectorySeparatorChar)
                  .Replace('/', Path.DirectorySeparatorChar);

        private static string GetStartingCwd()
        {
            try { return Directory.GetCurrentDirectory(); }
            catch { return AppContext.BaseDirectory; } // best-effort fallback
        }
    }

    public sealed class FolderSelectionState
    {
        public HashSet<string> Excluded { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}