namespace InfoPanel.Enums
{
    /// <summary>
    /// Union of the Windows fork's values (HwInfo, Libre, Plugin) and the Linux
    /// port's Hwmon so profiles written by either app deserialize on both.
    /// XmlSerializer persists enum member names, so names and values must never change.
    /// </summary>
    public enum SensorType
    {
        HwInfo = 0,
        Libre = 1,
        Plugin = 2,
        Hwmon = 3
    }
}
