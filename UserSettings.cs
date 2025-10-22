using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FolderDigest
{
    /// <summary>
    /// Stores simple user settings in the current working directory as JSON.
    /// Only remembers the last selected folder path for now.
    /// </summary>
    public sealed class UserSettings
    {
        public string? LastFolder { get; set; }

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

        private static string GetStartingCwd()
        {
            try { return Directory.GetCurrentDirectory(); }
            catch { return AppContext.BaseDirectory; } // best-effort fallback
        }
    }
}
