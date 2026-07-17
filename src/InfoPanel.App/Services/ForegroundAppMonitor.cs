using InfoPanel.Models;
using InfoPanel.Platform;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Services
{
    /// <summary>
    /// Polls the foreground application and shows/hides profile overlays based on
    /// per-profile trigger rules (Profile.TriggerProcessNames). Port of the Windows
    /// build's ForegroundAppMonitor; detection runs through the platform seam
    /// (X11 _NET_ACTIVE_WINDOW on Linux, so Proton/Wine games report their
    /// Windows executable names).
    /// </summary>
    public sealed class ForegroundAppMonitor : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ForegroundAppMonitor>();

        private const int PollIntervalMs = 800;

        private readonly AppHost _host;
        private readonly CancellationTokenSource _cts = new();
        private bool _wasEnabled;

        public ForegroundAppMonitor(AppHost host)
        {
            _host = host;
        }

        public void Start()
        {
            var service = PlatformServices.ForegroundApp;
            if (service is not { IsAvailable: true })
            {
                Logger.Information("Foreground app detection unavailable; program-specific profiles inactive");
                return;
            }

            if (service.Limitation is { } limitation)
            {
                Logger.Information("Program-specific profiles: {Limitation}", limitation);
            }

            _ = Task.Run(() => RunAsync(service, _cts.Token));
        }

        private async Task RunAsync(IForegroundAppService service, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var profiles = _host.Profiles.ToList();

                    if (!_host.Settings.ProgramSpecificPanelsEnabled)
                    {
                        // On disable, hand visibility back to the Active toggles once
                        if (_wasEnabled)
                        {
                            _wasEnabled = false;
                            ReconcileVisibilityToActiveOnly(profiles);
                        }

                        await Task.Delay(PollIntervalMs, token);
                        continue;
                    }

                    _wasEnabled = true;

                    var foregroundName = service.GetForegroundProcessName();
                    var matching = GetMatchingTriggerProfiles(profiles, foregroundName);
                    ApplyVisibility(profiles, matching, _host.Settings.HideOtherProfilesWhenProgramSpecificShown);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "ForegroundAppMonitor poll error");
                }

                await Task.Delay(PollIntervalMs, token);
            }
        }

        private static List<Profile> GetMatchingTriggerProfiles(List<Profile> profiles, string? foregroundName)
        {
            if (string.IsNullOrWhiteSpace(foregroundName))
            {
                return [];
            }

            var normalizedForeground = NormalizeProcessName(foregroundName);
            var list = new List<Profile>();

            foreach (var profile in profiles)
            {
                if (!profile.Active || string.IsNullOrWhiteSpace(profile.TriggerProcessNames))
                {
                    continue;
                }

                var names = profile.TriggerProcessNames
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (names.Any(name => string.Equals(NormalizeProcessName(name), normalizedForeground, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(profile);
                }
            }

            return list;
        }

        private static string NormalizeProcessName(string name)
        {
            var s = name.Trim();
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
        }

        private static void ApplyVisibility(List<Profile> profiles, List<Profile> matchingTriggerProfiles, bool hideOthers)
        {
            bool hasMatchingTrigger = matchingTriggerProfiles.Count > 0;

            foreach (var profile in profiles)
            {
                bool shouldBeVisible;
                if (hasMatchingTrigger && hideOthers)
                {
                    shouldBeVisible = profile.Active && matchingTriggerProfiles.Contains(profile);
                }
                else if (hasMatchingTrigger)
                {
                    bool isAlwaysOn = string.IsNullOrWhiteSpace(profile.TriggerProcessNames);
                    shouldBeVisible = profile.Active && (matchingTriggerProfiles.Contains(profile) || isAlwaysOn);
                }
                else
                {
                    // No matching trigger: show only always-on (no trigger) Active profiles
                    shouldBeVisible = profile.Active && string.IsNullOrWhiteSpace(profile.TriggerProcessNames);
                }

                SetWindowVisible(profile, shouldBeVisible);
            }
        }

        /// <summary>When program-specific profiles are turned off, visibility follows the Active toggles again.</summary>
        private static void ReconcileVisibilityToActiveOnly(List<Profile> profiles)
        {
            foreach (var profile in profiles)
            {
                SetWindowVisible(profile, profile.Active);
            }
        }

        private static void SetWindowVisible(Profile profile, bool visible)
        {
            if (visible == DisplayWindowManager.Instance.IsWindowOpen(profile.Guid))
            {
                return;
            }

            if (visible)
            {
                DisplayWindowManager.Instance.ShowDisplayWindow(profile);
            }
            else
            {
                DisplayWindowManager.Instance.CloseDisplayWindow(profile.Guid);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
