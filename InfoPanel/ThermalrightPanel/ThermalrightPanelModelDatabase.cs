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
        public const byte TROFEO_686_PM_BYTE = 128;   // 0x80 -> 1280x480 (6.86")
        public const byte TROFEO_240_PM_BYTE = 0x3A;  // 58  -> 320x240  (2.4")

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
            [ThermalrightPanelModel.TrofeoVision240] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision240,
                Name = "Trofeo Vision 2.4\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,  // "BP21940" - shared with 6.86", PM byte distinguishes
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
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
            }
        };

        public static ThermalrightPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            foreach (var model in Models.Values)
            {
                if (model.VendorId == vid && model.ProductId == pid)
                    return model;
            }
            return null;
        }

        /// <summary>
        /// Get display resolution from HID init response PM byte (byte[5]).
        /// Based on TRCC protocol: PM → FBL → resolution mapping.
        /// </summary>
        public static (int Width, int Height, string SizeName)? GetResolutionFromPM(byte pm)
        {
            return pm switch
            {
                TROFEO_686_PM_BYTE => (1280, 480, "6.86\""),
                65 => (1920, 462, "9.16\""),
                TROFEO_240_PM_BYTE => (320, 240, "2.4\""),
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
