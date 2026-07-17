using InfoPanel.Plugins.Graphics;
using System;

namespace InfoPanel.Models
{
    /// <summary>
    /// Plugin image resolution seam, mirroring SensorReader. The sensor host registers
    /// a resolver over its live per-plugin image writers at startup; LockedImage resolves
    /// lazily on every access so plugin reloads and buffer resizes need no invalidation.
    /// </summary>
    public static class PluginImageSource
    {
        public const string Scheme = "plugin-image://";

        private static Func<string, string, InProcPluginImageWriter?>? _resolver;

        public static void Configure(Func<string, string, InProcPluginImageWriter?> resolver)
        {
            _resolver = resolver;
        }

        public static InProcPluginImageWriter? Resolve(string pluginId, string imageId)
        {
            return _resolver?.Invoke(pluginId, imageId);
        }

        /// <summary>Parses "plugin-image://{pluginId}/{imageId}".</summary>
        public static bool TryParseUri(string uri, out string pluginId, out string imageId)
        {
            pluginId = "";
            imageId = "";

            if (!uri.StartsWith(Scheme, StringComparison.Ordinal))
            {
                return false;
            }

            var path = uri[Scheme.Length..];
            var slashIndex = path.IndexOf('/');
            if (slashIndex <= 0 || slashIndex == path.Length - 1)
            {
                return false;
            }

            pluginId = path[..slashIndex];
            imageId = path[(slashIndex + 1)..];
            return true;
        }
    }
}
