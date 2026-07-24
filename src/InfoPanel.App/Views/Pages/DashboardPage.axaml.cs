using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using InfoPanel.Models;
using InfoPanel.Stores;
using InfoPanel.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace InfoPanel.Views.Pages
{
    public partial class DashboardPage : UserControl
    {
        private readonly ObservableCollection<ProfileCardViewModel> _cards = [];
        private DispatcherTimer? _thumbnailTimer;
        private App? _app;

        private void NavCard_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string tag && TopLevel.GetTopLevel(this) is Views.MainWindow window)
            {
                window.NavigateTo(tag);
            }
        }

        private void OpenLink_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string url)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
                }
                catch
                {
                    // no handler registered; nothing sensible to do
                }
            }
        }

        public DashboardPage()
        {
            InitializeComponent();

            // Sensor-browsing UI: poll all sensors while this page is visible.
            bool sensorViewer = false;
            Loaded += (_, _) => { if (!sensorViewer) { sensorViewer = true; InfoPanel.Models.SensorDemand.AddUiViewer(); } };
            Unloaded += (_, _) => { if (sensorViewer) { sensorViewer = false; InfoPanel.Models.SensorDemand.RemoveUiViewer(); } };
            ProfileList.ItemsSource = _cards;

            Loaded += (_, _) =>
            {
                if (_app == null && Avalonia.Application.Current is App app)
                {
                    _app = app;
                    RebuildCards();
                    app.Host.Profiles.CollectionChanged += Profiles_CollectionChanged;
                }

                _thumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _thumbnailTimer.Tick += (_, _) => RefreshThumbnails();
                _thumbnailTimer.Start();
                RefreshThumbnails();
            };

            Unloaded += (_, _) =>
            {
                _thumbnailTimer?.Stop();
                _thumbnailTimer = null;
            };
        }

        private void Profiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildCards();

        private void RebuildCards()
        {
            _cards.Clear();
            if (_app == null) return;

            foreach (var profile in _app.Host.Profiles)
            {
                _cards.Add(new ProfileCardViewModel(profile, _app.Host));
            }
        }

        private void RefreshThumbnails()
        {
            foreach (var card in _cards)
            {
                card.RefreshThumbnail();
            }
        }

        private void NewProfile_Click(object? sender, RoutedEventArgs e)
        {
            if (_app == null) return;

            var profile = new Profile
            {
                Guid = Guid.NewGuid(),
                Name = $"Profile {_app.Host.Profiles.Count + 1}",
                Width = 800,
                Height = 480,
            };

            _app.Host.Profiles.Add(profile);
            _app.Host.SaveProfiles();
            DisplayItemStore.Instance.Save(profile);
        }

        private void Duplicate_Click(object? sender, RoutedEventArgs e)
        {
            if (_app == null || (sender as Button)?.Tag is not ProfileCardViewModel card) return;

            var source = card.Profile;
            var clone = new Profile
            {
                Guid = Guid.NewGuid(),
                Name = source.Name + " Copy",
                Width = source.Width,
                Height = source.Height,
                BackgroundColor = source.BackgroundColor,
                Font = source.Font,
                FontSize = source.FontSize,
                Color = source.Color,
                FontScale = source.FontScale,
            };

            // copy display items
            var items = DisplayItemStore.Instance.GetOrLoad(clone);
            foreach (var item in DisplayItemStore.Instance.GetSnapshot(source))
            {
                var copy = (DisplayItem)item.Clone();
                copy.SetProfile(clone);
                items.Add(copy);
            }

            _app.Host.Profiles.Add(clone);
            _app.Host.SaveProfiles();
            DisplayItemStore.Instance.Save(clone);
        }

        private async void Import_Click(object? sender, RoutedEventArgs e)
        {
            if (_app == null) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import profile",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType("InfoPanel profile") { Patterns = ["*.infopanel"] }
                ]
            });

            if (files.Count == 0 || files[0].Path is not { IsFile: true } uri) return;

            if (Persistence.ProfileTransfer.Import(uri.LocalPath) is Models.Profile imported)
            {
                _app.Host.Profiles.Add(imported);
                _app.Host.SaveProfiles();
            }
        }

        private async void Export_Click(object? sender, RoutedEventArgs e)
        {
            if (_app == null || (sender as Button)?.Tag is not ProfileCardViewModel card) return;

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Export profile to folder",
                AllowMultiple = false
            });

            if (folders.Count == 0 || folders[0].Path is not { IsFile: true } uri) return;

            // make sure the latest edits are on disk before zipping
            DisplayItemStore.Instance.Save(card.Profile);
            Persistence.ProfileTransfer.Export(card.Profile, uri.LocalPath);
        }

        private void ResetPosition_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ProfileCardViewModel card)
            {
                card.Profile.WindowX = 0;
                card.Profile.WindowY = 0;
                _app?.Host.SaveProfiles();
            }
        }

        private void SaveProfile_Click(object? sender, RoutedEventArgs e)
        {
            _app?.Host.SaveProfiles();
        }

        private void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (_app == null || (sender as Button)?.Tag is not ProfileCardViewModel card) return;

            if (_app.Host.Profiles.Count <= 1)
            {
                return; // always keep one profile (v1 behavior)
            }

            DisplayWindowManager.Instance.CloseDisplayWindow(card.Profile.Guid);
            _app.Host.Profiles.Remove(card.Profile);
            _app.Host.SaveProfiles(); // cleanupOrphans deletes the items file and assets
        }
    }
}
