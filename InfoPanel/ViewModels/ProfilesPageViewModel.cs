using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels
{
    public partial class ProfilesPageViewModel : ObservableObject
    {
        public ObservableCollection<Profile> Profiles => ConfigModel.Instance.Profiles;

        [ObservableProperty]
        private Profile? _selectedProfile;

        public ProfilesPageViewModel()
        {
            SelectedProfile = SharedModel.Instance.SelectedProfile;
        }

        partial void OnSelectedProfileChanged(Profile? value)
        {
            if (value != null)
            {
                SharedModel.Instance.SelectedProfile = value;
            }
        }

        [RelayCommand]
        private void AddProfile()
        {
            var profile = new Profile
            {
                Name = $"Profile {Profiles.Count + 1}",
                Active = false
            };
            ConfigModel.Instance.AddProfile(profile);
            ConfigModel.Instance.SaveProfiles();
            SelectedProfile = profile;
        }

        [RelayCommand]
        private void DeleteProfile()
        {
            if (SelectedProfile == null) return;

            var toDelete = SelectedProfile;
            if (ConfigModel.Instance.RemoveProfile(toDelete))
            {
                ConfigModel.Instance.SaveProfiles();
                SelectedProfile = Profiles.Count > 0 ? Profiles[0] : null;
            }
        }

        [RelayCommand]
        private void DuplicateProfile()
        {
            if (SelectedProfile == null) return;

            var clone = (Profile)SelectedProfile.Clone();
            clone.Guid = Guid.NewGuid();
            clone.Name = SelectedProfile.Name + " (Copy)";
            clone.Active = false;

            ConfigModel.Instance.AddProfile(clone);
            ConfigModel.Instance.SaveProfiles();
            SelectedProfile = clone;
        }

        [RelayCommand]
        private void SaveProfiles()
        {
            ConfigModel.Instance.SaveProfiles();
        }

        [RelayCommand]
        private void ResetPosition()
        {
            if (SelectedProfile == null) return;
            SelectedProfile.WindowX = 0;
            SelectedProfile.WindowY = 0;
        }

        [RelayCommand]
        private async Task ImportProfile()
        {
            try
            {
                var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow : null;

                if (topLevel == null) return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                    new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = "Import Profile",
                        AllowMultiple = false,
                        FileTypeFilter =
                        [
                            new Avalonia.Platform.Storage.FilePickerFileType("InfoPanel Profile") { Patterns = ["*.infopanel"] },
                            new Avalonia.Platform.Storage.FilePickerFileType("All files") { Patterns = ["*"] }
                        ]
                    });

                if (files.Count > 0)
                {
                    SharedModel.Instance.ImportProfile(files[0].Path.LocalPath);
                    ConfigModel.Instance.SaveProfiles();
                    OnPropertyChanged(nameof(Profiles));
                }
            }
            catch (Exception)
            {
                // Import failed - user will see no change
            }
        }

        [RelayCommand]
        private async Task Revert()
        {
            if (SelectedProfile == null) return;
            // Reload display items for the current profile
            await SharedModel.Instance.ReloadDisplayItems();
        }
    }
}
