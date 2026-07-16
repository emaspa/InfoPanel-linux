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

        public string Subtitle
        {
            get
            {
                var outputs = new List<string>();
                if (Profile.Active) outputs.Add("overlay");
                outputs.AddRange(host.Settings.ThermalrightPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.Model.ToString()));
                outputs.AddRange(host.Settings.BeadaPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.Model ?? "BeadaPanel"));
                outputs.AddRange(host.Settings.TuringPanelDevices
                    .Where(d => d.Enabled && d.ProfileGuid == Profile.Guid)
                    .Select(d => d.Model ?? "Turing"));

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
