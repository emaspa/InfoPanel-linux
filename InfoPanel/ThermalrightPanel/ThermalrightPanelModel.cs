namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightPanelModel
    {
        Unknown,
        // Grand / Hydro / Peerless Vision 240/360 - 3.95" (480x480) - responds with SSCRM-V1
        PeerlessVision360,
        // Wonder Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3
        WonderVision360,
        // TL-M10 Vision - 9.16" (1920x462) - responds with SSCRM-V4
        TLM10Vision,
        // Trofeo Vision 6.86" - HID (VID 0x0416 / PID 0x5302)
        // Resolution determined from init response PM byte
        TrofeoVision,
        // Trofeo Vision 9.16" - USB bulk (VID 0x0416 / PID 0x5408)
        TrofeoVision916,

        // Trofeo Vision 2.4" - HID (VID 0x0416 / PID 0x5302), same identifier as 6.86" but PM byte 0x3A
        TrofeoVision240,

        // Backward compatibility alias (was renamed to TrofeoVision)
        TrofeoVision686 = TrofeoVision,
    }
}
