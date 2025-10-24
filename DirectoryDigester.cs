using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FolderDigest
{
    public sealed class DirectoryDigesterOptions
    {
        public bool IncludeHidden { get; set; } = false;
        public bool IncludeBinaries { get; set; } = false;
        public long MaxFileSizeBytes { get; set; } = 1_000_000; // 1 MB default
    }

    public static class DirectoryDigester
    {
        // Stats for UI
        public static int LastFileCount { get; private set; }
        public static int LastSkippedCount { get; private set; }

        private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",".svn",".hg",".vs",".idea",".vscode",
            "node_modules","bin","obj","packages","dist","build","out","target",
            ".mypy_cache","__pycache__",".venv",".tox",".gradle",".dart_tool","coverage"
        };

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",".jpeg",".png",".gif",".bmp",".ico",".heic",
            ".zip",".rar",".7z",".gz",".tgz",".xz",".bz2",".tar",
            ".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",
            ".exe",".dll",".so",".dylib",".lib",".a",".o",".obj",".pdb",
            ".class",".jar",".war",".ear",
            ".ttf",".otf",".woff",".woff2",".eot",
            ".mp3",".mp4",".m4a",".m4v",".mov",".avi",".mkv",".flac",".wav",".webm",
            ".psd",".ai",".sketch",".blend",".fbx",
            ".sqlite",".db",".db3",".snd",".iso"
        };

        // NEW: optional filter set of relative paths to include (others skipped)
        public static string BuildDigest(string root, DirectoryDigesterOptions options, ISet<string>? onlyIncludeRelativePaths = null)
        {
            LastFileCount = 0;
            LastSkippedCount = 0;

            var sb = new StringBuilder(capacity: 1 << 20); // start with ~1MB capacity to reduce reallocations

            sb.AppendLine("# Directory Digest");
            sb.AppendLine($"Root: {root}");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            foreach (var file in EnumerateFiles(root, options.IncludeHidden))
            {
                // Size & type filters
                FileInfo fi;
                try { fi = new FileInfo(file); }
                catch { LastSkippedCount++; continue; }

                if (fi.Length > options.MaxFileSizeBytes)
                {
                    LastSkippedCount++;
                    continue;
                }

                if (!options.IncludeBinaries)
                {
                    var ext = fi.Extension;
                    if (BinaryExtensions.Contains(ext) || LooksBinary(file))
                    {
                        LastSkippedCount++;
                        continue;
                    }
                }

                var relPath = Path.GetRelativePath(root, file);

                // NEW: skip if not selected for inclusion
                if (onlyIncludeRelativePaths != null && !onlyIncludeRelativePaths.Contains(relPath))
                {
                    LastSkippedCount++;
                    continue;
                }

                // Read as text with BOM detection; fall back to bytes->UTF8 if necessary
                string content;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    content = reader.ReadToEnd();
                }
                catch
                {
                    try
                    {
                        // Fallback: load as bytes and decode as UTF8 replacing invalid sequences
                        var bytes = File.ReadAllBytes(file);
                        content = Encoding.UTF8.GetString(bytes);
                    }
                    catch
                    {
                        LastSkippedCount++;
                        continue;
                    }
                }

                sb.AppendLine($"--- START FILE: {relPath} ({fi.Length} bytes) ---");
                sb.Append(content);
                if (!content.EndsWith(Environment.NewLine)) sb.AppendLine(); // ensure trailing newline
                sb.AppendLine($"--- END FILE: {relPath} ---");
                sb.AppendLine();

                LastFileCount++;
            }

            if (LastFileCount == 0)
            {
                sb.AppendLine("(No files were included. Try enabling binaries, increasing the size limit, or picking another folder.)");
            }

            return sb.ToString();
        }

        // Made public so UI can reuse the traversal (skip hidden + dev folders)
        public static IEnumerable<string> EnumerateFiles(string root, bool includeHidden)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                IEnumerable<string> subdirs;
                IEnumerable<string> files;

                try
                {
                    subdirs = Directory.EnumerateDirectories(dir);
                    files = Directory.EnumerateFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var sd in subdirs)
                {
                    try
                    {
                        var name = Path.GetFileName(sd);
                        if (SkipDirNames.Contains(name)) continue;

                        var attrs = File.GetAttributes(sd);
                        if (!includeHidden && attrs.HasFlag(FileAttributes.Hidden)) continue;

                        stack.Push(sd);
                    }
                    catch { /* ignore */ }
                }

                foreach (var f in files)
                {
                    bool shouldInclude = false;
                    try
                    {
                        var attrs = File.GetAttributes(f);
                        if (!includeHidden && attrs.HasFlag(FileAttributes.Hidden)) 
                        { 
                            LastSkippedCount++; 
                        }
                        else
                        {
                            shouldInclude = true;
                        }
                    }
                    catch
                    {
                        LastSkippedCount++;
                    }

                    if (shouldInclude)
                    {
                        yield return f;
                    }
                }
            }
        }

        // Made public for UI filtering
        public static bool LooksBinary(string path)
        {
            try
            {
                var buffer = new byte[8192];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var read = fs.Read(buffer, 0, buffer.Length);
                if (read == 0) return false;

                int control = 0;
                for (int i = 0; i < read; i++)
                {
                    var b = buffer[i];
                    if (b == 0) return true; // null byte => binary
                    // count non-whitespace control chars
                    if (b < 32 && b != 9 && b != 10 && b != 13)
                        control++;
                }
                // heuristic: if > 1.5% control chars, treat as binary
                return control > read * 0.015;
            }
            catch
            {
                return true; // if we can't read it, treat as binary to be safe
            }
        }

    }
}