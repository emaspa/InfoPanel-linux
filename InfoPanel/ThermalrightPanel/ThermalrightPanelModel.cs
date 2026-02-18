namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightPanelModel
    {
        Unknown,
        // Peerless Vision 360 - 3.95" (480x480) - responds with SSCRM-V1
        PeerlessVision360,
        // Wonder Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3
        WonderVision360,
        // TL-M10 VISION - 9.16" (1920x462) - responds with SSCRM-V4
        TLM10Vision,
        // Trofeo Vision - HID panels (VID 0x0416 / PID 0x5302)
        // Resolution determined from init response PM byte:
        //   PM 128 (0x80) = 6.86" 1280x480
        //   PM 65  (0x41) = 9.16" 1920x462
        TrofeoVision,
    }
}
