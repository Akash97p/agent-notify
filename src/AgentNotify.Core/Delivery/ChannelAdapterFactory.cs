using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Single source of the implemented outbound channel adapters. The Windows tray process and the
/// portable headless host both build their dispatcher from this list so the two cannot drift apart.
/// </summary>
public static class ChannelAdapterFactory
{
    /// <summary>Creates one instance of every implemented outbound adapter, in dispatch order.</summary>
    public static IReadOnlyList<IOutboundChannelAdapter> CreateAll() =>
    [
        new WebhookChannelAdapter(),
        new SmtpChannelAdapter(),
        new TelegramChannelAdapter(),
        new DiscordChannelAdapter(),
        new SlackChannelAdapter(),
        new TeamsChannelAdapter(),
        new ZohoCliqChannelAdapter(),
        new GoogleChatChannelAdapter(),
        new MattermostChannelAdapter(),
        new MatrixChannelAdapter(),
        new NtfyChannelAdapter(),
        new GotifyChannelAdapter(),
        new PushoverChannelAdapter(),
        new PushbulletChannelAdapter(),
        new TwilioSmsChannelAdapter(),
        new WhatsAppCloudChannelAdapter(),
        new TwilioWhatsAppChannelAdapter(),
        new MqttChannelAdapter()
    ];
}
