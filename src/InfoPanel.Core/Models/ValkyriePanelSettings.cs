using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Models;

/// <summary>Separate retail families while sharing the LCD transport implementation.</summary>
public static class ValkyriePanelSettings
{
    public static IEnumerable<VmaxPanelDevice> EnabledDevices(Settings settings) =>
        settings.VmaxPanelDevices.Where(d => settings.VmaxPanelMultiDeviceMode && d.Enabled)
            .Concat(settings.ValkyriePanelDevices.Where(d => settings.ValkyriePanelMultiDeviceMode && d.Enabled));

    public static bool Migrate(Settings settings)
    {
        var old = settings.VmaxPanelDevices.Where(d => d.ModelInfo?.IsValkyrie == true).ToList();
        foreach (var device in old)
        {
            if (!settings.ValkyriePanelDevices.Any(d => d.DeviceId == device.DeviceId))
                settings.ValkyriePanelDevices.Add(device);
            settings.VmaxPanelDevices.Remove(device);
            foreach (var binding in settings.HotkeyBindings.Where(b => b.DeviceType == "Vmax" && b.DeviceId == device.DeviceId))
                binding.DeviceType = "Valkyrie";
        }
        if (old.Count == 0) return false;
        settings.ValkyriePanelMultiDeviceMode |= settings.VmaxPanelMultiDeviceMode;
        if (settings.VmaxPanelDevices.Count == 0) settings.VmaxPanelMultiDeviceMode = false;
        return true;
    }
}
