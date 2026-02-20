using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;

namespace InfoPanel.ViewModels
{
    public partial class SettingsPageViewModel : ObservableObject
    {
        public Settings Settings => ConfigModel.Instance.Settings;
    }
}
