namespace AgentNotify.Core.Delivery.Forms;

/// <summary>
/// The editable shape of every outbound provider kind, independent of any UI toolkit.
/// </summary>
/// <remarks>
/// Field keys are form keys, not stored property names: <see cref="ProviderFormBuilder"/> maps them
/// onto the configuration document each adapter reads and onto the secret names it decrypts.
/// </remarks>
public static class ProviderFormCatalog
{
    private const string AllowPrivateKey = "allow_private_network";

    private static readonly ProviderField AllowPrivate = new(AllowPrivateKey, "Allow private and loopback destinations", ProviderFieldType.Checkbox)
    {
        Help = "Link-local and cloud metadata addresses stay blocked.",
        Advanced = true
    };

    private static readonly IReadOnlyList<ProviderFieldOption> MinimumPriorities =
    [
        new("critical", "Critical only"),
        new("high", "High and critical"),
        new("normal", "Normal and above"),
        new("low", "All priorities")
    ];

    private static readonly IReadOnlyList<ProviderFieldOption> TwilioCredentialModes =
    [
        new("api_key", "API Key SID + secret (recommended)"),
        new("auth_token", "Account SID + Auth Token (local testing only)")
    ];

    private static ProviderField Secret(string key, string label, bool required = true) =>
        new(key, label, ProviderFieldType.Secret) { Required = required };

    private static ProviderField Webhook(string label, string? help = null) =>
        Secret("webhook_url", label) with { Help = help };

    public static IReadOnlyList<ProviderKindDescriptor> All { get; } =
    [
        new("webhook", "Webhook", "Developer", "POST a signed JSON payload to your own HTTPS endpoint.",
        [
            Secret("endpoint_url", "HTTPS endpoint URL"),
            Secret("authorization", "Authorization header value", required: false) with { Clearable = true },
            Secret("hmac_secret", "HMAC signing key", required: false) with { Clearable = true, Help = "Signs each payload so the receiver can verify it." },
            AllowPrivate
        ]),

        new("smtp", "SMTP email", "Email and SMS", "Send mail through an authenticated SMTP server with mandatory TLS.",
        [
            new("host", "SMTP host", ProviderFieldType.Text) { Required = true, Placeholder = "smtp.example.com" },
            new("port", "Port", ProviderFieldType.Number) { Required = true, Default = "587" },
            new("security", "Transport security", ProviderFieldType.Select)
            {
                Default = "start_tls",
                Options = [new("start_tls", "STARTTLS (required)"), new("tls", "TLS on connect")]
            },
            new("from_address", "From address", ProviderFieldType.Text) { Required = true },
            new("from_name", "From display name", ProviderFieldType.Text) { Default = "AgentNotify" },
            new("recipients", "Recipients", ProviderFieldType.Multiline) { Required = true, Help = "One to ten addresses, separated by commas, semicolons, or new lines." },
            new("subject_prefix", "Subject prefix", ProviderFieldType.Text) { Default = "[AgentNotify] " },
            Secret("username", "SMTP username"),
            Secret("password", "SMTP password or app password"),
            AllowPrivate
        ]),

        new("telegram", "Telegram Bot", "Chat", "Post through the official Telegram Bot API.",
        [
            Secret("bot_token", "Bot token from @BotFather"),
            Secret("chat_id", "Chat ID or @channel username"),
            new("message_thread_id", "Topic or thread ID", ProviderFieldType.Number) { Help = "Optional." },
            new("disable_notification", "Send silently (no notification sound)", ProviderFieldType.Checkbox),
            new("protect_content", "Protect messages from forwarding and saving", ProviderFieldType.Checkbox) { Default = "true" }
        ])
        {
            Notes = ["The user must start the bot first, or the bot must be allowed to post in the destination chat or channel."]
        },

        new("discord", "Discord webhook", "Chat", "Post to a Discord channel through an incoming webhook.",
        [
            Webhook("Discord incoming-webhook URL"),
            new("username", "Webhook display name", ProviderFieldType.Text) { Required = true, Default = "AgentNotify" },
            new("thread_id", "Thread ID", ProviderFieldType.Text) { Help = "Optional." }
        ])
        {
            Notes = ["Only official discord.com webhook URLs are accepted. User, role, @everyone, and @here mentions are always suppressed."]
        },

        new("slack", "Slack webhook", "Chat", "Post to Slack or GovSlack through an incoming webhook.",
        [
            Webhook("Slack incoming-webhook URL"),
            new("thread_timestamp", "Thread timestamp", ProviderFieldType.Text) { Placeholder = "1712345678.123456", Help = "Optional." }
        ])
        {
            Notes = ["Slack markup, automatic link names, and control-sequence mentions are suppressed."]
        },

        new("teams", "Microsoft Teams Workflows", "Chat", "Post an Adaptive Card through a Teams Workflows webhook.",
        [
            Webhook("Teams Workflows webhook URL")
        ])
        {
            Notes = ["Use the current URL copied from the Teams Workflows app. Legacy Office 365 connector and retired logic.azure.com URLs are not accepted."]
        },

        new("zoho_cliq", "Zoho Cliq webhook", "Chat", "Post to a Zoho Cliq channel or bot.",
        [
            Webhook("Zoho Cliq channel or bot webhook URL")
        ])
        {
            Notes = ["Paste the URL generated from Cliq Webhook Tokens. All nine official Zoho Cliq data-center domains are supported."]
        },

        new("google_chat", "Google Chat webhook", "Chat", "Post to a Google Chat space through an incoming webhook.",
        [
            Webhook("Google Chat incoming-webhook URL"),
            new("thread_key", "Thread key", ProviderFieldType.Text) { Help = "Optional. Starts or reuses one thread." },
            new("thread_reply_policy", "If the thread does not exist", ProviderFieldType.Select)
            {
                Default = "fallback",
                Options = [new("fallback", "Start a new thread"), new("fail", "Fail delivery")]
            }
        ])
        {
            Notes = ["Paste the complete URL copied from the space. User and everyone mentions are suppressed."]
        },

        new("mattermost", "Mattermost webhook", "Chat", "Post to a self-hosted or cloud Mattermost channel.",
        [
            Webhook("Mattermost incoming-webhook URL"),
            new("silent", "Deliver silently (no desktop, push, email, unread, or mention notification)", ProviderFieldType.Checkbox),
            AllowPrivate
        ])
        {
            Notes = ["Self-hosted HTTPS servers and subpath installations are supported. TLS certificate validation is always required."]
        },

        new("matrix", "Matrix room", "Chat", "Post to an unencrypted Matrix room through the Client-Server API.",
        [
            new("homeserver_base_url", "Homeserver HTTPS base URL", ProviderFieldType.Url) { Required = true, Placeholder = "https://matrix.example.org" },
            Secret("access_token", "Access token"),
            Secret("room_id", "Room ID beginning with !"),
            AllowPrivate
        ])
        {
            Notes = ["The access-token account must already be joined and allowed to post. Encrypted rooms are not supported yet."]
        },

        new("ntfy", "ntfy push", "Push", "Publish to an ntfy topic on ntfy.sh or your own server.",
        [
            new("server_base_url", "ntfy server HTTPS base URL", ProviderFieldType.Url) { Required = true, Default = "https://ntfy.sh" },
            Secret("topic", "Topic"),
            Secret("access_token", "Access token", required: false) with { Clearable = true },
            new("allow_unauthenticated_topic", "Allow unauthenticated publishing (the topic may be publicly readable and writable)", ProviderFieldType.Checkbox),
            AllowPrivate
        ])
        {
            Warning = "A protected self-hosted topic with a dedicated token is recommended. ntfy.sh topics are public unless account access control protects them."
        },

        new("gotify", "Gotify push", "Push", "Push plain-text messages to a Gotify server.",
        [
            new("server_base_url", "Gotify server HTTPS base URL", ProviderFieldType.Url) { Required = true, Help = "Without /message." },
            Secret("application_token", "Application token"),
            AllowPrivate
        ])
        {
            Notes = ["Messages are sent as text/plain. Markdown, remote images, click URLs, and actions are not enabled for agent-provided content."]
        },

        new("pushover", "Pushover", "Push", "Push to Pushover devices, with opt-in emergency alerts.",
        [
            Secret("application_token", "Application API token"),
            Secret("user_key", "User or delivery-group key"),
            Secret("device", "Device name", required: false) with { Clearable = true, Help = "Optional. Restricts delivery to one device." },
            new("sound", "Sound name", ProviderFieldType.Text) { Help = "Blank uses the account default." },
            new("critical_as_emergency", "Send critical notifications as repeating emergency alerts", ProviderFieldType.Checkbox),
            new("emergency_retry_seconds", "Emergency retry interval (seconds, minimum 30)", ProviderFieldType.Number) { Default = "60" },
            new("emergency_expire_seconds", "Emergency expiry (seconds, 1–10800)", ProviderFieldType.Number) { Default = "3600" }
        ])
        {
            Warning = "Emergency alerts repeat until acknowledged or expired."
        },

        new("pushbullet", "Pushbullet", "Push", "Send plain note pushes to Pushbullet.",
        [
            Secret("access_token", "Personal access token"),
            new("target_type", "Destination", ProviderFieldType.Select)
            {
                Default = "all",
                Options =
                [
                    new("all", "All devices on this account"),
                    new("device", "One device ID"),
                    new("channel", "Owned channel tag"),
                    new("email", "Email (may fall back to email delivery)")
                ]
            },
            Secret("target", "Device ID, channel tag, or email", required: false) with
            {
                ShowWhen = new("target_type", ["device", "channel", "email"])
            },
            new("quota_acknowledged", "I understand free accounts are limited to 500 pushes a month, and test sends count", ProviderFieldType.Checkbox) { Required = true }
        ])
        {
            Warning = "The token grants full access to the Pushbullet account. AgentNotify sends plain note pushes only."
        },

        new("twilio_sms", "Twilio SMS", "Email and SMS", "Send one-segment SMS to one permitted recipient.",
        [
            Secret("account_sid", "Account SID (AC…)"),
            new("credential_mode", "Authentication", ProviderFieldType.Select) { Default = "api_key", Options = TwilioCredentialModes },
            Secret("credential_sid", "API Key SID (SK…)", required: false) with { ShowWhen = new("credential_mode", ["api_key"]) },
            Secret("credential_secret", "API Key secret or Auth Token"),
            Secret("recipient", "Recipient in E.164 form") with { Placeholder = "+15551234567" },
            new("sender_type", "Sender", ProviderFieldType.Select)
            {
                Default = "messaging_service",
                Options = [new("messaging_service", "Messaging Service SID (MG…)"), new("phone", "Twilio phone number (E.164)")]
            },
            Secret("sender", "Messaging Service SID or Twilio phone number"),
            new("minimum_priority", "Minimum notification priority", ProviderFieldType.Select) { Default = "critical", Options = MinimumPriorities },
            new("validity_period_seconds", "Maximum queue validity (seconds, 6–36000)", ProviderFieldType.Number) { Default = "300" },
            new("paid_send_consent", "I authorize paid SMS sends to this one recipient; test sends are charged too", ProviderFieldType.Checkbox) { Required = true }
        ])
        {
            Paid = true,
            Warning = "One segment, one recipient, no media, links, or callbacks. A crash during a send can still duplicate it on recovery."
        },

        new("whatsapp_cloud", "WhatsApp Cloud API", "Email and SMS", "Send approved text templates to one opted-in recipient.",
        [
            new("api_version", "Meta Graph API version", ProviderFieldType.Text) { Required = true, Default = "v25.0" },
            Secret("phone_number_id", "WhatsApp phone-number ID"),
            Secret("access_token", "System-user access token with whatsapp_business_messaging"),
            Secret("recipient", "Opted-in recipient in E.164 form") with { Placeholder = "+15551234567" },
            new("template_name", "Approved text-template name", ProviderFieldType.Text) { Required = true, Default = "agentnotify_alert" },
            new("language_code", "Template language code", ProviderFieldType.Text) { Required = true, Default = "en_US" },
            new("body_parameters", "Body variables in approved order", ProviderFieldType.Text)
            {
                Default = "title,message",
                Help = "Comma-separated, up to five of: title, message, priority, type, agent, project."
            },
            new("minimum_priority", "Minimum notification priority", ProviderFieldType.Select) { Default = "critical", Options = MinimumPriorities },
            new("recipient_opt_in_acknowledged", "This one recipient explicitly opted in to these notifications", ProviderFieldType.Checkbox) { Required = true },
            new("template_approved_acknowledged", "This template, language, and variable order are approved in WhatsApp Manager", ProviderFieldType.Checkbox) { Required = true },
            new("paid_send_consent", "I authorize paid template sends; test sends can be charged too", ProviderFieldType.Checkbox) { Required = true }
        ])
        {
            Paid = true,
            Warning = "Approved text templates only — never free-form messages, media, or buttons."
        },

        new("twilio_whatsapp", "Twilio WhatsApp", "Email and SMS", "Send approved Content Templates through Twilio.",
        [
            Secret("account_sid", "Account SID (AC…)"),
            new("credential_mode", "Authentication", ProviderFieldType.Select) { Default = "api_key", Options = TwilioCredentialModes },
            Secret("credential_sid", "API Key SID (SK…)", required: false) with { ShowWhen = new("credential_mode", ["api_key"]) },
            Secret("credential_secret", "API Key secret or Auth Token"),
            Secret("recipient", "Opted-in recipient in E.164 form") with { Placeholder = "+15551234567" },
            Secret("messaging_service_sid", "WhatsApp-enabled Messaging Service SID (MG…)"),
            Secret("content_sid", "Approved Content Template SID (HX…)"),
            new("content_variables", "Content variables in approved numbered order", ProviderFieldType.Text)
            {
                Default = "title,message",
                Help = "Comma-separated, up to five of: title, message, priority, type, agent, project."
            },
            new("minimum_priority", "Minimum notification priority", ProviderFieldType.Select) { Default = "critical", Options = MinimumPriorities },
            new("validity_period_seconds", "Maximum queue validity (seconds, 6–36000)", ProviderFieldType.Number) { Default = "300" },
            new("recipient_opt_in_acknowledged", "This one recipient explicitly opted in to these notifications", ProviderFieldType.Checkbox) { Required = true },
            new("template_approved_acknowledged", "The HX template is approved and this is its exact variable order", ProviderFieldType.Checkbox) { Required = true },
            new("text_only_template_acknowledged", "The HX template is text-only, without media, buttons, catalogues, or dynamic URLs", ProviderFieldType.Checkbox) { Required = true },
            new("paid_send_consent", "I authorize paid Twilio WhatsApp sends; test sends can be charged too", ProviderFieldType.Checkbox) { Required = true }
        ])
        {
            Paid = true,
            Warning = "Content templates only — never a free-form body, sender override, media, link, or callback."
        },

        new("mqtt", "MQTT 5 (TLS)", "Developer", "Publish JSON to a fixed topic on a TLS MQTT 5 broker.",
        [
            new("broker_host", "Broker DNS host or IP address", ProviderFieldType.Text) { Required = true, Placeholder = "mqtt.example.com" },
            new("port", "TLS port", ProviderFieldType.Number) { Required = true, Default = "8883" },
            new("client_id", "Stable client ID", ProviderFieldType.Text) { Required = true, Default = "agentnotify" },
            Secret("topic", "Exact publish topic") with { Help = "Wildcards are not allowed." },
            new("authentication_mode", "Authentication", ProviderFieldType.Select)
            {
                Default = "username_password",
                Options =
                [
                    new("username_password", "Username + password"),
                    new("client_certificate", "Client certificate"),
                    new("username_and_certificate", "Username + password + client certificate"),
                    new("anonymous", "Anonymous TLS")
                ]
            },
            Secret("username", "Username", required: false) with { ShowWhen = new("authentication_mode", ["username_password", "username_and_certificate"]) },
            Secret("password", "Password", required: false) with { ShowWhen = new("authentication_mode", ["username_password", "username_and_certificate"]) },
            Secret("client_certificate_thumbprint", "Client certificate SHA-1 or SHA-256 thumbprint", required: false) with
            {
                ShowWhen = new("authentication_mode", ["client_certificate", "username_and_certificate"]),
                Help = "Windows only: the certificate and its private key stay in the Current User certificate store."
            },
            new("anonymous_acknowledged", "I explicitly allow anonymous publishing to this TLS broker", ProviderFieldType.Checkbox)
            {
                ShowWhen = new("authentication_mode", ["anonymous"])
            },
            new("qos", "Publish QoS", ProviderFieldType.Select)
            {
                Default = "1",
                Options =
                [
                    new("0", "QoS 0 — at most once"),
                    new("1", "QoS 1 — at least once, duplicates possible"),
                    new("2", "QoS 2 — exactly once within one MQTT session")
                ]
            },
            new("duplicate_risk_acknowledged", "I accept possible duplicates for QoS 1/2 and will deduplicate by delivery ID", ProviderFieldType.Checkbox)
            {
                ShowWhen = new("qos", ["1", "2"])
            },
            new("message_expiry_seconds", "Message expiry (seconds, 5–86400)", ProviderFieldType.Number) { Default = "300" },
            AllowPrivate
        ])
        {
            Notes = ["TLS 1.2 or 1.3 with normal certificate validation is mandatory. Publishes are JSON and non-retained."]
        },

        new("relay", "AgentNotify Relay", "Phone", "Send end-to-end encrypted envelopes to your paired phones through the hosted AgentNotify Relay.",
        [
            new("sender_name", "Sender name", ProviderFieldType.Text) { Help = "Optional. Shown to the Relay and your devices; up to 100 characters." },
            Secret("installation_token", "Manual installation token", required: false) with
            {
                Clearable = true,
                Advanced = true,
                Help = "For headless installs. Connecting normally stores this for you."
            }
        ])
        {
            SupportsPairing = true,
            Notes =
            [
                "Relay is a hosted service at an.relay.dev.kabanitech.com. There is no server address to enter.",
                "Press Connect and approve the short code in your browser. Local history stays authoritative while the Relay is unreachable."
            ]
        }
    ];

    public static ProviderKindDescriptor? Find(string? kind) =>
        All.FirstOrDefault(descriptor => string.Equals(descriptor.Kind, kind, StringComparison.Ordinal));
}
