using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FolderDigest
{
    /// <summary>
    /// Serializable settings for one attachment row.
    /// </summary>
    public sealed class AttachmentSetting
    {
        public Guid Id { get; set; }                    // stable identity per row
        public AttachmentPosition Position { get; set; } = AttachmentPosition.Before;

        /// <summary>
        /// Logical type name for this attachment. For custom attachments this is
        /// usually the simple class name (e.g. "MyCustomAttachment") or a
        /// fully‑qualified type name.
        /// </summary>
        public string Type { get; set; } = "LogAttachment";

        // Legacy properties for LogAttachment; kept for backwards compatibility.
        public string? FilePath { get; set; }
        public string? StartPattern { get; set; }

        /// <summary>
        /// Optional serialized state for the attachment. When present this is used
        /// to hydrate the IOutputAttachment instance that the property grid edits.
        /// </summary>
        public string? StateJson { get; set; }
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
    /// Helper to resolve attachment types by name, so custom IOutputAttachment
    /// implementations can be plugged in without changing core code.
    /// </summary>
    internal static class AttachmentTypeResolver
    {
        private static readonly Dictionary<string, Type> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static Type? Resolve(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            if (Cache.TryGetValue(typeName, out var cached))
                return cached;

            Type? result = null;

            // 1) Short name in the FolderDigest namespace
            if (!typeName.Contains("."))
            {
                var fullName = $"FolderDigest.{typeName}";
                result = Type.GetType(fullName, throwOnError: false);
            }

            // 2) Fully‑qualified / assembly‑qualified
            result ??= Type.GetType(typeName, throwOnError: false);

            // 3) Scan loaded assemblies by simple name
            if (result == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var t in types)
                    {
                        if (!typeof(IOutputAttachment).IsAssignableFrom(t))
                            continue;

                        if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                        {
                            result = t;
                            break;
                        }
                    }

                    if (result != null)
                        break;
                }
            }

            if (result != null && typeof(IOutputAttachment).IsAssignableFrom(result))
            {
                Cache[typeName] = result;
                return result;
            }

            return null;
        }
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
            if (setting == null)
                return null;

            var type = AttachmentTypeResolver.Resolve(setting.Type);
            if (type == null)
            {
                // Fallback for legacy LogAttachment rows
                if (string.Equals(setting.Type, "LogAttachment", StringComparison.OrdinalIgnoreCase))
                    type = typeof(LogAttachment);
                else
                    return null;
            }

            // If we have serialized state, prefer that.
            if (!string.IsNullOrWhiteSpace(setting.StateJson))
            {
                try
                {
                    var obj = JsonSerializer.Deserialize(setting.StateJson, type);
                    if (obj is IOutputAttachment attachmentFromJson)
                        return attachmentFromJson;
                }
                catch
                {
                    // swallow and fall back to legacy mapping below
                }
            }

            IOutputAttachment? attachment = null;
            try
            {
                attachment = (IOutputAttachment?)Activator.CreateInstance(type);
            }
            catch
            {
                attachment = null;
            }

            if (attachment == null)
                return null;

            // Legacy support for LogAttachment: map FilePath/StartPattern
            if (attachment is LogAttachment log)
            {
                if (!string.IsNullOrWhiteSpace(setting.FilePath))
                    log.FilePath = setting.FilePath!;
                if (!string.IsNullOrWhiteSpace(setting.StartPattern))
                    log.StartPattern = setting.StartPattern!;
            }

            return attachment;
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
        private Guid _id = Guid.NewGuid();
        private AttachmentPosition _position = AttachmentPosition.Before;
        private string _type = "LogAttachment";
        private string _filePath = string.Empty;      // kept for legacy browse handler
        private string _startPattern = string.Empty;  // kept for legacy browse handler
        private bool _isActive;
        private IOutputAttachment? _attachment;

        /// <summary>
        /// Stable identity for this row; used to persist per-folder active state.
        /// </summary>
        public Guid Id
        {
            get => _id;
            set
            {
                if (_id != value)
                {
                    _id = value;
                    OnPropertyChanged(nameof(Id));
                }
            }
        }

        /// <summary>
        /// Whether this attachment is active for the current folder.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

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

                    // When the type changes, create a fresh attachment instance
                    // so the property grid shows the right set of properties.
                    Attachment = CreateAttachmentInstance(_type);
                }
                else if (_attachment == null)
                {
                    // Even if type doesn't change, ensure attachment is created if it's null
                    Attachment = CreateAttachmentInstance(_type);
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

        /// <summary>
        /// The attachment model edited in the property grid. For custom attachments,
        /// expose whatever public properties you like on your IOutputAttachment class.
        /// </summary>
        public IOutputAttachment? Attachment
        {
            get => _attachment;
            set
            {
                if (!ReferenceEquals(_attachment, value))
                {
                    _attachment = value;
                    OnPropertyChanged(nameof(Attachment));

                    // For built‑in LogAttachment, mirror the key properties into the legacy
                    // convenience fields so any old code that still reads them will see values.
                    if (_attachment is LogAttachment log)
                    {
                        FilePath = log.FilePath;
                        StartPattern = log.StartPattern;
                    }
                }
            }
        }

        public AttachmentSetting ToSetting()
        {
            var setting = new AttachmentSetting
            {
                Id = this.Id,
                Position = this.Position,
                Type = this.Type
            };

            if (Attachment != null)
            {
                try
                {
                    setting.StateJson = JsonSerializer.Serialize(Attachment, Attachment.GetType());
                }
                catch
                {
                    // ignore – we'll still try to populate legacy fields below
                }

                if (Attachment is LogAttachment log)
                {
                    setting.FilePath = log.FilePath;
                    setting.StartPattern = log.StartPattern;
                }
            }

            return setting;
        }

        public static AttachmentRow FromSetting(AttachmentSetting setting)
        {
            if (setting == null) throw new ArgumentNullException(nameof(setting));

            var row = new AttachmentRow
            {
                Position = setting.Position,
                Type = string.IsNullOrWhiteSpace(setting.Type) ? "LogAttachment" : setting.Type
            };

            // Backwards‑compat: old settings won't have Id populated
            row.Id = setting.Id != Guid.Empty ? setting.Id : Guid.NewGuid();

            // Hydrate the attachment instance for the property grid
            row.Attachment = CreateAttachmentFromSetting(row.Type, setting);

            // For built‑in LogAttachment, mirror the key properties into the legacy
            // convenience fields so any old code that still reads them will see values.
            if (row.Attachment is LogAttachment log)
            {
                row.FilePath = log.FilePath;
                row.StartPattern = log.StartPattern;
            }
            else
            {
                row.FilePath = setting.FilePath ?? string.Empty;
                row.StartPattern = setting.StartPattern ?? string.Empty;
            }

            return row;
        }

        private static IOutputAttachment? CreateAttachmentInstance(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;

            var type = AttachmentTypeResolver.Resolve(typeName);
            if (type == null)
                return null;

            try
            {
                return (IOutputAttachment?)Activator.CreateInstance(type);
            }
            catch
            {
                return null;
            }
        }

        private static IOutputAttachment? CreateAttachmentFromSetting(string typeName, AttachmentSetting setting)
        {
            var type = AttachmentTypeResolver.Resolve(typeName);
            if (type == null)
            {
                if (string.Equals(typeName, "LogAttachment", StringComparison.OrdinalIgnoreCase))
                    type = typeof(LogAttachment);
                else
                    return null;
            }

            // Try new‑style JSON state first
            if (!string.IsNullOrWhiteSpace(setting.StateJson))
            {
                try
                {
                    var obj = JsonSerializer.Deserialize(setting.StateJson, type);
                    if (obj is IOutputAttachment attachmentFromJson)
                        return attachmentFromJson;
                }
                catch
                {
                    // ignore and fall through
                }
            }

            // Fall back to a default instance and hydrate any legacy fields we know about.
            IOutputAttachment? instance;
            try
            {
                instance = (IOutputAttachment?)Activator.CreateInstance(type);
            }
            catch
            {
                instance = null;
            }

            if (instance is LogAttachment log)
            {
                if (!string.IsNullOrWhiteSpace(setting.FilePath))
                    log.FilePath = setting.FilePath!;
                if (!string.IsNullOrWhiteSpace(setting.StartPattern))
                    log.StartPattern = setting.StartPattern!;
            }

            return instance;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

