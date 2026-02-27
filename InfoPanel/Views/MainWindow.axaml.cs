using Avalonia;
using Avalonia.Controls;
using InfoPanel.ViewModels;
using Serilog;

namespace InfoPanel.Views
{
    public partial class MainWindow : Window
    {
        private static readonly ILogger Logger = Log.ForContext<MainWindow>();

        public MainWindowViewModel ViewModel { get; }

        private bool _suppressTopSelection;
        private bool _suppressFooterSelection;
        private bool _isShuttingDown;

        public MainWindow()
        {
            ViewModel = new MainWindowViewModel();
            DataContext = ViewModel;
            InitializeComponent();

            Width = ConfigModel.Instance.Settings.UiWidth;
            Height = ConfigModel.Instance.Settings.UiHeight;

            // Track property changes for persistence and minimize-to-tray
            PropertyChanged += OnWindowPropertyChanged;

            // Select first item in TopNav on load
            Loaded += (_, _) =>
            {
                var topNav = this.FindControl<ListBox>("TopNav");
                if (topNav != null && topNav.ItemCount > 0)
                    topNav.SelectedIndex = 0;
            };
        }

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == BoundsProperty && Bounds.Width > 0 && Bounds.Height > 0)
            {
                ConfigModel.Instance.Settings.UiWidth = (float)Bounds.Width;
                ConfigModel.Instance.Settings.UiHeight = (float)Bounds.Height;
            }
            else if (e.Property == WindowStateProperty)
            {
                if (WindowState == WindowState.Minimized
                    && ConfigModel.Instance.Settings.MinimizeToTray)
                {
                    Hide();
                }
            }
        }

        private void OnTopSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressTopSelection) return;

            if (sender is ListBox lb && lb.SelectedItem is NavigationItem item)
            {
                _suppressFooterSelection = true;
                var footerNav = this.FindControl<ListBox>("FooterNav");
                if (footerNav != null) footerNav.SelectedIndex = -1;
                _suppressFooterSelection = false;

                ViewModel.Navigate(item.Page);
            }
        }

        private void OnFooterSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressFooterSelection) return;

            if (sender is ListBox lb && lb.SelectedItem is NavigationItem item)
            {
                _suppressTopSelection = true;
                var topNav = this.FindControl<ListBox>("TopNav");
                if (topNav != null) topNav.SelectedIndex = -1;
                _suppressTopSelection = false;

                ViewModel.Navigate(item.Page);
            }
        }

        public void RestoreWindow()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
            InvalidateVisual();
        }

        public void NavigateToPage(NavigationPage page)
        {
            ViewModel.Navigate(page);
        }

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (_isShuttingDown)
            {
                // Allow the close to proceed during shutdown
                return;
            }

            // Hide to tray instead of exiting when MinimizeToTray is enabled
            if (ConfigModel.Instance.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            e.Cancel = true;
            _isShuttingDown = true;
            base.OnClosing(e);

            Logger.Information("MainWindow closing, initiating clean shutdown");
            await App.CleanShutDown();
        }
    }
}
