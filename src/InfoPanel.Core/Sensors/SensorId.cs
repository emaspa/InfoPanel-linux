using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors;

/// <summary>A canonical, ordinal hardware identity. Tokens are UTF-8 percent encoded, never shortened.</summary>
public sealed record SensorId
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex ChannelPattern = new(@"\A(temp|fan|in|curr|power|freq|humidity)(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyPattern = new(@"\A(?:hwmon[0-9]+/(?:temp|fan|in|curr|power|freq|humidity)[0-9]+|thermal/thermal_zone[0-9]+)\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AnchorKinds = new(StringComparer.Ordinal)
        { "nvme-serial", "block-wwid", "block-serial", "gpu-uuid", "usb-serial", "usb-port", "pci", "platform", "type", "i2c", "name" };
    private static readonly HashSet<string> SecondaryKinds = new(StringComparer.Ordinal)
        { "pci", "acpi", "of", "port", "role", "adapter", "meta" };

    private SensorId(string value) => Value = value;
    public string Value { get; }
    public string Source => Value.Split('/')[0];
    public string Channel => Value[(Value.LastIndexOf('/') + 1)..];
    public string Chip => Source == "system" ? Value.Split('/')[1] : Source == "hwmon" ? DecodeToken(Value.Split('/')[2]) : AnchorTokens[0];
    public string Anchor => Identity.Split('~')[0];
    public string? Secondary => Identity.Contains('~') ? Identity.Split('~')[1] : null;
    public string AnchorKind => Anchor.Split('+')[0];
    public ImmutableArray<string> AnchorTokens => Anchor.Split('+').Skip(1).Select(DecodeToken).ToImmutableArray();
    public string Identity => Value.Split('/')[Source == "hwmon" ? 3 : 2];
    public string ChipKey => Source == "system" ? "" : Value[..Value.LastIndexOf('/')];
    public bool HasStrongIdentity => AnchorKind is "nvme-serial" or "usb-serial" or "block-wwid" or "block-serial" or "gpu-uuid";
    public override string ToString() => Value;

    public static bool IsValidChannel(string? channel) => channel != null && ChannelPattern.IsMatch(channel);
    public static bool IsLegacy(string? value) => value != null && LegacyPattern.IsMatch(value);
    public static bool IsMigratedSystemFamily(string? value) => value?.Split('/') is ["system", "disk" or "block" or "gpu" or "amdgpu", ..];
    // Only current-boot aliases for the migrated providers, never arbitrary system paths.
    public static bool IsLegacySystem(string? value) => value != null && Regex.IsMatch(value,
        @"\Asystem/(?:(?:disk|block)/[A-Za-z0-9_-]+|(?:gpu|amdgpu)(?:/[0-9]+)?)/[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant);
    public static string NormalizeChipName(string name)
    {
        name = name.Trim();
        if (Regex.IsMatch(name, @"\Aiwlwifi_[0-9]+\z")) return "iwlwifi";
        if (Regex.IsMatch(name, @"\Ar8169_[0-9]+_[0-9a-fA-F]+:[0-9a-fA-F]+\z")) return "r8169";
        return name;
    }
    public static string NormalizeLabel(string? label) => Regex.Replace(label?.Trim() ?? "", @"\s+", " ").ToUpperInvariant();

    public static string EncodeToken(string value)
    {
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException("Identity tokens cannot be empty.", nameof(value));
        var bytes = Utf8.GetBytes(value);
        var reserved = Regex.IsMatch(value, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            var safe = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'_' or (byte)'-';
            if ((reserved && i == 0) || (b == '.' && (value is "." or ".." || i == bytes.Length - 1))) safe = false;
            result.Append(safe ? ((char)b).ToString() : $"%{b:X2}");
        }
        return result.ToString();
    }

    public static string DecodeToken(string token)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (c == '%')
            {
                if (i + 2 >= token.Length || !byte.TryParse(token.AsSpan(i + 1, 2), global::System.Globalization.NumberStyles.HexNumber,
                    global::System.Globalization.CultureInfo.InvariantCulture, out var b)) throw new FormatException("Invalid percent escape.");
                bytes.Add(b);
                i += 2;
            }
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-') bytes.Add((byte)c);
            else throw new FormatException("Unescaped identity token.");
        }
        try { return Utf8.GetString(bytes.ToArray()); }
        catch (DecoderFallbackException ex) { throw new FormatException("Invalid UTF-8 identity token.", ex); }
    }

    public static string Component(string kind, params string[] tokens) => kind + "+" + string.Join('+', tokens.Select(EncodeToken));
    public static SensorId Hwmon(string chip, string anchor, string channel, string? secondary = null) =>
        Parse($"hwmon/v1/{EncodeToken(NormalizeChipName(chip))}/{anchor}{(secondary == null ? "" : "~" + secondary)}/{channel}");
    public static SensorId Thermal(string anchor, string? secondary = null) =>
        Parse($"thermal/v1/{anchor}{(secondary == null ? "" : "~" + secondary)}/temp");
    public static SensorId System(string family, string anchor, string metric, string? secondary = null) =>
        Parse($"system/{family}/{anchor}{(secondary == null ? "" : "~" + secondary)}/{metric}");
    public static SensorId Parse(string value) => TryParse(value, out var id) ? id! : throw new FormatException($"Invalid stable sensor id: {value}");

    public static bool TryParse(string? value, out SensorId? id)
    {
        id = null;
        if (value == null) return false;
        var parts = value.Split('/');
        var hwmon = parts.Length == 5 && parts[0] == "hwmon";
        var thermal = parts.Length == 4 && parts[0] == "thermal";
        var system = parts.Length == 4 && IsMigratedSystemFamily(value);
        if (!system && ((!hwmon && !thermal) || parts[1] != "v1")) return false;
        if (system ? !Regex.IsMatch(parts[^1], @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)
            : hwmon ? !IsValidChannel(parts[^1]) : parts[^1] != "temp") return false;
        try
        {
            if (hwmon && !ValidToken(parts[2])) return false;
            var identity = parts[hwmon ? 3 : 2].Split('~');
            if (identity.Length > 2 || !ValidComponent(identity[0], AnchorKinds)) return false;
            if (identity.Length == 2 && !ValidComponent(identity[1], SecondaryKinds)) return false;
            id = new SensorId(value);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
    }

    private static bool ValidToken(string token) => token.Length > 0 && EncodeToken(DecodeToken(token)) == token;
    private static bool ValidComponent(string part, HashSet<string> kinds)
    {
        var tokens = part.Split('+');
        return tokens.Length >= 2 && kinds.Contains(tokens[0]) && tokens.Skip(1).All(ValidToken);
    }
}
