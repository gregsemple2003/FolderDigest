using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace FolderDigest
{
    public partial class MainWindow : Window
    {
        private readonly UserSettings _settings;

        // Grid data
        private readonly ObservableCollection<FileItem> _fileItems = new();

        // Debounce scanning on text/options changes
        private CancellationTokenSource? _scanCts;

        // Extension set to quickly filter obvious binaries in the UI list (matches the digester)
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

        public MainWindow()
        {
            InitializeComponent();

            _settings = UserSettings.Load();

            var startFolder = (!string.IsNullOrWhiteSpace(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                ? _settings.LastFolder
                : Environment.CurrentDirectory;

            txtFolder.Text = startFolder;

            // Hook up UI events (persist folder immediately; refresh list on changes)
            txtFolder.TextChanged += (_, __) =>
            {
                _settings.LastFolder = txtFolder.Text.Trim();
                _settings.Save();            // persist immediately
                ScheduleRefreshFileList();   // and refresh the panel
            };

            chkIncludeHidden.Checked += (_, __) => ScheduleRefreshFileList();
            chkIncludeHidden.Unchecked += (_, __) => ScheduleRefreshFileList();
            chkIncludeBinaries.Checked += (_, __) => ScheduleRefreshFileList();
            chkIncludeBinaries.Unchecked += (_, __) => ScheduleRefreshFileList();
            txtMaxMB.LostFocus += (_, __) => ScheduleRefreshFileList();
            txtMaxMB.TextChanged += (_, __) => { /* light debounce */ ScheduleRefreshFileList(); };

            // Bind grid
            dgFiles.ItemsSource = _fileItems;

            // Hook up preview mouse event for multi-toggle checkbox functionality
            dgFiles.PreviewMouseLeftButtonDown += DgFiles_PreviewMouseLeftButtonDown;

            // Initial load
            Loaded += async (_, __) => await RefreshFileListAsync();
        }

        private void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = "Select a folder to create a digest from",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            var result = dlg.ShowDialog();
            if (result == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                txtFolder.Text = dlg.SelectedPath; // triggers persistence + refresh via TextChanged
            }
        }

        private async void btnGenerate_Click(object sender, RoutedEventArgs e)
        {
            var root = txtFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                System.Windows.MessageBox.Show(this, "Please choose a valid folder.", "Folder Digest", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Save the current folder before running
            _settings.LastFolder = root;
            _settings.Save();

            if (!double.TryParse(txtMaxMB.Text.Trim(), out var maxMb) || maxMb <= 0)
                maxMb = 1.0;

            var opts = new DirectoryDigesterOptions
            {
                IncludeHidden = chkIncludeHidden.IsChecked == true,
                IncludeBinaries = chkIncludeBinaries.IsChecked == true,
                MaxFileSizeBytes = (long)(maxMb * 1024 * 1024)
            };

            // Build set of selected relative paths
            var selected = new HashSet<string>(
                _fileItems.Where(f => f.Include).Select(f => f.RelativePath),
                StringComparer.OrdinalIgnoreCase);

            ToggleBusy(true, "Building digest…");
            txtOutput.Clear();
            btnCopy.IsEnabled = false;
            btnSave.IsEnabled = false;

            try
            {
                var digest = await Task.Run(() =>
                    DirectoryDigester.BuildDigest(root, opts, selected));

                txtOutput.Text = digest;
                btnCopy.IsEnabled = digest.Length > 0;
                btnSave.IsEnabled = digest.Length > 0;
                lblStatus.Text = $"Done. {DirectoryDigester.LastFileCount:N0} files included, {DirectoryDigester.LastSkippedCount:N0} skipped.";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "Error while creating digest", MessageBoxButton.OK, MessageBoxImage.Error);
                lblStatus.Text = "Failed.";
            }
            finally
            {
                ToggleBusy(false);
            }
        }

        private void btnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtOutput.Text))
            {
                System.Windows.Clipboard.SetText(txtOutput.Text, System.Windows.TextDataFormat.UnicodeText);
                lblStatus.Text = "Digest copied to clipboard.";
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(txtOutput.Text)) return;

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "digest.txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) == true)
            {
                File.WriteAllText(sfd.FileName, txtOutput.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                lblStatus.Text = $"Saved: {sfd.FileName}";
            }
        }

        private void ToggleBusy(bool isBusy, string? message = null)
        {
            btnGenerate.IsEnabled = !isBusy;
            btnBrowse.IsEnabled = !isBusy;
            progress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            lblStatus.Text = message ?? "";
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _settings.LastFolder = txtFolder.Text.Trim();
                _settings.Save();
            }
            catch { /* ignore */ }
            base.OnClosed(e);
        }

        // ----------------------------
        // File list / selection logic
        // ----------------------------

        private void ScheduleRefreshFileList()
        {
            // Debounce successive changes
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();

            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var token = _scanCts.Token;
                    await Task.Delay(300, token);
                    await RefreshFileListAsync(token);
                }
                catch (TaskCanceledException) { /* debounced */ }
            }, DispatcherPriority.Background);
        }

        private async Task RefreshFileListAsync(CancellationToken cancellationToken = default)
        {
            var root = txtFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _fileItems.Clear();
                lblFileCount.Text = "No folder selected.";
                return;
            }

            // Parse options used for *candidate list* (we mirror the digest filters)
            if (!double.TryParse(txtMaxMB.Text.Trim(), out var maxMb) || maxMb <= 0)
                maxMb = 1.0;

            var includeHidden = chkIncludeHidden.IsChecked == true;
            var includeBinaries = chkIncludeBinaries.IsChecked == true;
            var maxBytes = (long)(maxMb * 1024 * 1024);

            ToggleBusy(true, "Scanning files…");

            try
            {
                var items = await Task.Run(() =>
                {
                    var list = new List<FileItem>(capacity: 1024);
                    foreach (var path in DirectoryDigester.EnumerateFiles(root, includeHidden))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        FileInfo fi;
                        try { fi = new FileInfo(path); }
                        catch { continue; }

                        if (fi.Length > maxBytes) continue;

                        if (!includeBinaries)
                        {
                            var ext = fi.Extension;
                            if (BinaryExtensions.Contains(ext) || DirectoryDigester.LooksBinary(path))
                                continue;
                        }

                        var rel = Path.GetRelativePath(root, path);
                        var include = _settings.IsIncluded(root, rel); // default true if not explicitly excluded

                        list.Add(new FileItem
                        {
                            RelativePath = rel,
                            LastWriteTime = fi.LastWriteTime,
                            SizeBytes = fi.Length,
                            Include = include
                        });
                    }

                    // Sort initially by path to make it predictable
                    list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
                    return list;
                }, cancellationToken);

                // Replace items; also hook events for immediate persistence
                _fileItems.Clear();
                foreach (var it in items)
                {
                    it.PropertyChanged += FileItem_PropertyChanged;
                    _fileItems.Add(it);
                }

                // Prune missing exclusions from settings (keeps JSON tidy)
                _settings.PruneMissing(root, _fileItems.Select(f => f.RelativePath).ToArray());

                UpdateFileCountLabel();
                lblStatus.Text = $"Ready. {_fileItems.Count:N0} candidate files.";
            }
            catch (TaskCanceledException)
            {
                // ignored – debounced
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Scan failed: {ex.Message}";
                _fileItems.Clear();
                UpdateFileCountLabel();
            }
            finally
            {
                ToggleBusy(false);
            }
        }

        private void FileItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is FileItem fi && e.PropertyName == nameof(FileItem.Include))
            {
                var root = txtFolder.Text.Trim();
                if (!string.IsNullOrWhiteSpace(root))
                {
                    // Persist immediately on toggle
                    _settings.SetIncluded(root, fi.RelativePath, fi.Include);
                }
                UpdateFileCountLabel();
            }
        }

        private void UpdateFileCountLabel()
        {
            var selected = _fileItems.Count(f => f.Include);
            lblFileCount.Text = $"{selected:N0} of {_fileItems.Count:N0} selected";
        }

        // ----------------------------
        // Multi-toggle checkbox logic
        // ----------------------------

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T t) return t;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void DgFiles_PreviewMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            // Only multi-toggle when the user clicked a CheckBox inside the grid.
            if (e.OriginalSource is not DependencyObject origin) return;

            // Was the click on a CheckBox cell?
            var checkBox = FindAncestor<System.Windows.Controls.CheckBox>(origin);
            if (checkBox is null) return; // Not a checkbox: let normal behavior happen.

            var row = FindAncestor<DataGridRow>(origin);
            if (row is null) return;

            // We only "bulk toggle" if the clicked checkbox's row is ALREADY selected.
            // (Clicking a checkbox on an unselected row should toggle only that row.)
            if (!row.IsSelected) return;

            if (row.Item is FileItem clickedItem)
            {
                // Before WPF toggles anything, decide the new value based on the clicked row's current state.
                bool newValue = !clickedItem.Include;

                // Apply to every currently selected item.
                // NOTE: Setting Include raises PropertyChanged -> your existing persistence kicks in.
                foreach (var obj in dgFiles.SelectedItems)
                {
                    if (obj is FileItem fi && fi.Include != newValue)
                        fi.Include = newValue;
                }

                // Prevent default per-row toggle and selection changes for this click.
                e.Handled = true;
            }
        }
    }
}
