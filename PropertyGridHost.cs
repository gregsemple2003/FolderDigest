using System.Windows;
using System.Windows.Forms.Integration;
using WinForms = System.Windows.Forms;

namespace FolderDigest
{
    /// <summary>
    /// Simple WPF wrapper around WinForms PropertyGrid so we can bind SelectedObject.
    /// </summary>
    public sealed class PropertyGridHost : WindowsFormsHost
    {
        private readonly WinForms.PropertyGrid _grid;

        public PropertyGridHost()
        {
            _grid = new WinForms.PropertyGrid
            {
                Dock = WinForms.DockStyle.Fill,
                ToolbarVisible = false,
                HelpVisible = false,
                PropertySort = WinForms.PropertySort.CategorizedAlphabetical
            };

            Child = _grid;
        }

        public static readonly DependencyProperty SelectedObjectProperty =
            DependencyProperty.Register(
                nameof(SelectedObject),
                typeof(object),
                typeof(PropertyGridHost),
                new PropertyMetadata(null, OnSelectedObjectChanged));

        public object? SelectedObject
        {
            get => GetValue(SelectedObjectProperty);
            set => SetValue(SelectedObjectProperty, value);
        }

        private static void OnSelectedObjectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PropertyGridHost host)
                return;

            host._grid.SelectedObject = e.NewValue;
        }
    }
}

