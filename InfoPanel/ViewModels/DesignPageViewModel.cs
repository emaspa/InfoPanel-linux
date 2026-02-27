using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels
{
    public partial class DesignPageViewModel : ObservableObject
    {
        public ObservableCollection<Profile> Profiles => ConfigModel.Instance.Profiles;
        public Profile? SelectedProfile
        {
            get => SharedModel.Instance.SelectedProfile;
            set
            {
                if (value != null)
                    SharedModel.Instance.SelectedProfile = value;
            }
        }

        public ObservableCollection<DisplayItem> DisplayItems => SharedModel.Instance.DisplayItems;

        public DisplayItem? SelectedItem
        {
            get => SharedModel.Instance.SelectedItem;
            set => SharedModel.Instance.SelectedItem = value;
        }

        [ObservableProperty]
        private string _searchFilter = string.Empty;

        public ObservableCollection<DisplayItem> FilteredDisplayItems { get; } = [];

        public PluginSensorsViewModel PluginSensors { get; } = new();
        public HwmonSensorsViewModel HwmonSensors { get; } = new();

        public DesignPageViewModel()
        {
            SharedModel.Instance.PropertyChanged += OnSharedModelPropertyChanged;
            DisplayItems.CollectionChanged += (_, _) => RefreshFilteredItems();
            RefreshFilteredItems();
        }

        private void OnSharedModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SharedModel.SelectedProfile):
                    OnPropertyChanged(nameof(SelectedProfile));
                    OnPropertyChanged(nameof(DisplayItems));
                    DisplayItems.CollectionChanged += (_, _) => RefreshFilteredItems();
                    RefreshFilteredItems();
                    break;
                case nameof(SharedModel.SelectedItem):
                    OnPropertyChanged(nameof(SelectedItem));
                    break;
            }
        }

        partial void OnSearchFilterChanged(string value)
        {
            RefreshFilteredItems();
        }

        private void RefreshFilteredItems()
        {
            FilteredDisplayItems.Clear();
            var filter = SearchFilter?.Trim();
            foreach (var item in DisplayItems)
            {
                if (string.IsNullOrEmpty(filter) ||
                    item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    item.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredDisplayItems.Add(item);
                }
            }
        }

        [RelayCommand]
        private void RefreshSensors()
        {
            try { PluginSensors.Refresh(); }
            catch (System.IO.FileNotFoundException) { /* Plugin loader not available */ }

            try { HwmonSensors.Refresh(); }
            catch (System.IO.FileNotFoundException) { /* Hwmon not available */ }
        }

        [RelayCommand]
        private void AddTextItem()
        {
            if (SelectedProfile == null) return;
            var item = new TextDisplayItem("New Text", SelectedProfile);
            SharedModel.Instance.AddDisplayItem(item);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void AddClockItem()
        {
            if (SelectedProfile == null) return;
            var item = new ClockDisplayItem("Clock", SelectedProfile);
            SharedModel.Instance.AddDisplayItem(item);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void AddCalendarItem()
        {
            if (SelectedProfile == null) return;
            var item = new CalendarDisplayItem("Calendar", SelectedProfile);
            SharedModel.Instance.AddDisplayItem(item);
            SaveDisplayItems();
        }

        [RelayCommand]
        private async Task AddImageItem()
        {
            if (SelectedProfile == null) return;

            var topLevel = GetMainWindow();
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.svg", "*.webp"] },
                    new FilePickerFileType("All files") { Patterns = ["*"] }
                ]
            });

            if (files.Count > 0)
            {
                var item = new ImageDisplayItem("Image", SelectedProfile)
                {
                    FilePath = files[0].Path.LocalPath
                };
                SharedModel.Instance.AddDisplayItem(item);
                SaveDisplayItems();
            }
        }

        [RelayCommand]
        private void AddShapeItem()
        {
            if (SelectedProfile == null) return;
            var item = new ShapeDisplayItem("Shape", SelectedProfile);
            SharedModel.Instance.AddDisplayItem(item);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void AddGroupItem()
        {
            if (SelectedProfile == null) return;
            var item = new GroupDisplayItem { Name = "Group" };
            item.SetProfile(SelectedProfile);
            SharedModel.Instance.AddDisplayItem(item);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void DeleteItem()
        {
            if (SelectedItem == null) return;
            SharedModel.Instance.RemoveDisplayItem(SelectedItem);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void MoveItemUp()
        {
            if (SelectedItem == null) return;
            SharedModel.Instance.PushDisplayItemBy(SelectedItem, -1);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void MoveItemDown()
        {
            if (SelectedItem == null) return;
            SharedModel.Instance.PushDisplayItemBy(SelectedItem, 1);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void PushToTop()
        {
            if (SelectedItem == null) return;
            SharedModel.Instance.PushDisplayItemToTop(SelectedItem);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void PushToBottom()
        {
            if (SelectedItem == null) return;
            SharedModel.Instance.PushDisplayItemToEnd(SelectedItem);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void DuplicateItem()
        {
            if (SelectedItem == null || SelectedProfile == null) return;
            var clone = (DisplayItem)SelectedItem.Clone();
            clone.X += 10;
            clone.Y += 10;
            SharedModel.Instance.AddDisplayItem(clone);
            SaveDisplayItems();
        }

        [RelayCommand]
        private void SaveDisplayItems()
        {
            SharedModel.Instance.SaveDisplayItems();
        }

        [RelayCommand]
        private async Task ReloadItems()
        {
            await SharedModel.Instance.ReloadDisplayItems();
            RefreshFilteredItems();
        }

        private static Window? GetMainWindow()
        {
            return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;
        }
    }
}
