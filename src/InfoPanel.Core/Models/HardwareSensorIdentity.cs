using System.Xml.Serialization;

namespace InfoPanel.Models;

/// <summary>Optional migration hints; LibreSensorId remains the persisted binding.</summary>
public sealed class HardwareSensorIdentity
{
    [XmlElement(IsNullable = false)]
    public string? ChipName { get; set; }

    [XmlElement(IsNullable = false)]
    public string? ChannelLabel { get; set; }

    [XmlElement(IsNullable = false)]
    public string? OriginalId { get; set; }

    internal HardwareSensorIdentity Copy() => new()
    {
        ChipName = ChipName, ChannelLabel = ChannelLabel, OriginalId = OriginalId
    };
}
