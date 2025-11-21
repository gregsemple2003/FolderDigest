using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FolderDigest
{
	public enum AttachmentPosition
	{
		Before,
		After
	}

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

        // Layout (star weights) for the resizable panes
        public double FilePaneStars { get; set; } = 2.0;
        public double DigestPaneStars { get; set; } = 1.0;

        // Per-folder selection state: only store Excluded paths (relative to the folder).
        public Dictionary<string, FolderSelectionState> Selections { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        // NEW: most-recently-used folders (normalized full paths)
        public List<string> RecentFolders { get; set; } = new();

        // Output attachments configuration (applies to all folders for now)
        public List<AttachmentSetting> Attachments { get; set; } = new();

        // Per-folder attachment activation: which attachment rows are active for a given folder.
        public Dictionary<string, FolderAttachmentSelectionState> AttachmentSelections { get; set; }
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

        public void AddRecentFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;

            string full;
            try { full = Path.GetFullPath(folder.Trim()); }
            catch { return; }

            if (!Directory.Exists(full)) return;

            // De-dup (case-insensitive), move to front
            for (int i = RecentFolders.Count - 1; i >= 0; i--)
                if (StringComparer.OrdinalIgnoreCase.Equals(RecentFolders[i], full))
                    RecentFolders.RemoveAt(i);

            RecentFolders.Insert(0, full);

            // Cap the list
            const int MaxRecent = 12;
            if (RecentFolders.Count > MaxRecent)
                RecentFolders.RemoveRange(MaxRecent, RecentFolders.Count - MaxRecent);

            Save();
        }

        public bool IsAttachmentActive(string folder, Guid attachmentId)
        {
            if (AttachmentSelections.TryGetValue(folder, out var state))
                return state.ActiveAttachmentIds.Contains(attachmentId);

            // Default: attachments are inactive for a new folder
            return false;
        }

        public void SetAttachmentActive(string folder, Guid attachmentId, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;

            if (!AttachmentSelections.TryGetValue(folder, out var state))
            {
                if (!isActive)
                {
                    // default is inactive, nothing to store
                    return;
                }

                state = new FolderAttachmentSelectionState();
                AttachmentSelections[folder] = state;
            }

            if (isActive)
                state.ActiveAttachmentIds.Add(attachmentId);
            else
                state.ActiveAttachmentIds.Remove(attachmentId);

            Save();
        }

        public void PruneAttachmentSelections(IEnumerable<Guid> existingAttachmentIds)
        {
            var validIds = new HashSet<Guid>(existingAttachmentIds);

            if (AttachmentSelections.Count == 0)
                return;

            foreach (var kvp in AttachmentSelections.Values)
            {
                kvp.ActiveAttachmentIds.RemoveWhere(id => !validIds.Contains(id));
            }

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

    public sealed class FolderAttachmentSelectionState
    {
        public HashSet<Guid> ActiveAttachmentIds { get; set; } = new();
    }
}