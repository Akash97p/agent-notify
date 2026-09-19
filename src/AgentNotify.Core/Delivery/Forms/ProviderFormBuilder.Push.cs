using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

public static partial class ProviderFormBuilder
{
    private static ProviderFormResult BuildPushover(Form form)
    {
        form.RequireSecret("application_token", "Enter the 30-character Pushover application API token.");
        form.RequireSecret("user_key", "Enter the 30-character Pushover user or delivery-group key.");
        var token = form.TrimmedSecret("application_token");
        if (token.Length > 0 && !IsPushoverKey(token))
            throw new ArgumentException("The Pushover application token must be 30 alphanumeric characters.");
        var userKey = form.TrimmedSecret("user_key");
        if (userKey.Length > 0 && !IsPushoverKey(userKey))
            throw new ArgumentException("The Pushover user/group key must be 30 alphanumeric characters.");
        if (!int.TryParse(form.Text("emergency_retry_seconds"), out var retry) || retry < 30)
            throw new ArgumentException("Emergency retry must be at least 30 seconds.");
        if (!int.TryParse(form.Text("emergency_expire_seconds"), out var expire) || expire is < 1 or > 10_800)
            throw new ArgumentException("Emergency expiry must be between 1 and 10800 seconds.");
        var sound = form.Text("sound").Trim();
        if (sound.Length > 64 || sound.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Pushover sound names may contain only letters, digits, underscore, and hyphen.");
        var device = form.TrimmedSecret("device");
        if (device.Length > 0 && (device.Length > 25 || device.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
            throw new ArgumentException("Pushover device names are at most 25 characters using letters, digits, underscore, and hyphen.");

        var config = Serialize(new
        {
            applicationTokenSecretName = "application_token",
            userKeySecretName = "user_key",
            deviceSecretName = "device",
            sound,
            criticalAsEmergency = form.Bool("critical_as_emergency"),
            emergencyRetrySeconds = retry,
            emergencyExpireSeconds = expire
        });
        form.AddTrimmedSecret("application_token");
        form.AddTrimmedSecret("user_key");
        form.AddTrimmedSecret("device");
        if (form.HasStored("device") && device.Length == 0 && form.Clears("device")) form.Remove("device");
        return form.Result(config);
    }

    private static bool IsPushoverKey(string value) =>
        value.Length == 30 && value.All(char.IsAsciiLetterOrDigit);

    private static ProviderFormResult BuildPushbullet(Form form)
    {
        var targetType = form.Choice("target_type", ["all", "device", "channel", "email"]);
        var previousTargetType = form.PreviousConfigString("targetType", "all");
        var target = form.TrimmedSecret("target");
        form.RequireSecret("access_token", "Enter the Pushbullet personal access token.");
        var token = form.TrimmedSecret("access_token");
        if (token.Length > 0 && !IsPrintableSecret(token))
            throw new ArgumentException("The Pushbullet token must be 16–256 printable characters without spaces.");
        if (!form.Bool("quota_acknowledged"))
            throw new ArgumentException("Acknowledge the Pushbullet monthly quota before saving.");
        if (targetType != "all" && target.Length == 0 &&
            (!form.HasStored("target") || !string.Equals(previousTargetType, targetType, StringComparison.Ordinal)))
            throw new ArgumentException("Enter the selected Pushbullet device ID, channel tag, or email target.");
        ValidatePushbulletTarget(targetType, target);

        var config = Serialize(new
        {
            accessTokenSecretName = "access_token",
            targetType,
            targetSecretName = "target",
            quotaAcknowledged = true
        });
        form.AddTrimmedSecret("access_token");
        if (targetType != "all") form.AddTrimmedSecret("target");
        if (targetType == "all" && form.HasStored("target")) form.Remove("target");
        return form.Result(config);
    }

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

        if (value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Device IDs and channel tags may contain only letters, digits, underscore, and hyphen.");
    }

}
