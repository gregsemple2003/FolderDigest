using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderDigest
{
    /// <summary>
    /// Serializable settings for one attachment row.
    /// </summary>
    public sealed class AttachmentSetting
    {
        public AttachmentPosition Position { get; set; } = AttachmentPosition.Before;
        public string Type { get; set; } = "LogAttachment";
        public string? FilePath { get; set; }
        public string? StartPattern { get; set; }
    }

    public interface IOutputAttachment
    {
        string Name { get; }

        /// <summary>
        /// Returns the fully rendered block to inject into the digest.
        /// Must include any headers/footers and newlines.
        /// </summary>
        string Render();
    }

    /// <summary>
    /// Takes the base digest and a list of attachment settings and
    /// produces the final output.
    /// </summary>
    public static class AttachmentComposer
    {
        public static string ApplyAttachments(string digest, IReadOnlyList<AttachmentSetting> settings)
        {
            if (settings == null || settings.Count == 0)
                return digest ?? string.Empty;

            var before = new StringBuilder();
            var after = new StringBuilder();

            foreach (var setting in settings)
            {
                if (setting == null) continue;

                var attachment = CreateAttachment(setting);
                if (attachment == null) continue;

                var text = attachment.Render();
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (setting.Position == AttachmentPosition.Before)
                    before.Append(text);
                else
                    after.Append(text);
            }

            var sb = new StringBuilder();

            if (before.Length > 0)
                sb.Append(before);

            sb.Append(digest ?? string.Empty);

            if (after.Length > 0)
            {
                // Ensure there is a newline before we append the trailing block
                if (sb.Length > 0 && !sb.ToString().EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    sb.AppendLine();

                sb.Append(after);
            }

            return sb.ToString();
        }

        private static IOutputAttachment? CreateAttachment(AttachmentSetting setting)
        {
            switch (setting.Type)
            {
                case "LogAttachment":
                    if (string.IsNullOrWhiteSpace(setting.FilePath))
                        return null;

                    return new LogAttachment
                    {
                        FilePath = setting.FilePath,
                        StartPattern = setting.StartPattern ?? string.Empty
                    };

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Reads from a log file, starting at the first line matching StartPattern (if any),
    /// through to the end of the file, and wraps it with START/END markers.
    /// </summary>
    public sealed class LogAttachment : IOutputAttachment
    {
        public string FilePath { get; set; } = string.Empty;
        public string StartPattern { get; set; } = string.Empty;

        public string Name => "LogAttachment";

        public string Render()
        {
            var sb = new StringBuilder();

            sb.AppendLine($"--- START {Name} {FilePath} ---");

            if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            {
                sb.AppendLine("(File not found.)");
                sb.AppendLine($"--- END {Name} {FilePath} ---");
                return sb.ToString();
            }

            try
            {
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                bool copyFromHere = string.IsNullOrWhiteSpace(StartPattern);
                Regex? regex = null;

                if (!copyFromHere)
                {
                    try
                    {
                        // StartPattern is a .NET regex: e.g. "Test Run.*"
                        regex = new Regex(StartPattern, RegexOptions.Compiled);
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"(Invalid regex '{StartPattern}': {ex.Message})");
                        sb.AppendLine($"--- END {Name} {FilePath} ---");
                        return sb.ToString();
                    }
                }

                bool matched = copyFromHere;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!matched && regex != null && regex.IsMatch(line))
                    {
                        matched = true;
                    }

                    if (matched)
                    {
                        sb.AppendLine(line);
                    }
                }

                if (!matched && !copyFromHere)
                {
                    sb.AppendLine($"(No lines matched '{StartPattern}'.)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(Error reading file: {ex.Message})");
            }

            sb.AppendLine($"--- END {Name} {FilePath} ---");
            return sb.ToString();
        }
    }

    /// <summary>
    /// UI model for one row in the attachments grid.
    /// Converts to/from AttachmentSetting for persistence.
    /// </summary>
    public sealed class AttachmentRow : INotifyPropertyChanged
    {
        private AttachmentPosition _position = AttachmentPosition.Before;
        private string _type = "LogAttachment";
        private string _filePath = string.Empty;
        private string _startPattern = string.Empty;

        public AttachmentPosition Position
        {
            get => _position;
            set
            {
                if (_position != value)
                {
                    _position = value;
                    OnPropertyChanged(nameof(Position));
                }
            }
        }

        public string Type
        {
            get => _type;
            set
            {
                if (_type != value)
                {
                    _type = value;
                    OnPropertyChanged(nameof(Type));
                }
            }
        }

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged(nameof(FilePath));
                }
            }
        }

        public string StartPattern
        {
            get => _startPattern;
            set
            {
                if (_startPattern != value)
                {
                    _startPattern = value;
                    OnPropertyChanged(nameof(StartPattern));
                }
            }
        }

        public AttachmentSetting ToSetting()
            => new AttachmentSetting
            {
                Position = this.Position,
                Type = this.Type,
                FilePath = this.FilePath,
                StartPattern = this.StartPattern
            };

        public static AttachmentRow FromSetting(AttachmentSetting setting)
        {
            if (setting == null) throw new ArgumentNullException(nameof(setting));

            return new AttachmentRow
            {
                Position = setting.Position,
                Type = string.IsNullOrWhiteSpace(setting.Type) ? "LogAttachment" : setting.Type,
                FilePath = setting.FilePath ?? string.Empty,
                StartPattern = setting.StartPattern ?? string.Empty
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

