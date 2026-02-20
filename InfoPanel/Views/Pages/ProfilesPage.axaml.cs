using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage()
        {
            InitializeComponent();
            DataContext = new ProfilesPageViewModel();
        }
    }
}
