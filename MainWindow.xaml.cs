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
using System.Windows.Data;
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

        // Attachments grid
        private readonly ObservableCollection<AttachmentRow> _attachments = new();

        // View over _fileItems so we can filter without touching data
        private ICollectionView? _fileView;
        private string _currentFilter = string.Empty;

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

            // Apply saved pane sizes BEFORE positioning the window, so initial measure uses the right ratio.
            ApplyPaneHeightsFromSettings();

            // Set window geometry from settings
            Left = _settings.WindowX;
            Top = _settings.WindowY;
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;

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
                ApplyAttachmentActiveStateForCurrentFolder();
            };

            chkIncludeHidden.Checked += (_, __) => ScheduleRefreshFileList();
            chkIncludeHidden.Unchecked += (_, __) => ScheduleRefreshFileList();
            chkIncludeBinaries.Checked += (_, __) => ScheduleRefreshFileList();
            chkIncludeBinaries.Unchecked += (_, __) => ScheduleRefreshFileList();
            txtMaxMB.LostFocus += (_, __) => ScheduleRefreshFileList();
            txtMaxMB.TextChanged += (_, __) => { /* light debounce */ ScheduleRefreshFileList(); };

            // Remember a typed-in, valid folder when you leave the box
            txtFolder.LostFocus += (_, __) =>
            {
                var path = txtFolder.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    RememberFolderInRecent(path);
            };

            // Bind grids
            dgFiles.ItemsSource = _fileItems;
            dgAttachments.ItemsSource = _attachments;

            // Load saved attachments
            if (_settings.Attachments != null)
            {
                foreach (var setting in _settings.Attachments)
                {
                    var row = AttachmentRow.FromSetting(setting);
                    row.PropertyChanged += AttachmentRow_PropertyChanged;
                    _attachments.Add(row);
                }
            }

            // Apply per-folder activation for the initial folder
            ApplyAttachmentActiveStateForCurrentFolder();

            // Create a view over the observable collection and attach a filter predicate
            _fileView = CollectionViewSource.GetDefaultView(_fileItems);
            _fileView.Filter = FilePassesFilter;

            // Filter input: live updates; Esc clears
            txtFilter.TextChanged += (_, __) => ApplyFilter();
            txtFilter.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    txtFilter.Clear();
                    e.Handled = true;
                }
            };

            // Hook up preview mouse event for multi-toggle checkbox functionality
            dgFiles.PreviewMouseLeftButtonDown += DgFiles_PreviewMouseLeftButtonDown;

            // Hook up sorting events for persistence
            dgFiles.Sorting += DgFiles_Sorting;

            // Initial load
            Loaded += async (_, __) => 
            {
                await RefreshFileListAsync();
                ApplySavedSortState();
            };

            // Initialize recent button state
            UpdateRecentButtonState();
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
                RememberFolderInRecent(dlg.SelectedPath); // NEW
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

            // NEW: add to recent
            RememberFolderInRecent(root);

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
                // Snapshot only *active* attachments so the background thread doesn't touch UI objects
                var attachmentSnapshot = _attachments
                    .Where(a => a.IsActive)
                    .Select(a => a.ToSetting())
                    .ToArray();

                var finalOutput = await Task.Run(() =>
                {
                    var digest = DirectoryDigester.BuildDigest(root, opts, selected);
                    return AttachmentComposer.ApplyAttachments(digest, attachmentSnapshot);
                });

                txtOutput.Text = finalOutput;
                btnCopy.IsEnabled = finalOutput.Length > 0;
                btnSave.IsEnabled = finalOutput.Length > 0;
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
                
                // Save window geometry
                _settings.WindowX = Left;
                _settings.WindowY = Top;
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;

                // Persist pane ratio (use star values when available, else fallback to actual heights)
                double topWeight = RowDefFiles.Height.IsStar ? RowDefFiles.Height.Value : RowDefFiles.ActualHeight;
                double bottomWeight = RowDefDigest.Height.IsStar ? RowDefDigest.Height.Value : RowDefDigest.ActualHeight;

                if (topWeight <= 0) topWeight = 1;
                if (bottomWeight <= 0) bottomWeight = 1;

                _settings.FilePaneStars = topWeight;
                _settings.DigestPaneStars = bottomWeight;

                // Persist attachments themselves (global, not per-folder active flags)
                _settings.Attachments.Clear();
                foreach (var row in _attachments)
                {
                    _settings.Attachments.Add(row.ToSetting());
                }

                // Drop activation state for attachments that no longer exist
                _settings.PruneAttachmentSelections(_attachments.Select(a => a.Id));

                _settings.Save();
            }
            catch { /* ignore */ }
            base.OnClosed(e);
        }

        private void ApplyPaneHeightsFromSettings()
        {
            // Safety: clamp to sane minimums to avoid collapsing panes
            var top = _settings.FilePaneStars > 0 ? _settings.FilePaneStars : 2.0;
            var bottom = _settings.DigestPaneStars > 0 ? _settings.DigestPaneStars : 1.0;

            // Named RowDefinitions come from XAML
            RowDefFiles.Height  = new GridLength(top, GridUnitType.Star);
            RowDefDigest.Height = new GridLength(bottom, GridUnitType.Star);
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

                _fileView?.Refresh();   // keep whatever the user typed in the filter active

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
            var total = _fileItems.Count;

            long selectedBytes = 0;
            int selectedCount = 0;
            foreach (var f in _fileItems)
            {
                if (f.Include)
                {
                    selectedCount++;
                    selectedBytes += f.SizeBytes;
                }
            }

            // Round up to the nearest KB so tiny files don't show as 0 KB
            var selectedKB = (selectedBytes + 1023) / 1024;

            lblFileCount.Text = $"{selectedCount:N0} of {total:N0} selected";
            if (grpFiles != null)
            {
                grpFiles.Header = $"Files in folder ({selectedKB:N0} KB selected)";
            }
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

        // ----------------------------
        // DataGrid sorting persistence
        // ----------------------------

        private void DgFiles_Sorting(object? sender, DataGridSortingEventArgs e)
        {
            var current = e.Column.SortDirection; // pre-toggle value
            var next = current switch
            {
                ListSortDirection.Ascending  => ListSortDirection.Descending,
                ListSortDirection.Descending => ListSortDirection.Ascending,
                _                            => ListSortDirection.Ascending  // null -> first click sorts ascending
            };

            _settings.SortColumn = e.Column.SortMemberPath;
            _settings.SortDirection = next.ToString();
            _settings.Save();
        }

        private void ApplySavedSortState()
        {
            if (string.IsNullOrEmpty(_settings.SortColumn)) return;

            // Find the column to sort by
            var column = dgFiles.Columns.FirstOrDefault(c => c.SortMemberPath == _settings.SortColumn);
            if (column == null) return;

            // Apply the sort direction
            var sortDirection = _settings.SortDirection switch
            {
                "Descending" => ListSortDirection.Descending,
                "Ascending" => ListSortDirection.Ascending,
                _ => ListSortDirection.Ascending
            };

            // Set the column's sort direction and trigger the sort
            column.SortDirection = sortDirection;
            
            // Clear existing sort descriptions and add the new one
            dgFiles.Items.SortDescriptions.Clear();
            dgFiles.Items.SortDescriptions.Add(new SortDescription(_settings.SortColumn, sortDirection));
            
            // Force the DataGrid to refresh its sort indicators and apply the sort
            dgFiles.Items.Refresh();
        }

        // ----------------------------
        // Recent folders functionality
        // ----------------------------

        private void UpdateRecentButtonState()
        {
            btnRecent.IsEnabled = true; // keep it clickable; we show a helpful tip when empty
        }

        private void RememberFolderInRecent(string folder)
        {
            try
            {
                _settings.AddRecentFolder(folder);
                UpdateRecentButtonState();
            }
            catch { /* never crash on persistence */ }
        }

        private void btnRecent_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();

            // Seed: include current textbox path if it exists and isn't already in MRU
            var recents = new List<string>(_settings.RecentFolders);
            var current = txtFolder.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current) &&
                !recents.Any(r => StringComparer.OrdinalIgnoreCase.Equals(r, current)))
            {
                recents.Insert(0, Path.GetFullPath(current));
            }

            if (recents.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "No recent folders yet", IsEnabled = false });
                var hint = new MenuItem { Header = "Tip: Use Browse… / Generate / or leave the box to remember", IsEnabled = false };
                menu.Items.Add(hint);
            }
            else
            {
                foreach (var path in recents)
                {
                    var mi = new MenuItem { Header = path };
                    mi.Click += (_, __) => { txtFolder.Text = path; };
                    menu.Items.Add(mi);
                }

                menu.Items.Add(new Separator());
                var miClear = new MenuItem { Header = "Clear recent" };
                miClear.Click += (_, __) =>
                {
                    _settings.RecentFolders.Clear();
                    _settings.Save();
                    // no need to disable the button; menu will just show the tip next time
                };
                menu.Items.Add(miClear);
            }

            // Show the menu under the button
            btnRecent.ContextMenu = menu;
            menu.PlacementTarget = btnRecent;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            menu.Closed += (_, __) => btnRecent.ContextMenu = null;
        }

        // ----------------------------
        // View filter logic
        // ----------------------------

        private void ApplyFilter()
        {
            _currentFilter = txtFilter.Text?.Trim() ?? string.Empty;
            _fileView?.Refresh();       // only affects what's shown, not Include state
        }

        private bool FilePassesFilter(object obj)
        {
            if (obj is not FileItem f) return false;
            if (string.IsNullOrWhiteSpace(_currentFilter)) return true;

            // Simple case-insensitive AND of space-separated tokens.
            // Optional: prefix a token with "-" to exclude it.
            var path = f.RelativePath ?? string.Empty;
            var tokens = _currentFilter.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    var term = token[1..];
                    if (term.Length > 0 && path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        return false; // excluded term is present
                }
                else
                {
                    if (path.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                        return false; // required term missing
                }
            }
            return true;
        }

        private void btnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            txtFilter.Clear();
            txtFilter.Focus();
        }

        // ----------------------------
        // Attachments UI
        // ----------------------------

        private void AttachmentRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not AttachmentRow row)
                return;

            if (e.PropertyName == nameof(AttachmentRow.IsActive))
            {
                var folder = txtFolder.Text.Trim();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    _settings.SetAttachmentActive(folder, row.Id, row.IsActive);
                }
            }
        }

        private void ApplyAttachmentActiveStateForCurrentFolder()
        {
            var folder = txtFolder.Text.Trim();

            if (string.IsNullOrWhiteSpace(folder))
            {
                // No folder: treat all as inactive in the UI
                foreach (var row in _attachments)
                {
                    row.IsActive = false;
                }
                return;
            }

            foreach (var row in _attachments)
            {
                // If there is no entry for this folder/attachment, default is false
                row.IsActive = _settings.IsAttachmentActive(folder, row.Id);
            }
        }

        private void btnAddAttachment_Click(object sender, RoutedEventArgs e)
        {
            var row = new AttachmentRow
            {
                Position = AttachmentPosition.Before,
                Type = "LogAttachment"
            };

            // New rows default to whatever the per-folder state says (usually false for a new Id)
            var folder = txtFolder.Text.Trim();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                row.IsActive = _settings.IsAttachmentActive(folder, row.Id);
            }

            row.PropertyChanged += AttachmentRow_PropertyChanged;

            _attachments.Add(row);
            dgAttachments.SelectedItem = row;
            dgAttachments.ScrollIntoView(row);
        }

        private void btnRemoveAttachment_Click(object sender, RoutedEventArgs e)
        {
            if (dgAttachments.SelectedItem is AttachmentRow row)
            {
                row.PropertyChanged -= AttachmentRow_PropertyChanged;
                _attachments.Remove(row);
                // Stale IDs in AttachmentSelections are cleaned up on close via PruneAttachmentSelections
            }
        }

        private void AttachmentBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not AttachmentRow row) return;

            // Fully qualify to disambiguate from System.Windows.Forms.OpenFileDialog
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select log file to attach",
                CheckFileExists = true
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(row.FilePath))
                {
                    var dir = Path.GetDirectoryName(row.FilePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        dlg.InitialDirectory = dir;
                }
            }
            catch
            {
                // ignore
            }

            if (dlg.ShowDialog(this) == true)
            {
                row.FilePath = dlg.FileName;
            }
        }
    }
}
