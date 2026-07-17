using Serilog;
using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    /// <summary>
    /// Checks GitHub releases for a newer version. No accounts or telemetry:
    /// one anonymous request to the public releases API, carrying the release
    /// notes back so users can see what changed before downloading.
    /// </summary>
    public static class UpdateChecker
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(UpdateChecker));

        // Not /releases/latest: that endpoint hides prereleases, and alpha builds
        // are published as prereleases. The list is newest-first.
        private const string ReleasesApi = "https://api.github.com/repos/emaspa/InfoPanel-linux/releases?per_page=10";

        public sealed record UpdateInfo(Version Version, string Title, string Url, string Notes);

        /// <summary>Set when a startup or manual check found a newer release.</summary>
        public static UpdateInfo? Available { get; private set; }

        /// <summary>Raised on the UI thread when <see cref="Available"/> changes.</summary>
        public static event Action? AvailableChanged;

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

        /// <summary>
        /// Silent startup check; failures only log. When an update is found, a
        /// desktop notification points the user at the About page, which shows
        /// the release notes and download link.
        /// </summary>
        public static async Task RunStartupCheckAsync()
        {
            try
            {
                // Let startup I/O settle first
                await Task.Delay(TimeSpan.FromSeconds(10));

                if (await CheckAsync() is { } update)
                {
                    Notify(update);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Startup update check failed");
            }
        }

        private static void Notify(UpdateInfo update)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notify-send")
                {
                    ArgumentList =
                    {
                        "--app-name=InfoPanel",
                        "--icon=infopanel",
                        $"{update.Title} is available",
                        "Open About in InfoPanel to see what changed and download the update.",
                    },
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not send update notification");
            }
        }

        /// <summary>
        /// Queries the latest release. Returns the update when newer than the
        /// running version, null when up to date. Throws on network errors so
        /// manual checks can show a message.
        /// </summary>
        public static async Task<UpdateInfo?> CheckAsync(Version? currentOverride = null)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("InfoPanel-Linux");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await client.GetStringAsync(ReleasesApi);
            using var doc = JsonDocument.Parse(json);

            JsonElement? newest = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                {
                    continue;
                }

                newest = release;
                break;
            }

            if (newest is not { } root)
            {
                return null;
            }

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                return null;
            }

            var current = currentOverride ?? CurrentVersion;
            // Assembly versions are 4-part (0.0.2.0); compare at the tag's precision
            var comparable = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
            if (latest <= comparable)
            {
                Logger.Debug("Update check: {Latest} is not newer than {Current}", latest, comparable);
                return null;
            }

            var info = new UpdateInfo(
                latest,
                root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tag : tag,
                root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "",
                root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "");

            Logger.Information("Update available: {Title} ({Version})", info.Title, info.Version);
            Available = info;
            Utils.UiThread.Post(() => AvailableChanged?.Invoke());
            return info;
        }
    }
}
