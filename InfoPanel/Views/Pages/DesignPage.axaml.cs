using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class DesignPage : UserControl
    {
        public DesignPage()
        {
            InitializeComponent();
            DataContext = new DesignPageViewModel();
        }
    }
}
