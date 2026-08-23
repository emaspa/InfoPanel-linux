using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using SkiaSharp;

namespace InfoPanel.ViewModels
{
    /// <summary>Dashboard card state for one profile: live thumbnail + activation.</summary>
    public partial class ProfileCardViewModel(Profile profile, AppHost host) : ObservableObject
    {
        public Profile Profile { get; } = profile;

        [ObservableProperty]
        private Bitmap? _thumbnail;

        /// <summary>Expander state lives here so card visual rebuilds can't collapse it.</summary>
        [ObservableProperty]
        private bool _isSettingsExpanded;

        /// <summary>Font list including the profile's current font (embedded fonts like Inter aren't in SKFontManager).</summary>
        public IReadOnlyList<string> FontChoices { get; } =
            Utils.UiCatalog.FontFamilies.Contains(profile.Font)
                ? Utils.UiCatalog.FontFamilies
                : [profile.Font, .. Utils.UiCatalog.FontFamilies];

        /// <summary>Guards against ComboBox writing null when its popup/state churns.</summary>
        public string? FontSelection
        {
            get => Profile.Font;
            set
            {
                if (!string.IsNullOrEmpty(value) && value != Profile.Font)
                {
                    Profile.Font = value;
                    OnPropertyChanged();
                }
            }
        }

        private const string NoDisplay = "Not assigned";

        private static List<Utils.MonitorInfo> Monitors() =>
            Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } main
                ? Utils.ScreenHelper.GetAllMonitors(main)
                : [];

        /// <summary>Overlay display choices for the card's settings expander.</summary>
        public IReadOnlyList<string> DisplayChoices =>
            [NoDisplay, .. Monitors().Select(m => m.Label)];

        /// <summary>Guards against ComboBox writing null when its popup/state churns.</summary>
        public string? DisplaySelection
        {
            get
            {
                var monitors = Monitors();
                var assigned = Profile.TargetWindow is { } target
                    ? Utils.ScreenHelper.MatchTargetWindow(target, monitors, strict: false)
                    : null;
                return assigned?.Label ?? NoDisplay;
            }
            set
            {
                if (string.IsNullOrEmpty(value) || value == DisplaySelection) return;

                if (value == NoDisplay)
                {
                    Profile.TargetWindow = null;
                }
                else
                {
                    var monitor = Monitors().FirstOrDefault(m => m.Label == value);
                    if (monitor == null) return;
                    Utils.ScreenHelper.AssignTargetWindow(Profile, monitor);
                }

                host.SaveProfiles();
                OnPropertyChanged();
            }
        }

        public string Subtitle
        {
            get
            {
                // Friendly device names, matching what the Devices page shows.
                var outputs = new List<string>();
                if (Profile.Active) outputs.Add("overlay");
                outputs.AddRange(host.Settings.ThermalrightPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model.ToString()));
                outputs.AddRange(host.Settings.BeadaPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => System.Enum.TryParse<BeadaPanel.BeadaPanelModel>(d.Model, out var beadaModel)
                        && BeadaPanel.BeadaPanelModelDatabase.Models.TryGetValue(beadaModel, out var bi)
                        ? bi.Name : d.Model ?? "BeadaPanel"));
                outputs.AddRange(host.Settings.TuringPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model ?? "Turing"));
                outputs.AddRange(host.Settings.ThermaltakePanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model.ToString()));
                outputs.AddRange(host.Settings.JlPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model.ToString()));
                outputs.AddRange(host.Settings.VmaxPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model.ToString()));
                outputs.AddRange(host.Settings.JonsboPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model.ToString()));
                outputs.AddRange(host.Settings.LianLiPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.ModelInfo?.Name ?? d.Model.ToString()));

                var where = outputs.Count > 0 ? $" · {string.Join(", ", outputs)}" : "";
                return $"{Profile.Width}×{Profile.Height}{where}";
            }
        }

        public bool IsActive
        {
            get => Profile.Active;
            set
            {
                if (Profile.Active == value) return;

                Profile.Active = value;
                host.SaveProfiles();

                if (value)
                {
                    DisplayWindowManager.Instance.ShowDisplayWindow(Profile);
                }
                else
                {
                    DisplayWindowManager.Instance.CloseDisplayWindow(Profile.Guid);
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Subtitle));
            }
        }

        /// <summary>Renders a fresh thumbnail (~quarter scale) off the live profile state.</summary>
        public void RefreshThumbnail()
        {
            try
            {
                using var bitmap = PanelRenderer.RenderSK(Profile, preview: true);
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                using var stream = new MemoryStream(data.ToArray());
                Thumbnail = new Bitmap(stream);
            }
            catch
            {
                // rendering may transiently fail during profile edits; keep the old thumbnail
            }
        }
    }
}
