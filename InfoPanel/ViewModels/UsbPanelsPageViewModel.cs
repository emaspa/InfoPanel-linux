using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace InfoPanel.ViewModels
{
    public partial class UsbPanelsPageViewModel : ObservableObject
    {
        public ObservableCollection<LCD_ROTATION> RotationValues { get; }

        public Settings Settings => ConfigModel.Instance.Settings;

        public ObservableCollection<BeadaPanelDevice> BeadaPanelDevices => ConfigModel.Instance.Settings.BeadaPanelDevices;
        public ObservableCollection<TuringPanelDevice> TuringPanelDevices => ConfigModel.Instance.Settings.TuringPanelDevices;
        public ObservableCollection<ThermalrightPanelDevice> ThermalrightPanelDevices => ConfigModel.Instance.Settings.ThermalrightPanelDevices;

        public ObservableCollection<Profile> Profiles => ConfigModel.Instance.Profiles;

        public UsbPanelsPageViewModel()
        {
            RotationValues = new ObservableCollection<LCD_ROTATION>(
                Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());
        }
    }
}
