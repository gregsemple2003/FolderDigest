using Microsoft.Win32;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace FolderDigest
{
    public partial class MainWindow : Window
    {
        private readonly UserSettings _settings;

        public MainWindow()
        {
            InitializeComponent();

            _settings = UserSettings.Load();

            var startFolder = (!string.IsNullOrWhiteSpace(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                ? _settings.LastFolder
                : Environment.CurrentDirectory;

            txtFolder.Text = startFolder;
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
                txtFolder.Text = dlg.SelectedPath;

                // Persist selection
                _settings.LastFolder = dlg.SelectedPath;
                _settings.Save();
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

            ToggleBusy(true, "Building digest…");
            txtOutput.Clear();
            btnCopy.IsEnabled = false;
            btnSave.IsEnabled = false;

            try
            {
                var digest = await Task.Run(() =>
                    DirectoryDigester.BuildDigest(root, opts));

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
    }
}
