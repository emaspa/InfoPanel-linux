using System.Xml.Serialization;
using InfoPanel.JonsboPanel;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.VmaxPanel;
using Xunit;

namespace InfoPanel.App.Tests;

public class ValkyrieDiscoveryTests
{
    private const string DeviceId = @"USB\VID_345F&PID_9132\test-panel";
    private static VmaxPanelDiscoveryInfo V360() => new()
    {
        DeviceId = DeviceId, DeviceLocation = "test-panel", SerialNumber = "test-panel",
        Model = VmaxPanelModel.ValkyrieV360,
        ModelInfo = VmaxPanelModelDatabase.GetModelByPanelSize(320, 240),
    };

    [Fact]
    public void UnreadableBridgeIsNotAssignedARetailModel()
    {
        Assert.False(JonsboPanelHelper.IsDs339(null));
        Assert.Null(VmaxPanelHelper.ModelFromSize(null));
        Assert.True(JonsboPanelHelper.IsDs339((376, 960)));
        Assert.False(JonsboPanelHelper.IsDs339((320, 240)));
        Assert.Null(VmaxPanelHelper.ModelFromSize((480, 480)));
    }

    [Fact]
    public void ConfirmedV360ReplacesOnlyMatchingMistakenJonsboEntry()
    {
        var profile = Guid.NewGuid();
        var settings = new Settings { JonsboPanelMultiDeviceMode = true };
        settings.JonsboPanelDevices.Add(new JonsboPanelDevice
        {
            DeviceId = DeviceId, Model = JonsboPanelModel.DS339,
            ProfileGuid = profile, Enabled = false,
        });
        var unrelated = new JonsboPanelDevice { DeviceId = "other", Model = JonsboPanelModel.DS339 };
        settings.JonsboPanelDevices.Add(unrelated);
        settings.HotkeyBindings.Add(new HotkeyBinding { DeviceId = DeviceId, DeviceType = "Jonsbo", ProfileGuid = profile });
        var result = PanelDiscoveryReconciler.Apply(settings, [], [V360()]);
        Assert.Equal((1, 0), result);
        Assert.Same(unrelated, Assert.Single(settings.JonsboPanelDevices));
        Assert.Empty(settings.VmaxPanelDevices);
        var device = Assert.Single(settings.ValkyriePanelDevices);
        Assert.Equal(profile, device.ProfileGuid);
        Assert.False(device.Enabled);
        Assert.Equal(LCD_ROTATION.RotateNone, device.Rotation);
        Assert.True(settings.ValkyriePanelMultiDeviceMode);
        Assert.Equal("Valkyrie", settings.HotkeyBindings[0].DeviceType);
        Assert.Equal((0, 1), PanelDiscoveryReconciler.Apply(settings, [], [V360()]));
        Assert.Single(settings.ValkyriePanelDevices);
    }

    [Fact]
    public void UnknownScanDoesNotDeleteSavedConfigurationsOrGuessModel()
    {
        var settings = new Settings();
        var old = new JonsboPanelDevice { DeviceId = DeviceId, Model = JonsboPanelModel.DS339 };
        settings.JonsboPanelDevices.Add(old);
        PanelDiscoveryReconciler.Apply(settings,
            [new JonsboPanelDiscoveryInfo { DeviceId = DeviceId, Model = JonsboPanelModel.DS339, Ms9132Confirmed = false }], []);
        Assert.Same(old, Assert.Single(settings.JonsboPanelDevices));
        Assert.Empty(settings.ValkyriePanelDevices);
        Assert.Empty(settings.VmaxPanelDevices);
    }

    [Fact]
    public void LegacyValkyrieConfigurationAndHotkeysMoveWithoutLosingSettings()
    {
        var settings = new Settings { VmaxPanelMultiDeviceMode = true };
        var device = new VmaxPanelDevice { DeviceId = DeviceId, Model = VmaxPanelModel.ValkyrieV360, Enabled = true, ProfileGuid = Guid.NewGuid(), Brightness = 42 };
        var vmax = new VmaxPanelDevice { DeviceId = "vmax", Model = VmaxPanelModel.Vmax46Inch };
        settings.VmaxPanelDevices.Add(device);
        settings.VmaxPanelDevices.Add(vmax);
        settings.HotkeyBindings.Add(new HotkeyBinding { DeviceId = DeviceId, DeviceType = "Vmax" });
        Assert.True(ValkyriePanelSettings.Migrate(settings));
        Assert.Same(device, Assert.Single(settings.ValkyriePanelDevices));
        Assert.Same(vmax, Assert.Single(settings.VmaxPanelDevices));
        Assert.Equal(42, device.Brightness);
        Assert.True(settings.ValkyriePanelMultiDeviceMode);
        Assert.True(settings.VmaxPanelMultiDeviceMode);
        Assert.Equal("Valkyrie", settings.HotkeyBindings[0].DeviceType);
        Assert.False(ValkyriePanelSettings.Migrate(settings));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public void FamilyMasterSwitchesGateStreamingIndependently(bool vmax, bool vk, int count)
    {
        var settings = new Settings { VmaxPanelMultiDeviceMode = vmax, ValkyriePanelMultiDeviceMode = vk };
        settings.VmaxPanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.Vmax46Inch, Enabled = true });
        settings.ValkyriePanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieV360, Enabled = true });
        settings.ValkyriePanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieMc360, Enabled = false });
        var enabled = ValkyriePanelSettings.EnabledDevices(settings).ToList();
        Assert.Equal(count, enabled.Count);
        Assert.Equal(vmax, enabled.Any(d => d.Model == VmaxPanelModel.Vmax46Inch));
        Assert.Equal(vk, enabled.Any(d => d.Model == VmaxPanelModel.ValkyrieV360));
    }

    [Fact]
    public void SeparateFamilySettingsRoundTripAndNotifyChanges()
    {
        var settings = new Settings { ValkyriePanelMultiDeviceMode = true };
        settings.ValkyriePanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieV360 });
        settings.ValkyriePanelDevices.Add(new VmaxPanelDevice { Model = VmaxPanelModel.ValkyrieMc360 });
        var serializer = new XmlSerializer(typeof(Settings));
        using var writer = new StringWriter();
        serializer.Serialize(writer, settings);
        using var reader = new StringReader(writer.ToString());
        var restored = Assert.IsType<Settings>(serializer.Deserialize(reader));
        Assert.True(restored.ValkyriePanelMultiDeviceMode);
        Assert.False(restored.VmaxPanelMultiDeviceMode);
        Assert.Equal(2, restored.ValkyriePanelDevices.Count);
        Assert.Empty(restored.VmaxPanelDevices);
        var notifications = new List<string?>();
        restored.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        restored.ValkyriePanelDevices[0].Brightness = 42;
        Assert.Contains(nameof(Settings.ValkyriePanelDevices), notifications);
    }
}
