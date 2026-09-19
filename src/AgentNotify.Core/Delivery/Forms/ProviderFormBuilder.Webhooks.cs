using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

public static partial class ProviderFormBuilder
{
    private static ProviderFormResult BuildWebhook(Form form)
    {
        form.RequireSecret("endpoint_url", "Enter the webhook HTTPS endpoint.");
        var keepAuthorization = form.RawSecret("authorization").Length > 0 ||
                                form.HasStored("authorization") && !form.Clears("authorization");
        var keepHmac = form.RawSecret("hmac_secret").Length > 0 ||
                       form.HasStored("hmac_secret") && !form.Clears("hmac_secret");

        var config = Serialize(new
        {
            urlSecretName = "endpoint_url",
            allowPrivateNetwork = form.AllowPrivate,
            secretHeaders = keepAuthorization
                ? new Dictionary<string, string> { ["Authorization"] = "authorization" }
                : null,
            signature = keepHmac ? new { secretName = "hmac_secret" } : null
        });

        form.AddTrimmedSecret("endpoint_url");
        form.AddRawSecret("authorization");
        form.AddRawSecret("hmac_secret");
        if (form.Clears("authorization")) form.Remove("authorization");
        if (form.Clears("hmac_secret")) form.Remove("hmac_secret");
        return form.Result(config);
    }

    private static ProviderFormResult BuildSmtp(Form form)
    {
        form.RequireSecret("username", "Enter the SMTP username.");
        if (form.RawSecret("password").Length == 0 && !form.HasStored("password"))
            throw new ArgumentException("Enter the SMTP password or app password.");
        if (!int.TryParse(form.Text("port"), out var port) || port is < 1 or > 65535)
            throw new ArgumentException("SMTP port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(form.Text("host")))
            throw new ArgumentException("Enter the SMTP host.");
        if (string.IsNullOrWhiteSpace(form.Text("from_address")))
            throw new ArgumentException("Enter the From email address.");
        var recipients = form.Text("recipients")
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (recipients.Length is 0 or > 10)
            throw new ArgumentException("Enter one to ten recipient email addresses.");

        var config = Serialize(new
        {
            host = form.Text("host").Trim(),
            port,
            security = form.Choice("security", ["start_tls", "tls"]),
            allowPrivateNetwork = form.AllowPrivate,
            fromAddress = form.Text("from_address").Trim(),
            fromName = form.Text("from_name").Trim(),
            recipients,
            subjectPrefix = form.Text("subject_prefix"),
            usernameSecretName = "username",
            passwordSecretName = "password"
        });
        form.AddTrimmedSecret("username");
        form.AddRawSecret("password");
        return form.Result(config);
    }

    private static ProviderFormResult BuildTelegram(Form form)
    {
        form.RequireSecret("bot_token", "Enter the Telegram bot token.");
        form.RequireSecret("chat_id", "Enter the Telegram chat ID or @channel username.");
        int? threadId = null;
        if (!string.IsNullOrWhiteSpace(form.Text("message_thread_id")))
        {
            if (!int.TryParse(form.Text("message_thread_id"), out var parsed) || parsed <= 0)
                throw new ArgumentException("Telegram topic/thread ID must be a positive integer.");
            threadId = parsed;
        }

        var config = Serialize(new
        {
            botTokenSecretName = "bot_token",
            chatIdSecretName = "chat_id",
            messageThreadId = threadId,
            disableNotification = form.Bool("disable_notification"),
            protectContent = form.Bool("protect_content")
        });
        form.AddTrimmedSecret("bot_token");
        form.AddTrimmedSecret("chat_id");
        return form.Result(config);
    }

    private static ProviderFormResult BuildDiscord(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the Discord incoming-webhook URL.");
        var username = form.Text("username").Trim();
        if (username.Length is 0 or > 80 || username.Any(char.IsControl))
            throw new ArgumentException("Discord webhook display name must contain 1 to 80 characters.");
        var threadId = string.IsNullOrWhiteSpace(form.Text("thread_id")) ? null : form.Text("thread_id").Trim();
        if (threadId is not null &&
            (threadId.Length is < 5 or > 20 || !threadId.All(char.IsAsciiDigit) || !threadId.Any(c => c != '0')))
            throw new ArgumentException("Discord thread ID must be a valid numeric snowflake.");

        var config = Serialize(new { webhookUrlSecretName = "webhook_url", username, threadId });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildSlack(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the Slack incoming-webhook URL.");
        var threadTimestamp = string.IsNullOrWhiteSpace(form.Text("thread_timestamp")) ? null : form.Text("thread_timestamp").Trim();
        if (threadTimestamp is not null)
        {
            var separator = threadTimestamp.IndexOf('.');
            if (separator is < 10 or > 20 || separator != threadTimestamp.LastIndexOf('.') ||
                !threadTimestamp[..separator].All(char.IsAsciiDigit) ||
                threadTimestamp[(separator + 1)..].Length != 6 ||
                !threadTimestamp[(separator + 1)..].All(char.IsAsciiDigit))
                throw new ArgumentException("Slack thread timestamp must look like 1712345678.123456.");
        }

        var config = Serialize(new { webhookUrlSecretName = "webhook_url", threadTimestamp });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildWebhookUrlOnly(Form form, string missingMessage)
    {
        form.RequireSecret("webhook_url", missingMessage);
        form.AddTrimmedSecret("webhook_url");
        return form.Result(Serialize(new { webhookUrlSecretName = "webhook_url" }));
    }

    private static ProviderFormResult BuildGoogleChat(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the incoming-webhook URL copied from Google Chat.");
        var threadKey = string.IsNullOrWhiteSpace(form.Text("thread_key")) ? null : form.Text("thread_key").Trim();
        if (threadKey is not null && (threadKey.Length > 4000 || threadKey.Any(char.IsControl)))
            throw new ArgumentException("Google Chat thread key must contain at most 4000 characters and no control characters.");

        var config = Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            threadKey,
            threadReplyPolicy = form.Choice("thread_reply_policy", ["fallback", "fail"])
        });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildMattermost(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the Mattermost incoming-webhook URL.");
        var config = Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            allowPrivateNetwork = form.AllowPrivate,
            silent = form.Bool("silent")
        });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildMatrix(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Text("homeserver_base_url")))
            throw new ArgumentException("Enter the Matrix homeserver HTTPS base URL.");
        form.RequireSecret("access_token", "Enter the Matrix access token.");
        form.RequireSecret("room_id", "Enter the Matrix room ID.");
        var config = Serialize(new
        {
            homeserverBaseUrl = form.Text("homeserver_base_url").Trim(),
            allowPrivateNetwork = form.AllowPrivate,
            accessTokenSecretName = "access_token",
            roomIdSecretName = "room_id"
        });
        form.AddTrimmedSecret("access_token");
        form.AddTrimmedSecret("room_id");
        return form.Result(config);
    }

    private static ProviderFormResult BuildNtfy(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Text("server_base_url")))
            throw new ArgumentException("Enter the ntfy server HTTPS base URL.");
        form.RequireSecret("topic", "Enter the ntfy topic.");
        var willHaveToken = form.TrimmedSecret("access_token").Length > 0 ||
                            form.HasStored("access_token") && !form.Clears("access_token");
        if (!willHaveToken && !form.Bool("allow_unauthenticated_topic"))
            throw new ArgumentException("Enter an ntfy access token or explicitly allow unauthenticated publishing.");

        var config = Serialize(new
        {
            serverBaseUrl = form.Text("server_base_url").Trim(),
            allowPrivateNetwork = form.AllowPrivate,
            allowUnauthenticatedTopic = form.Bool("allow_unauthenticated_topic"),
            topicSecretName = "topic",
            accessTokenSecretName = "access_token"
        });
        form.AddTrimmedSecret("topic");
        form.AddTrimmedSecret("access_token");
        if (form.Clears("access_token")) form.Remove("access_token");
        return form.Result(config);
    }

    private static ProviderFormResult BuildGotify(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Text("server_base_url")))
            throw new ArgumentException("Enter the Gotify server HTTPS base URL.");
        form.RequireSecret("application_token", "Enter the Gotify application token.");
        var config = Serialize(new
        {
            serverBaseUrl = form.Text("server_base_url").Trim(),
            allowPrivateNetwork = form.AllowPrivate,
            applicationTokenSecretName = "application_token"
        });
        form.AddTrimmedSecret("application_token");
        return form.Result(config);
    }

}
