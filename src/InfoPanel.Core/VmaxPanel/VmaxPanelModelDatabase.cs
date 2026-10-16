using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.VmaxPanel
{
    public static class VmaxPanelModelDatabase
    {
        public const int VMAX_VENDOR_ID = 0x345F;
        public const int VMAX_PRODUCT_ID_46INCH = 0x9132;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (VMAX_VENDOR_ID, VMAX_PRODUCT_ID_46INCH),
            (0x374A, 0xA021),
        ];

        public static readonly Dictionary<VmaxPanelModel, VmaxPanelModelInfo> Models = new()
        {
            [VmaxPanelModel.Vmax46Inch] = new VmaxPanelModelInfo
            {
                Model = VmaxPanelModel.Vmax46Inch,
                Name = "VMAX 4.6\" LCD",
                Width = 320,
                Height = 960,
                VendorId = VMAX_VENDOR_ID,
                ProductId = VMAX_PRODUCT_ID_46INCH,
            },
            [VmaxPanelModel.ValkyrieV360] = new VmaxPanelModelInfo
            {
                Model = VmaxPanelModel.ValkyrieV360,
                Name = "Valkyrie V360 LCD",
                Width = 320,
                Height = 240,
                VendorId = VMAX_VENDOR_ID,
                ProductId = VMAX_PRODUCT_ID_46INCH,
            },
            [VmaxPanelModel.ValkyrieMc360] = new VmaxPanelModelInfo
            {
                Model = VmaxPanelModel.ValkyrieMc360,
                Name = "Valkyrie MC360 LCD",
                Width = 640, Height = 480, VendorId = 0x374A, ProductId = 0xA021,
                UsesBgr565 = true, Experimental = true,
            },
        };

        // VID/PID identifies the shared MS9132 bridge, not the attached panel.
        public static VmaxPanelModelInfo? GetModelByPanelSize(int width, int height) =>
            Models.Values.FirstOrDefault(m => m.VendorId == VMAX_VENDOR_ID && m.Width == width && m.Height == height);

        public static VmaxPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
