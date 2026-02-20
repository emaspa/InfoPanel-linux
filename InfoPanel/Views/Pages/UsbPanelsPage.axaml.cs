using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class UsbPanelsPage : UserControl
    {
        public UsbPanelsPage()
        {
            InitializeComponent();
            DataContext = new UsbPanelsPageViewModel();
        }
    }
}
