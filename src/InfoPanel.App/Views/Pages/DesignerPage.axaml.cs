using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Designer;
using InfoPanel.Models;

namespace InfoPanel.Views.Pages
{
    public partial class DesignerPage : UserControl
    {
        private DesignerSession? _session;

        public DesignerPage()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                if (Avalonia.Application.Current is App app && ProfilePicker.ItemsSource == null)
                {
                    ProfilePicker.ItemsSource = app.Host.Profiles;
                    ProfilePicker.SelectedIndex = app.Host.Profiles.Count > 0 ? 0 : -1;
                }
            };
        }

        private void ProfilePicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ProfilePicker.SelectedItem is not Profile profile)
            {
                return;
            }

            if (_session != null)
            {
                _session.SaveNow();
                _session.Undo.StateChanged -= Undo_StateChanged;
                _session.SelectionChanged -= Session_SelectionChanged;
            }

            _session = new DesignerSession(profile);
            _session.Undo.StateChanged += Undo_StateChanged;
            _session.SelectionChanged += Session_SelectionChanged;
            Canvas.Session = _session;
            Canvas.ZoomChanged += (_, _) => ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
            ZoomLabel.Text = $"{Canvas.Zoom * 100:0}%";
        }

        private void Undo_StateChanged(object? sender, EventArgs e)
        {
            UndoButton.IsEnabled = _session?.Undo.CanUndo == true;
            RedoButton.IsEnabled = _session?.Undo.CanRedo == true;
        }

        private void Session_SelectionChanged(object? sender, EventArgs e)
        {
            var count = _session?.Selection.Count ?? 0;
            SelectionLabel.Text = count switch
            {
                0 => "",
                1 => _session!.Selection[0].Name,
                _ => $"{count} items selected"
            };
        }

        private void Undo_Click(object? sender, RoutedEventArgs e)
        {
            _session?.Undo.Undo();
            Canvas.InvalidateVisual();
        }

        private void Redo_Click(object? sender, RoutedEventArgs e)
        {
            _session?.Undo.Redo();
            Canvas.InvalidateVisual();
        }

        private void Snap_Click(object? sender, RoutedEventArgs e)
        {
            Canvas.SnapToGrid = SnapToggle.IsChecked == true;
        }

        private void Fit_Click(object? sender, RoutedEventArgs e)
        {
            Canvas.ZoomToFit();
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            _session?.SaveNow();
        }
    }
}
