using System.ComponentModel;

namespace InfoPanel.Models
{
    public enum LCD_ROTATION
    {
        [Description("No rotation")]
        RotateNone = 0,
        [Description("Rotate 90°")]
        Rotate90FlipNone = 1,
        [Description("Rotate 180°")]
        Rotate180FlipNone = 2,
        [Description("Rotate 270°")]
        Rotate270FlipNone = 3,
    }

    /// <summary>
    /// Video player status (stub replacing FlyleafLib.Status for Linux port)
    /// </summary>
    public enum VideoPlayerStatus
    {
        Stopped,
        Playing,
        Paused,
        Opening,
        Failed
    }
}
