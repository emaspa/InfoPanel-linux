using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            DataContext = new SettingsPageViewModel();
        }
    }
}
