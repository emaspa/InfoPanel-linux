using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Models;

namespace InfoPanel.Views.Pages
{
    public partial class DashboardPage : UserControl
    {
        public DashboardPage()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                if (Avalonia.Application.Current is App app)
                {
                    ProfileList.ItemsSource = app.Host.Profiles;
                }
            };
        }

        private void ShowOverlay_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is Profile profile)
            {
                DisplayWindowManager.Instance.ShowDisplayWindow(profile);
            }
        }

        private void HideOverlay_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is Profile profile)
            {
                DisplayWindowManager.Instance.CloseDisplayWindow(profile.Guid);
            }
        }
    }
}
