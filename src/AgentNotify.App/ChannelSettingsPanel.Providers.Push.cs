using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentNotify.Protocol;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core;

namespace AgentNotify.App;

public partial class ChannelSettingsPanel : System.Windows.Controls.UserControl
{
    private async Task<ProviderProfile> SaveNtfyProviderAsync(ProviderProfile? existing)
    {
        var hasTopic = existing?.SecretNames.Contains("topic", StringComparer.Ordinal) == true;
        var hasToken = existing?.SecretNames.Contains("access_token", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(NtfyServerBox.Text))
            throw new ArgumentException("Enter the ntfy server HTTPS base URL.");
        if (string.IsNullOrWhiteSpace(NtfyTopicBox.Password) && !hasTopic)
            throw new ArgumentException("Enter the ntfy topic.");
        var willHaveToken = !string.IsNullOrWhiteSpace(NtfyTokenBox.Password) ||
                            hasToken && NtfyClearTokenBox.IsChecked != true;
        if (!willHaveToken && NtfyAnonymousBox.IsChecked != true)
            throw new ArgumentException("Enter an ntfy access token or explicitly allow unauthenticated publishing.");
        var config = JsonSerializer.Serialize(new
        {
            serverBaseUrl = NtfyServerBox.Text.Trim(),
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            allowUnauthenticatedTopic = NtfyAnonymousBox.IsChecked == true,
            topicSecretName = "topic",
            accessTokenSecretName = "access_token"
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(NtfyTopicBox.Password)) changes["topic"] = NtfyTopicBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(NtfyTokenBox.Password)) changes["access_token"] = NtfyTokenBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "ntfy",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = NtfyClearTokenBox.IsChecked == true ? new[] { "access_token" } : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        NtfyTopicBox.Clear(); NtfyTokenBox.Clear(); NtfyClearTokenBox.IsChecked = false;
        return saved;
    }

    private async Task<ProviderProfile> SaveGotifyProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("application_token", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(GotifyServerBox.Text))
            throw new ArgumentException("Enter the Gotify server HTTPS base URL.");
        if (string.IsNullOrWhiteSpace(GotifyTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the Gotify application token.");
        var config = JsonSerializer.Serialize(new
        {
            serverBaseUrl = GotifyServerBox.Text.Trim(),
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            applicationTokenSecretName = "application_token"
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(GotifyTokenBox.Password))
            changes["application_token"] = GotifyTokenBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "gotify",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0) await _profiles.UpdateSecretsAsync(saved.Id, changes);
        GotifyTokenBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SavePushoverProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("application_token", StringComparer.Ordinal) == true;
        var hasUserKey = existing?.SecretNames.Contains("user_key", StringComparer.Ordinal) == true;
        var hasDevice = existing?.SecretNames.Contains("device", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(PushoverTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the 30-character Pushover application API token.");
        if (string.IsNullOrWhiteSpace(PushoverUserKeyBox.Password) && !hasUserKey)
            throw new ArgumentException("Enter the 30-character Pushover user or delivery-group key.");
        if (!string.IsNullOrWhiteSpace(PushoverTokenBox.Password) && !IsPushoverKey(PushoverTokenBox.Password.Trim()))
            throw new ArgumentException("The Pushover application token must be 30 alphanumeric characters.");
        if (!string.IsNullOrWhiteSpace(PushoverUserKeyBox.Password) && !IsPushoverKey(PushoverUserKeyBox.Password.Trim()))
            throw new ArgumentException("The Pushover user/group key must be 30 alphanumeric characters.");
        if (!int.TryParse(PushoverRetryBox.Text, out var retry) || retry < 30)
            throw new ArgumentException("Emergency retry must be at least 30 seconds.");
        if (!int.TryParse(PushoverExpireBox.Text, out var expire) || expire is < 1 or > 10_800)
            throw new ArgumentException("Emergency expiry must be between 1 and 10800 seconds.");
        var sound = PushoverSoundBox.Text.Trim();
        if (sound.Length > 64 || sound.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Pushover sound names may contain only letters, digits, underscore, and hyphen.");
        var enteredDevice = PushoverDeviceBox.Password.Trim();
        if (enteredDevice.Length > 0 && (enteredDevice.Length > 25 || enteredDevice.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            throw new ArgumentException("Pushover device names are at most 25 characters using letters, digits, underscore, and hyphen.");

        var config = JsonSerializer.Serialize(new
        {
            applicationTokenSecretName = "application_token",
            userKeySecretName = "user_key",
            deviceSecretName = "device",
            sound,
            criticalAsEmergency = PushoverEmergencyBox.IsChecked == true,
            emergencyRetrySeconds = retry,
            emergencyExpireSeconds = expire
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(PushoverTokenBox.Password))
            changes["application_token"] = PushoverTokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(PushoverUserKeyBox.Password))
            changes["user_key"] = PushoverUserKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(PushoverDeviceBox.Password))
            changes["device"] = PushoverDeviceBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "pushover",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = hasDevice && enteredDevice.Length == 0 && PushoverClearDeviceBox.IsChecked == true
                ? new[] { "device" }
                : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        PushoverTokenBox.Clear();
        PushoverUserKeyBox.Clear();
        PushoverDeviceBox.Clear();
        PushoverClearDeviceBox.IsChecked = false;
        return saved;
    }

    private static bool IsPushoverKey(string value) =>
        value.Length == 30 && value.All(char.IsAsciiLetterOrDigit);

    private async Task<ProviderProfile> SavePushbulletProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("access_token", StringComparer.Ordinal) == true;
        var hasTarget = existing?.SecretNames.Contains("target", StringComparer.Ordinal) == true;
        var targetType = (PushbulletTargetTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var previousTargetType = ReadConfigString(existing?.ConfigJson, "targetType", "all");
        var enteredTarget = PushbulletTargetBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(PushbulletTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the Pushbullet personal access token.");
        if (!string.IsNullOrWhiteSpace(PushbulletTokenBox.Password) && !IsPushbulletToken(PushbulletTokenBox.Password.Trim()))
            throw new ArgumentException("The Pushbullet token must be 16–256 printable characters without spaces.");
        if (PushbulletQuotaBox.IsChecked != true)
            throw new ArgumentException("Acknowledge the Pushbullet monthly quota before saving.");
        if (targetType != "all" && enteredTarget.Length == 0 &&
            (!hasTarget || !string.Equals(previousTargetType, targetType, StringComparison.Ordinal)))
            throw new ArgumentException("Enter the selected Pushbullet device ID, channel tag, or email target.");
        ValidatePushbulletTarget(targetType, enteredTarget);

        var config = JsonSerializer.Serialize(new
        {
            accessTokenSecretName = "access_token",
            targetType,
            targetSecretName = "target",
            quotaAcknowledged = true
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(PushbulletTokenBox.Password))
            changes["access_token"] = PushbulletTokenBox.Password.Trim();
        if (enteredTarget.Length > 0)
            changes["target"] = enteredTarget;
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "pushbullet",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = targetType == "all" && hasTarget ? new[] { "target" } : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        PushbulletTokenBox.Clear();
        PushbulletTargetBox.Clear();
        return saved;
    }

    private static bool IsPushbulletToken(string value) =>
        value.Length is >= 16 and <= 256 && value.All(character => character is >= '!' and <= '~');

    private static void ValidatePushbulletTarget(string targetType, string value)
    {
        if (targetType == "all" || value.Length == 0) return;
        if (targetType == "email")
        {
            if (value.Length > 254 || value.IndexOfAny(['\r', '\n']) >= 0 ||
                !System.Net.Mail.MailAddress.TryCreate(value, out var address) ||
                !string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Enter one valid email address without a display name.");
            return;
        }
        if (targetType is not ("device" or "channel") || value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Device IDs and channel tags may contain only letters, digits, underscore, and hyphen.");
    }

    private static string ReadConfigString(string? configJson, string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return fallback;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var value = GetJsonString(document.RootElement, name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch (JsonException) { return fallback; }
    }

}
