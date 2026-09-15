using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.ViewModels;

namespace InfoPanel.Designer;

internal static class DesignerSensorBinding
{
    internal static SensorBinding FromLeaf(SensorTreeItem leaf)
    {
        if (!leaf.IsSensor) throw new ArgumentException("Select a bindable sensor.", nameof(leaf));
        return leaf.SensorType == SensorType.Plugin
            ? SensorBinding.ForPlugin(leaf.SensorId!, leaf.Name)
            : SensorBinding.ForHardware(leaf.SensorId!, leaf.ChipName, leaf.Name);
    }

    internal static void Replace(DesignerSession session, DisplayItem target, SensorTreeItem leaf)
    {
        if (!leaf.IsSensor || target is TableSensorDisplayItem && leaf.SensorType != SensorType.Plugin
            || SensorBinding.Capture(target) is not { } previous) return;
        session.Undo.Execute(new SetPropertyAction<SensorBinding>(target, "Sensor binding",
            binding => SensorBinding.Apply(target, binding), previous, FromLeaf(leaf)));
    }

    internal static SensorDisplayItem CreateSensorItem(SensorTreeItem leaf, Profile profile)
    {
        var item = new SensorDisplayItem
        {
            Name = leaf.Name,
            X = profile.Width / 3,
            Y = profile.Height / 3,
            Font = profile.Font,
            FontSize = profile.FontSize,
            Color = profile.Color,
        };
        SensorBinding.Apply(item, FromLeaf(leaf));
        return item;
    }
}
