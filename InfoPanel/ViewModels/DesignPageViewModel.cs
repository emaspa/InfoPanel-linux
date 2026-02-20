using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using System.Collections.ObjectModel;

namespace InfoPanel.ViewModels
{
    public partial class DesignPageViewModel : ObservableObject
    {
        public Profile? SelectedProfile => SharedModel.Instance.SelectedProfile;

        public ObservableCollection<DisplayItem> DisplayItems => SharedModel.Instance.DisplayItems;

        public DisplayItem? SelectedItem
        {
            get => SharedModel.Instance.SelectedItem;
            set => SharedModel.Instance.SelectedItem = value;
        }

        public DesignPageViewModel()
        {
            SharedModel.Instance.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SharedModel.SelectedProfile))
                {
                    OnPropertyChanged(nameof(SelectedProfile));
                    OnPropertyChanged(nameof(DisplayItems));
                }
                else if (e.PropertyName == nameof(SharedModel.SelectedItem))
                {
                    OnPropertyChanged(nameof(SelectedItem));
                }
            };
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
    }
}
