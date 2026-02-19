namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightPanelModel
    {
        Unknown,
        // Grand / Hydro / Peerless Vision 240/360 - 3.95" (480x480) - responds with SSCRM-V1
        PeerlessVision360,
        // Wonder Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x01
        WonderVision360,
        // Rainbow Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x02
        RainbowVision360,
        // TL-M10 Vision - 9.16" (1920x462) - responds with SSCRM-V4
        TLM10Vision,
        // Trofeo Vision 6.86" - HID (VID 0x0416 / PID 0x5302)
        // Resolution determined from init response PM byte
        TrofeoVision,
        // Trofeo Vision 9.16" - USB bulk (VID 0x0416 / PID 0x5408)
        TrofeoVision916,

        // Frozen Warframe 240/360 - HID (VID 0x0416 / PID 0x5302), same identifier as Trofeo 6.86" but PM byte 0x3A -> 320x240
        FrozenWarframe,

        // Generic HID Trofeo panels identified only by PM byte (model unknown)
        // PM 0x20 -> 320x320, RGB565, big-endian
        TrofeoVision320,
        // PM 0x40 -> 1600x720
        TrofeoVision1600x720,
        // PM 0x0A -> 960x540
        TrofeoVision960x540,
        // PM 0x0C -> 800x480
        TrofeoVision800x480,

        // HID 0x5302 models identified by PM byte
        AssassinSpirit120Vision,  // PM 0x24 (36)  -> 240x240, RGB565
        AS120Vision,              // PM 0x32 (50)  -> 320x240, RGB565
        AS120VisionB,             // PM 0x33 (51)  -> 320x240, RGB565
        BA120Vision,              // PM 0x34 (52)  -> 320x240, RGB565
        BA120VisionB,             // PM 0x35 (53)  -> 320x240, RGB565
        FrozenWarframePro,        // PM 0x64 (100) -> 320x240, RGB565
        EliteVisionHid,           // PM 0x65 (101) -> 320x240, RGB565

        // ChiZhu bulk (87AD:70DB) PM=0x20 variant — 320x320, RGB565
        ChiZhuVision320x320,

        // Backward compatibility alias (was renamed to TrofeoVision)
        TrofeoVision686 = TrofeoVision,
    }
}
