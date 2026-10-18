using InfoPanel.JonsboPanel;
using InfoPanel.Models;
using InfoPanel.VmaxPanel;

namespace InfoPanel.Services;

/// <summary>Apply confirmed discovery results, repairing earlier MS9132 misidentification.</summary>
public static class PanelDiscoveryReconciler
{
    public static (int Added, int Updated) Apply(Settings settings,
        IEnumerable<JonsboPanelDiscoveryInfo> jonsbo, IEnumerable<VmaxPanelDiscoveryInfo> displays)
    {
        ValkyriePanelSettings.Migrate(settings);
        int added = 0, updated = 0;
        foreach (var found in jonsbo)
        {
            if (found.Model == JonsboPanelModel.DS339 && !found.Ms9132Confirmed) continue;
            var existing = settings.JonsboPanelDevices.FirstOrDefault(d => Same(d.DeviceId, found.DeviceId));
            if (existing == null)
            {
                settings.JonsboPanelDevices.Add(new JonsboPanelDevice
                {
                    DeviceId = found.DeviceId, DeviceLocation = found.DeviceLocation, Model = found.Model,
                });
                added++;
            }
            else
            {
                existing.DeviceLocation = found.DeviceLocation;
                existing.Model = found.Model;
                updated++;
            }
        }
        foreach (var found in displays)
        {
            var isValkyrie = found.ModelInfo?.IsValkyrie == true;
            var family = isValkyrie ? settings.ValkyriePanelDevices : settings.VmaxPanelDevices;
            var wasEmpty = family.Count == 0;
            var existing = family.FirstOrDefault(d => Same(d.DeviceId, found.DeviceId));
            // Only confirmed V360 geometry permits removing the entries earlier releases
            // created for it: a DS339 when its EDID was unreadable, otherwise a VMAX 4.6".
            var isV360 = found.Model == VmaxPanelModel.ValkyrieV360;
            var stale = isV360
                ? settings.JonsboPanelDevices.FirstOrDefault(d => d.Model == JonsboPanelModel.DS339 && Same(d.DeviceId, found.DeviceId))
                : null;
            var staleVmax = isV360 && !string.IsNullOrEmpty(found.LegacyDeviceId)
                ? settings.VmaxPanelDevices.FirstOrDefault(d => d.Model == VmaxPanelModel.Vmax46Inch && Same(d.DeviceId, found.LegacyDeviceId))
                : null;
            if (existing == null)
            {
                existing = new VmaxPanelDevice
                {
                    DeviceId = found.DeviceId, DeviceLocation = found.DeviceLocation, Model = found.Model,
                    TargetFrameRate = isValkyrie ? 10 : 1,
                    // Preserve assignments, but don't start a previously disabled device.
                    ProfileGuid = staleVmax?.ProfileGuid ?? stale?.ProfileGuid ?? Guid.Empty,
                    Enabled = staleVmax?.Enabled ?? stale?.Enabled ?? false,
                    Brightness = staleVmax?.Brightness ?? stale?.Brightness ?? 99,
                    Rotation = staleVmax?.Rotation ?? default,
                };
                family.Add(existing);
                added++;
            }
            else
            {
                existing.DeviceLocation = found.DeviceLocation;
                existing.Model = found.Model;
                updated++;
            }
            if (stale != null)
            {
                settings.JonsboPanelDevices.Remove(stale);
                foreach (var binding in settings.HotkeyBindings.Where(b => b.DeviceType == "Jonsbo" && Same(b.DeviceId, stale.DeviceId)))
                    binding.DeviceType = "Valkyrie";
                if (settings.JonsboPanelDevices.Count == 0) settings.JonsboPanelMultiDeviceMode = false;
            }
            if (staleVmax != null)
            {
                // The V360 is now keyed by serial, so hotkeys move to the new ID as well.
                settings.VmaxPanelDevices.Remove(staleVmax);
                foreach (var binding in settings.HotkeyBindings.Where(b => b.DeviceType == "Vmax" && Same(b.DeviceId, staleVmax.DeviceId)))
                {
                    binding.DeviceType = "Valkyrie";
                    binding.DeviceId = found.DeviceId;
                }
                if (settings.VmaxPanelDevices.Count == 0) settings.VmaxPanelMultiDeviceMode = false;
            }
            if (wasEmpty)
            {
                if (isValkyrie) settings.ValkyriePanelMultiDeviceMode = true;
                else settings.VmaxPanelMultiDeviceMode = true;
            }
        }
        return (added, updated);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
