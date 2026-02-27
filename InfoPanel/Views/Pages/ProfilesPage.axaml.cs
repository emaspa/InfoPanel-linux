using Avalonia.Controls;
using InfoPanel.ViewModels;
using SkiaSharp;
using System.Linq;

namespace InfoPanel.Views.Pages
{
    public partial class ProfilesPage : UserControl
    {
        public ProfilesPage()
        {
            InitializeComponent();
            DataContext = new ProfilesPageViewModel();
            FontComboBox.ItemsSource = SKFontManager.Default.GetFontFamilies().OrderBy(f => f).ToList();
        }
    }
}
