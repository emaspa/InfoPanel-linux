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
        // Trofeo Vision - 9.16" (1920x480) - VID 0x0416 / PID 0x5408
        TrofeoVision,
        // Trofeo Vision - 6.86" (1280x480) - VID 0x0416 / PID 0x5302
        TrofeoVision686,
    }
}
