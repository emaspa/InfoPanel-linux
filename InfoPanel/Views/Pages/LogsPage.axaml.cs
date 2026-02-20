using Avalonia.Controls;
using InfoPanel.ViewModels;

namespace InfoPanel.Views.Pages
{
    public partial class LogsPage : UserControl
    {
        public LogsPage()
        {
            InitializeComponent();
            DataContext = new LogsPageViewModel();
        }
    }
}
