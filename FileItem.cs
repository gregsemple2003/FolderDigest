using System;
using System.ComponentModel;

namespace FolderDigest
{
    public sealed class FileItem : INotifyPropertyChanged
    {
        public string RelativePath { get; init; } = "";
        public DateTime LastWriteTime { get; init; }
        public long SizeBytes { get; init; }

        private bool _include = true;
        public bool Include
        {
            get => _include;
            set
            {
                if (_include != value)
                {
                    _include = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include)));
                }
            }
        }

        public string ModifiedDisplay => LastWriteTime.ToString("yyyy-MM-dd HH:mm");
        public string SizeDisplay => FormatSize(SizeBytes);

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string FormatSize(long bytes)
        {
            const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;
            if (bytes >= GB) return $"{bytes / (double)GB:0.##} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:0.##} MB";
            if (bytes >= KB) return $"{bytes / (double)KB:0.##} KB";
            return $"{bytes} B";
        }
    }
}