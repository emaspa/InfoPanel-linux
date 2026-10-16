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
            // Only confirmed V360 geometry permits removing the old erroneous DS339.
            var stale = found.Model == VmaxPanelModel.ValkyrieV360
                ? settings.JonsboPanelDevices.FirstOrDefault(d => d.Model == JonsboPanelModel.DS339 && Same(d.DeviceId, found.DeviceId))
                : null;
            if (existing == null)
            {
                existing = new VmaxPanelDevice
                {
                    DeviceId = found.DeviceId, DeviceLocation = found.DeviceLocation, Model = found.Model,
                    TargetFrameRate = isValkyrie ? 10 : 1,
                    // Preserve assignments, but don't start a previously disabled device.
                    ProfileGuid = stale?.ProfileGuid ?? Guid.Empty,
                    Enabled = stale?.Enabled ?? false,
                    Brightness = stale?.Brightness ?? 99,
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
