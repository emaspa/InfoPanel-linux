using InfoPanel.Persistence;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Loader;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InfoPanel.Monitors
{
    /// <summary>
    /// Host-managed persistence for IPluginConfigurable plugins (ports the Windows
    /// build's HostService config handling to the in-proc host). Values live in
    /// {data dir}/plugins/{plugin id}.config.json as a flat Key→Value map; they are
    /// applied via ApplyConfig after plugin initialization and saved on every change
    /// from the config UI. Plugins that still manage their own ConfigFilePath are
    /// left alone.
    /// </summary>
    public static class PluginConfigStore
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(PluginConfigStore));
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string GetConfigFilePath(PluginWrapper wrapper)
        {
            var dir = Path.Combine(ConfigPersistence.BaseFolder, "plugins");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"{wrapper.Id}.config.json");
        }

        /// <summary>True when the host (not the plugin itself) persists this plugin's config.</summary>
        public static bool IsHostManaged(PluginWrapper wrapper) =>
#pragma warning disable CS0618 // legacy plugins still expose their own config file
            wrapper.Plugin is IPluginConfigurable && wrapper.Plugin.ConfigFilePath == null;
#pragma warning restore CS0618

        /// <summary>Applies stored values (if any) and creates the sidecar with defaults when missing.</summary>
        public static void LoadAndApply(PluginWrapper wrapper)
        {
            if (!IsHostManaged(wrapper) || wrapper.Plugin is not IPluginConfigurable configurable)
            {
                return;
            }

            var configPath = GetConfigFilePath(wrapper);
            if (File.Exists(configPath))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(configPath));
                    if (stored != null)
                    {
                        var props = configurable.ConfigProperties;
                        foreach (var kvp in stored)
                        {
                            var prop = props.FirstOrDefault(p => p.Key == kvp.Key);
                            if (prop == null) continue;

                            try
                            {
                                configurable.ApplyConfig(kvp.Key, CoerceJsonElement(kvp.Value, prop.Type));
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning(ex, "Failed to apply stored config key {Key} on plugin {PluginId}", kvp.Key, wrapper.Id);
                            }
                        }

                        Logger.Information("Loaded stored config for plugin {PluginId} from {Path}", wrapper.Id, configPath);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to load config file for plugin {PluginId}", wrapper.Id);
                }
            }
            else
            {
                Save(wrapper, configurable);
            }
        }

        /// <summary>
        /// Coerces a UI-provided value to the property's CLR type, applies it to the
        /// plugin, and persists the full config map. Returns false when the plugin
        /// rejected the value.
        /// </summary>
        public static bool Apply(PluginWrapper wrapper, string key, object? value)
        {
            if (wrapper.Plugin is not IPluginConfigurable configurable)
            {
                return false;
            }

            var prop = configurable.ConfigProperties.FirstOrDefault(p => p.Key == key);
            var nativeValue = prop != null ? CoerceValue(value, prop.Type) : value;

            try
            {
                configurable.ApplyConfig(key, nativeValue);
                if (IsHostManaged(wrapper))
                {
                    Save(wrapper, configurable);
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error applying config key {Key} on plugin {PluginId}", key, wrapper.Id);
                return false;
            }
        }

        private static void Save(PluginWrapper wrapper, IPluginConfigurable configurable)
        {
            try
            {
                var values = configurable.ConfigProperties.ToDictionary(p => p.Key, p => p.Value);
                File.WriteAllText(GetConfigFilePath(wrapper), JsonSerializer.Serialize(values, JsonOptions));
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to save config for plugin {PluginId}", wrapper.Id);
            }
        }

        private static object? CoerceJsonElement(JsonElement element, PluginConfigType type)
        {
            try
            {
                return type switch
                {
                    PluginConfigType.Boolean => element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False
                        ? element.GetBoolean()
                        : bool.TryParse(element.ToString(), out var b) && b,
                    PluginConfigType.Integer => element.ValueKind == JsonValueKind.Number
                        ? (int)Math.Round(element.GetDouble())
                        : int.TryParse(element.ToString(), out var i) ? i : 0,
                    PluginConfigType.Double => element.ValueKind == JsonValueKind.Number
                        ? element.GetDouble()
                        : double.TryParse(element.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0,
                    _ => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
                };
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to coerce stored value {Value} to {Type}", element, type);
                return null;
            }
        }

        private static object? CoerceValue(object? value, PluginConfigType type)
        {
            try
            {
                return type switch
                {
                    PluginConfigType.Boolean => value is bool ? value : Convert.ToBoolean(value),
                    PluginConfigType.Integer => value is int ? value : Convert.ToInt32(value),
                    PluginConfigType.Double => value is double ? value : Convert.ToDouble(value),
                    PluginConfigType.String => value?.ToString(),
                    PluginConfigType.Choice => value?.ToString(),
                    _ => value,
                };
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to coerce value {Value} to {Type}", value, type);
                return type switch
                {
                    PluginConfigType.Boolean => false,
                    PluginConfigType.Integer => 0,
                    PluginConfigType.Double => 0.0,
                    _ => value?.ToString(),
                };
            }
        }
    }
}
