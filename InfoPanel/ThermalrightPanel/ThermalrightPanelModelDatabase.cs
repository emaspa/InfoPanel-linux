using System.Collections.Generic;

namespace InfoPanel.ThermalrightPanel
{
    public static class ThermalrightPanelModelDatabase
    {
        // Primary Thermalright VID/PID (most panels)
        public const int THERMALRIGHT_VENDOR_ID = 0x87AD;
        public const int THERMALRIGHT_PRODUCT_ID = 0x70DB;

        // Trofeo Vision panels
        public const int TROFEO_VENDOR_ID = 0x0416;
        public const int TROFEO_PRODUCT_ID_686 = 0x5302;  // 6.86" - HID transport
        public const int TROFEO_PRODUCT_ID_916 = 0x5408;  // 9.16" - USB bulk transport

        // HID identifier string reported by Trofeo HID panels (init response bytes 20-27)
        // Both 6.86" and 2.4" report "BP21940" — PM byte distinguishes them
        public const string TROFEO_686_HID_IDENTIFIER = "BP21940";

        // PM byte (init response byte[5]) for Trofeo HID panels
        public const byte TROFEO_686_PM_BYTE  = 0x80;  // 128 -> 1280x480 (6.86")
        public const byte TROFEO_240_PM_BYTE  = 0x3A;  //  58 -> 320x240  (2.4")
        public const byte TROFEO_320_PM_BYTE  = 0x20;  //  32 -> 320x320  (big-endian RGB565)
        public const byte TROFEO_1600_PM_BYTE = 0x40;  //  64 -> 1600x720
        public const byte TROFEO_960_PM_BYTE  = 0x0A;  //  10 -> 960x540
        public const byte TROFEO_800_PM_BYTE  = 0x0C;  //  12 -> 800x480

        // HID 0x5302 PM bytes for small panels (identified from rejeb/thermalright-lcd-control)
        public const byte TROFEO_ASSASSIN120_PM_BYTE = 0x24;  //  36 -> 240x240, RGB565
        public const byte TROFEO_AS120_PM_BYTE       = 0x32;  //  50 -> 320x240, RGB565
        public const byte TROFEO_AS120B_PM_BYTE      = 0x33;  //  51 -> 320x240, RGB565
        public const byte TROFEO_BA120_PM_BYTE       = 0x34;  //  52 -> 320x240, RGB565
        public const byte TROFEO_BA120B_PM_BYTE      = 0x35;  //  53 -> 320x240, RGB565
        public const byte TROFEO_FWPRO_PM_BYTE       = 0x64;  // 100 -> 320x240, RGB565
        public const byte TROFEO_ELITE_PM_BYTE       = 0x65;  // 101 -> 320x240, RGB565

        // ChiZhu bulk 87AD:70DB PM byte (at offset 24 of 1024-byte init response)
        public const byte CHIZHU_320X320_PM_BYTE = 0x20;  //  32 -> 320x320, RGB565 big-endian

        // All supported VID/PID pairs for device scanning
        public static readonly (int Vid, int Pid)[] SupportedDevices =
        {
            (THERMALRIGHT_VENDOR_ID, THERMALRIGHT_PRODUCT_ID),
            (TROFEO_VENDOR_ID, TROFEO_PRODUCT_ID_686),
            (TROFEO_VENDOR_ID, TROFEO_PRODUCT_ID_916)
        };

        // Device identifiers returned in init response
        public const string IDENTIFIER_V1 = "SSCRM-V1"; // Grand / Hydro / Peerless Vision 240/360 (480x480)
        public const string IDENTIFIER_V3 = "SSCRM-V3"; // Wonder Vision 360 (2400x1080)
        public const string IDENTIFIER_V4 = "SSCRM-V4"; // TL-M10 Vision (1920x462)

        public static readonly Dictionary<ThermalrightPanelModel, ThermalrightPanelModelInfo> Models = new()
        {
            [ThermalrightPanelModel.PeerlessVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.PeerlessVision360,
                Name = "Grand / Hydro / Peerless Vision 240/360",
                DeviceIdentifier = IDENTIFIER_V1,
                Width = 480,
                Height = 480,
                RenderWidth = 480,
                RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID
            },
            [ThermalrightPanelModel.WonderVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.WonderVision360,
                Name = "Wonder Vision 360",
                DeviceIdentifier = IDENTIFIER_V3,
                Width = 2400,
                Height = 1080,
                RenderWidth = 1600,  // TRCC uses 1600x720
                RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID
            },
            [ThermalrightPanelModel.TLM10Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TLM10Vision,
                Name = "TL-M10 Vision",
                DeviceIdentifier = IDENTIFIER_V4,
                Width = 1920,
                Height = 462,
                RenderWidth = 1920,
                RenderHeight = 462,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID
            },
            [ThermalrightPanelModel.TrofeoVision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision,
                Name = "Trofeo Vision 6.86\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,  // "BP21940" - shared with 2.4", PM byte distinguishes
                Width = 1280,
                Height = 480,
                RenderWidth = 1280,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_686_PM_BYTE
            },
            [ThermalrightPanelModel.FrozenWarframe] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframe,
                Name = "Frozen Warframe 240/360",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,  // "BP21940" - shared with 6.86", PM byte distinguishes
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,  // Uses raw RGB565, not JPEG
                PmByte = TROFEO_240_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision916] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision916,
                Name = "Trofeo Vision 9.16\"",
                DeviceIdentifier = "",  // Identified by unique VID/PID
                Width = 1920,
                Height = 462,
                RenderWidth = 1920,
                RenderHeight = 462,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_916,
                ProtocolType = ThermalrightProtocolType.Trofeo
            },
            [ThermalrightPanelModel.TrofeoVision320] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision320,
                Name = "Trofeo Vision 320x320",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565BigEndian,
                PmByte = TROFEO_320_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision1600x720] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision1600x720,
                Name = "Trofeo Vision 1600x720",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 1600,
                Height = 720,
                RenderWidth = 1600,
                RenderHeight = 720,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_1600_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision960x540] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision960x540,
                Name = "Trofeo Vision 960x540",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 960,
                Height = 540,
                RenderWidth = 960,
                RenderHeight = 540,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_960_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision800x480] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision800x480,
                Name = "Trofeo Vision 800x480",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 800,
                Height = 480,
                RenderWidth = 800,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_800_PM_BYTE
            },
            [ThermalrightPanelModel.AssassinSpirit120Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AssassinSpirit120Vision,
                Name = "Assassin Spirit 120 Vision 1.54\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 240,
                Height = 240,
                RenderWidth = 240,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_ASSASSIN120_PM_BYTE
            },
            [ThermalrightPanelModel.AS120Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AS120Vision,
                Name = "AS120 Vision",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_AS120_PM_BYTE
            },
            [ThermalrightPanelModel.AS120VisionB] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AS120VisionB,
                Name = "AS120 Vision (B)",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_AS120B_PM_BYTE
            },
            [ThermalrightPanelModel.BA120Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.BA120Vision,
                Name = "BA120 Vision",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_BA120_PM_BYTE
            },
            [ThermalrightPanelModel.BA120VisionB] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.BA120VisionB,
                Name = "BA120 Vision (B)",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_BA120B_PM_BYTE
            },
            [ThermalrightPanelModel.FrozenWarframePro] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframePro,
                Name = "Frozen Warframe Pro 2.73\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_FWPRO_PM_BYTE
            },
            [ThermalrightPanelModel.EliteVisionHid] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.EliteVisionHid,
                Name = "Elite Vision",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_ELITE_PM_BYTE
            },
            [ThermalrightPanelModel.ChiZhuVision320x320] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.ChiZhuVision320x320,
                Name = "ChiZhu Vision 320x320",
                DeviceIdentifier = "",  // Identified by VID/PID + PM byte at offset 24 of ChiZhu bulk response
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu,
                PixelFormat = ThermalrightPixelFormat.Rgb565BigEndian
            }
        };

        public static ThermalrightPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            ThermalrightPanelModelInfo? match = null;
            foreach (var model in Models.Values)
            {
                if (model.VendorId == vid && model.ProductId == pid)
                {
                    if (match != null)
                        return null; // Multiple models share this VID/PID — can't determine at scan time
                    match = model;
                }
            }
            return match;
        }

        /// <summary>
        /// Get display resolution from HID init response PM byte (byte[5]).
        /// Based on TRCC protocol: PM → FBL → resolution mapping.
        /// </summary>
        public static (int Width, int Height, string SizeName)? GetResolutionFromPM(byte pm)
        {
            return pm switch
            {
                TROFEO_686_PM_BYTE      => (1280, 480,  "6.86\""),
                65                      => (1920, 462,  "9.16\""),
                TROFEO_240_PM_BYTE      => (320,  240,  "2.4\""),
                TROFEO_320_PM_BYTE      => (320,  320,  "320x320"),
                TROFEO_1600_PM_BYTE     => (1600, 720,  "1600x720"),
                TROFEO_960_PM_BYTE      => (960,  540,  "960x540"),
                TROFEO_800_PM_BYTE      => (800,  480,  "800x480"),
                TROFEO_ASSASSIN120_PM_BYTE => (240, 240, "240x240"),
                TROFEO_AS120_PM_BYTE    => (320,  240,  "320x240"),
                TROFEO_AS120B_PM_BYTE   => (320,  240,  "320x240"),
                TROFEO_BA120_PM_BYTE    => (320,  240,  "320x240"),
                TROFEO_BA120B_PM_BYTE   => (320,  240,  "320x240"),
                TROFEO_FWPRO_PM_BYTE    => (320,  240,  "320x240"),
                TROFEO_ELITE_PM_BYTE    => (320,  240,  "320x240"),
                _ => null
            };
        }

        /// <summary>
        /// Get model info by HID PM byte. Used for Trofeo HID panels that share VID/PID and identifier.
        /// </summary>
        public static ThermalrightPanelModelInfo? GetModelByPM(byte pm)
        {
            foreach (var model in Models.Values)
            {
                if (model.PmByte.HasValue && model.PmByte.Value == pm)
                    return model;
            }
            return null;
        }

        /// <summary>
        /// Get model info by device identifier string (e.g., "SSCRM-V1", "SSCRM-V3", "SSCRM-V4")
        /// </summary>
        public static ThermalrightPanelModelInfo? GetModelByIdentifier(string identifier)
        {
            foreach (var model in Models.Values)
            {
                if (model.DeviceIdentifier == identifier)
                    return model;
            }
            return null;
        }
    }
}
