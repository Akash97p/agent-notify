using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

public static partial class ProviderFormBuilder
{
    internal static bool IsMqttHost(string value)
    {
        if (value.Length is < 1 or > 253 || value.EndsWith('.') || value.Any(c => c > 127)) return false;
        if (System.Net.IPAddress.TryParse(value, out _)) return true;
        return Uri.CheckHostName(value) == UriHostNameType.Dns && value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static void ValidateMqttTopic(string value)
    {
        if (value.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(value) > 512 ||
            value[0] is '/' or '$' || value[^1] == '/' || value.Contains("//", StringComparison.Ordinal) ||
            value.Any(c => c is '\0' or '+' or '#' || char.IsControl(c)))
            throw new ArgumentException("MQTT topic must be a fixed non-system topic without wildcards, empty levels, or controls.");
    }

    private static bool IsCertificateThumbprint(string value)
    {
        var normalized = NormalizeCertificateThumbprint(value);
        return normalized.Length is 40 or 64 && normalized.All(Uri.IsHexDigit);
    }

    private static string NormalizeCertificateThumbprint(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());

    private static bool IsTwilioSid(string value, string prefix) =>
        value.Length == 34 && value.StartsWith(prefix, StringComparison.Ordinal) && value[2..].All(Uri.IsHexDigit);

    private static bool IsE164(string value) =>
        value.Length is >= 9 and <= 16 && value[0] == '+' && value[1] is >= '1' and <= '9' && value[2..].All(char.IsAsciiDigit);

    private static bool IsPrintableSecret(string value) =>
        value.Length is >= 16 and <= 256 && value.All(c => c is >= '!' and <= '~');

    private static bool IsMetaGraphVersion(string value)
    {
        if (!value.StartsWith('v') || !value.EndsWith(".0", StringComparison.Ordinal)) return false;
        return int.TryParse(value.AsSpan(1, value.Length - 3), out var major) && major is >= 1 and <= 99;
    }

    private static bool IsLanguageCode(string value)
    {
        var parts = value.Split('_');
        return parts.Length is 1 or 2 && parts[0].Length is 2 or 3 &&
               parts[0].All(c => c is >= 'a' and <= 'z') &&
               (parts.Length == 1 || parts[1].Length == 2 && parts[1].All(c => c is >= 'A' and <= 'Z'));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json.Options);

    /// <summary>Submitted values with catalog defaults applied, plus the secret changes being collected.</summary>
    private sealed class Form
    {
        private readonly ProviderKindDescriptor _descriptor;
        private readonly ProviderFormInput _input;
        private readonly ProviderProfile? _existing;
        private readonly Dictionary<string, string> _changes = new(StringComparer.Ordinal);
        private readonly List<string> _removals = [];

        public Form(ProviderKindDescriptor descriptor, ProviderFormInput input, ProviderProfile? existing)
        {
            _descriptor = descriptor;
            _input = input;
            _existing = existing;
        }

        public RelayPairingOutcome? Pairing => _input.Pairing;

        public bool AllowPrivate => Bool("allow_private_network");

        public string Text(string key)
        {
            if (_input.Values.TryGetValue(key, out var value))
                return value ?? "";
            return _descriptor.Fields.FirstOrDefault(field => field.Key == key)?.Default ?? "";
        }

        public bool Bool(string key) =>
            Text(key).Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";

        /// <summary>The submitted value when it is one of <paramref name="allowed"/>, otherwise the field default.</summary>
        public string Choice(string key, IReadOnlyList<string> allowed)
        {
            var value = Text(key).Trim();
            if (allowed.Contains(value, StringComparer.Ordinal)) return value;
            var fallback = _descriptor.Fields.FirstOrDefault(field => field.Key == key)?.Default;
            if (fallback is not null && allowed.Contains(fallback, StringComparer.Ordinal) && value.Length == 0) return fallback;
            throw new ArgumentException($"Select a valid value for {Label(key)}.");
        }

        public string RawSecret(string key) =>
            _input.Secrets.TryGetValue(key, out var value) ? value ?? "" : "";

        public string TrimmedSecret(string key) => RawSecret(key).Trim();

        public bool HasStored(string name) =>
            _existing?.SecretNames.Contains(name, StringComparer.Ordinal) == true;

        public bool Clears(string name) => _input.ClearSecrets.Contains(name, StringComparer.Ordinal);

        public void RequireSecret(string name, string message)
        {
            if (TrimmedSecret(name).Length == 0 && !HasStored(name))
                throw new ArgumentException(message);
        }

        public void AddTrimmedSecret(string name)
        {
            var value = TrimmedSecret(name);
            if (value.Length > 0) _changes[name] = value;
        }

        public void AddRawSecret(string name)
        {
            var value = RawSecret(name);
            if (value.Length > 0) _changes[name] = value;
        }

        public void SetSecret(string name, string value) => _changes[name] = value;

        public void Remove(string name)
        {
            if (!_changes.ContainsKey(name) && !_removals.Contains(name, StringComparer.Ordinal))
                _removals.Add(name);
        }

        public string? PreviousConfigValue(string property)
        {
            var value = PreviousConfigString(property, "");
            return value.Length == 0 ? null : value;
        }

        public string PreviousConfigString(string property, string fallback)
        {
            if (_existing is null || string.IsNullOrWhiteSpace(_existing.ConfigJson)) return fallback;
            try
            {
                using var document = JsonDocument.Parse(_existing.ConfigJson);
                var value = JsonConfigReader.GetString(document.RootElement, property);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        public ProviderFormResult Result(string configJson) =>
            new(configJson, _changes, _existing is null ? [] : _removals);

        private string Label(string key) =>
            _descriptor.Fields.FirstOrDefault(field => field.Key == key)?.Label ?? key;
    }
}
