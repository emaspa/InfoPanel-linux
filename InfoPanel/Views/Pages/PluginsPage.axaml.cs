using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class PluginsPage : UserControl
    {
        public PluginsPage()
        {
            InitializeComponent();
            DataContext = new PluginsPageViewModel();
        }
    }
}
