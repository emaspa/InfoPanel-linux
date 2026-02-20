using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class UpdatesPage : UserControl
    {
        public UpdatesPage()
        {
            InitializeComponent();
            DataContext = new UpdatesPageViewModel();
        }
    }
}
