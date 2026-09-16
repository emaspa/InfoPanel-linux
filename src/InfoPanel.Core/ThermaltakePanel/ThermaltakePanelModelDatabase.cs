using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.ThermaltakePanel
{
    public static class ThermaltakePanelModelDatabase
    {
        public const int THERMALTAKE_VENDOR_ID = 0x264A;
        public const int THERMALTAKE_PRODUCT_ID_6INCH = 0x2347;

        public const int ASROCK_VENDOR_ID = 0x26CE;
        public const int ASROCK_PRODUCT_ID_PG360 = 0x0A10;
        public const int ASROCK_PRODUCT_ID_STEEL_LEGEND_360 = 0x0A11;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (THERMALTAKE_VENDOR_ID, THERMALTAKE_PRODUCT_ID_6INCH),
            (ASROCK_VENDOR_ID, ASROCK_PRODUCT_ID_PG360),
            (ASROCK_VENDOR_ID, ASROCK_PRODUCT_ID_STEEL_LEGEND_360),
        ];

        public static readonly Dictionary<ThermaltakePanelModel, ThermaltakePanelModelInfo> Models = new()
        {
            [ThermaltakePanelModel.ToughLiquid6Inch] = new ThermaltakePanelModelInfo
            {
                Model = ThermaltakePanelModel.ToughLiquid6Inch,
                Name = "Thermaltake 6\" LCD",
                Width = 1480,
                Height = 720,
                VendorId = THERMALTAKE_VENDOR_ID,
                ProductId = THERMALTAKE_PRODUCT_ID_6INCH,
            },
            [ThermaltakePanelModel.AsrockPhantomGaming360LCD] = new ThermaltakePanelModelInfo
            {
                Model = ThermaltakePanelModel.AsrockPhantomGaming360LCD,
                Name = "ASRock Phantom Gaming 360 LCD",
                Width = 480,
                Height = 480,
                VendorId = ASROCK_VENDOR_ID,
                ProductId = ASROCK_PRODUCT_ID_PG360,
            },
            [ThermaltakePanelModel.AsrockSteelLegend360LCD] = new ThermaltakePanelModelInfo
            {
                Model = ThermaltakePanelModel.AsrockSteelLegend360LCD,
                Name = "ASRock Steel Legend 360 LCD",
                Width = 480,
                Height = 480,
                VendorId = ASROCK_VENDOR_ID,
                ProductId = ASROCK_PRODUCT_ID_STEEL_LEGEND_360,
            },
        };

        public static ThermaltakePanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
