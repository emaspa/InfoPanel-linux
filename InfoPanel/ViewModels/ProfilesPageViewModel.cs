using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.Models;
using System;
using System.Collections.ObjectModel;

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
    }
}
