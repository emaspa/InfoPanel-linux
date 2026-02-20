using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfoPanel.BeadaPanel;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.ThermalrightPanel;
using InfoPanel.TuringPanel;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace InfoPanel.ViewModels
{
    public partial class UsbPanelsPageViewModel : ObservableObject
    {
        private static readonly ILogger Logger = Log.ForContext<UsbPanelsPageViewModel>();

        public ObservableCollection<LCD_ROTATION> RotationValues { get; }

        public Settings Settings => ConfigModel.Instance.Settings;

        public ObservableCollection<BeadaPanelDevice> BeadaPanelDevices => ConfigModel.Instance.Settings.BeadaPanelDevices;
        public ObservableCollection<TuringPanelDevice> TuringPanelDevices => ConfigModel.Instance.Settings.TuringPanelDevices;
        public ObservableCollection<ThermalrightPanelDevice> ThermalrightPanelDevices => ConfigModel.Instance.Settings.ThermalrightPanelDevices;

        public ObservableCollection<Profile> Profiles => ConfigModel.Instance.Profiles;

        [ObservableProperty]
        private bool _isDiscoveringBeadaPanel;

        [ObservableProperty]
        private bool _isDiscoveringTuringPanel;

        [ObservableProperty]
        private bool _isDiscoveringThermalrightPanel;

        public UsbPanelsPageViewModel()
        {
            RotationValues = new ObservableCollection<LCD_ROTATION>(
                Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());
        }

        [RelayCommand]
        private async Task DiscoverBeadaPanelDevices()
        {
            IsDiscoveringBeadaPanel = true;
            try
            {
                int vendorId = 0x4e58;
                int productId = 0x1001;

                var allDevices = UsbDevice.AllDevices;
                Logger.Information("BeadaPanel Discovery: Scanning {Count} USB devices for VID={VendorId:X4} PID={ProductId:X4}",
                    allDevices.Count, vendorId, productId);

                foreach (UsbRegistry deviceReg in allDevices)
                {
                    if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                    {
                        var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;
                        var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                        {
                            Logger.Warning("BeadaPanel Discovery: Skipping device with missing properties - DeviceID: '{DeviceId}', LocationInformation: '{DeviceLocation}'",
                                deviceId, deviceLocation);
                            continue;
                        }

                        Logger.Information("BeadaPanel Discovery: Found matching device - FullName: '{FullName}', DevicePath: '{DevicePath}', SymbolicName: '{SymbolicName}'",
                            deviceReg.FullName, deviceReg.DevicePath, deviceReg.SymbolicName);

                        try
                        {
                            var panelInfo = await BeadaPanelHelper.GetPanelInfoAsync(deviceReg);
                            if (panelInfo != null && BeadaPanelModelDatabase.Models.ContainsKey(panelInfo.Model))
                            {
                                Logger.Information("Discovered BeadaPanel device: {PanelInfo}", panelInfo);
                                ConfigModel.Instance.AccessSettings(settings =>
                                {
                                    var device = settings.BeadaPanelDevices.FirstOrDefault(d => d.IsMatching(deviceId, deviceLocation, panelInfo));

                                    if (device != null)
                                    {
                                        device.DeviceLocation = deviceLocation;
                                        device.UpdateRuntimeProperties(panelInfo: panelInfo);
                                    }
                                    else
                                    {
                                        device = new BeadaPanelDevice()
                                        {
                                            DeviceId = deviceId,
                                            DeviceLocation = deviceLocation,
                                            Model = panelInfo.Model.ToString(),
                                            ProfileGuid = ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty,
                                            RuntimeProperties = new BeadaPanelDevice.BeadaPanelDeviceRuntimeProperties()
                                            {
                                                PanelInfo = panelInfo
                                            }
                                        };

                                        settings.BeadaPanelDevices.Add(device);
                                    }
                                });
                            }
                            else
                            {
                                Logger.Information("Skipping device {DevicePath} - StatusLink unavailable (likely already running)", deviceReg.DevicePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Error discovering BeadaPanel device");
                        }
                    }
                }
            }
            finally
            {
                IsDiscoveringBeadaPanel = false;
            }
        }

        [RelayCommand]
        private void RemoveBeadaPanelDevice(BeadaPanelDevice device)
        {
            ConfigModel.Instance.AccessSettings(settings =>
            {
                var deviceConfig = settings.BeadaPanelDevices.FirstOrDefault(c => c.Id == device.Id);
                if (deviceConfig != null)
                {
                    if (BeadaPanelTask.Instance.IsDeviceRunning(deviceConfig.Id))
                    {
                        _ = BeadaPanelTask.Instance.StopDevice(deviceConfig.Id);
                    }
                    settings.BeadaPanelDevices.Remove(deviceConfig);
                }
            });
        }

        [RelayCommand]
        private async Task DiscoverTuringPanelDevices()
        {
            IsDiscoveringTuringPanel = true;
            try
            {
                var serialDeviceTask = TuringPanelHelper.GetSerialDevices();
                var usbDeviceTask = TuringPanelHelper.GetUsbDevices();

                await Task.WhenAll(serialDeviceTask, usbDeviceTask);

                var discoveredDevices = usbDeviceTask.Result.Concat(serialDeviceTask.Result).ToList();

                foreach (var discoveredDevice in discoveredDevices)
                {
                    ConfigModel.Instance.AccessSettings(settings =>
                    {
                        var device = settings.TuringPanelDevices.FirstOrDefault(d => d.IsMatching(discoveredDevice.DeviceId));

                        if (device == null)
                        {
                            discoveredDevice.ProfileGuid = ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty;
                            settings.TuringPanelDevices.Add(discoveredDevice);
                            Logger.Information("TuringPanel Discovery: Added new device with DeviceId '{DeviceId}'", discoveredDevice.DeviceId);
                        }
                        else
                        {
                            device.DeviceLocation = discoveredDevice.DeviceLocation;
                            Logger.Information("TuringPanel Discovery: Device with DeviceId '{DeviceId}' already exists", discoveredDevice.DeviceId);
                        }
                    });
                }
            }
            finally
            {
                IsDiscoveringTuringPanel = false;
            }
        }

        [RelayCommand]
        private void RemoveTuringPanelDevice(TuringPanelDevice device)
        {
            ConfigModel.Instance.AccessSettings(settings =>
            {
                var deviceConfig = settings.TuringPanelDevices.FirstOrDefault(c => c.Id == device.Id);
                if (deviceConfig != null)
                {
                    if (TuringPanelTask.Instance.IsDeviceRunning(deviceConfig.Id))
                    {
                        _ = TuringPanelTask.Instance.StopDevice(deviceConfig.Id);
                    }
                    settings.TuringPanelDevices.Remove(deviceConfig);
                }
            });
        }

        [RelayCommand]
        private Task DiscoverThermalrightPanelDevices()
        {
            IsDiscoveringThermalrightPanel = true;
            try
            {
                var discoveredDevices = ThermalrightPanelHelper.ScanDevices();
                Logger.Information("ThermalrightPanel Discovery: Found {Count} devices", discoveredDevices.Count);

                foreach (var discoveredDevice in discoveredDevices)
                {
                    ConfigModel.Instance.AccessSettings(settings =>
                    {
                        var device = settings.ThermalrightPanelDevices.FirstOrDefault(d =>
                            d.IsMatching(discoveredDevice.DeviceId, discoveredDevice.DeviceLocation, discoveredDevice.Model));

                        if (device == null)
                        {
                            var newDevice = new ThermalrightPanelDevice()
                            {
                                DeviceId = discoveredDevice.DeviceId,
                                DeviceLocation = discoveredDevice.DeviceLocation,
                                Model = discoveredDevice.Model,
                                ProfileGuid = ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty
                            };

                            if (discoveredDevice.ModelInfo != null)
                            {
                                newDevice.RuntimeProperties.Name = $"Thermalright {discoveredDevice.ModelInfo.Name}";
                            }

                            settings.ThermalrightPanelDevices.Add(newDevice);
                            Logger.Information("ThermalrightPanel Discovery: Added new device with DeviceId '{DeviceId}'", discoveredDevice.DeviceId);
                        }
                        else
                        {
                            device.DeviceLocation = discoveredDevice.DeviceLocation;
                            Logger.Information("ThermalrightPanel Discovery: Device with DeviceId '{DeviceId}' already exists", discoveredDevice.DeviceId);
                        }
                    });
                }
            }
            finally
            {
                IsDiscoveringThermalrightPanel = false;
            }

            return Task.CompletedTask;
        }

        [RelayCommand]
        private void RemoveThermalrightPanelDevice(ThermalrightPanelDevice device)
        {
            ConfigModel.Instance.AccessSettings(settings =>
            {
                var deviceConfig = settings.ThermalrightPanelDevices.FirstOrDefault(c => c.Id == device.Id);
                if (deviceConfig != null)
                {
                    if (ThermalrightPanelTask.Instance.IsDeviceRunning(deviceConfig.Id))
                    {
                        _ = ThermalrightPanelTask.Instance.StopDevice(deviceConfig.Id);
                    }
                    settings.ThermalrightPanelDevices.Remove(deviceConfig);
                }
            });
        }
    }
}
