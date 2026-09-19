using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

/// <summary>
/// Validates submitted provider editor state and turns it into the stored configuration document
/// and secret changes each adapter expects.
/// </summary>
/// <remarks>
/// This is the portable twin of the Windows Settings window's provider editor: the property names,
/// secret names, and refusal messages are the same, so a profile saved from either surface is read
/// identically by the delivery adapters. Every refusal is an <see cref="ArgumentException"/> whose
/// message is written for the person filling in the form.
/// </remarks>
public static partial class ProviderFormBuilder
{
    private static readonly HashSet<string> TemplateVariables =
        new(["title", "message", "priority", "type", "agent", "project"], StringComparer.Ordinal);

    public static ProviderFormResult Build(string kind, ProviderFormInput input, ProviderProfile? existing)
    {
        var descriptor = ProviderFormCatalog.Find(kind)
            ?? throw new ArgumentException("Select a supported provider type.");
        if (existing is not null && !string.Equals(existing.Kind, descriptor.Kind, StringComparison.Ordinal))
            throw new ArgumentException("The provider type cannot be changed on a saved profile.");

        var form = new Form(descriptor, input, existing);
        return descriptor.Kind switch
        {
            "webhook" => BuildWebhook(form),
            "smtp" => BuildSmtp(form),
            "telegram" => BuildTelegram(form),
            "discord" => BuildDiscord(form),
            "slack" => BuildSlack(form),
            "teams" => BuildWebhookUrlOnly(form, "Enter the current Microsoft Teams Workflows webhook URL."),
            "zoho_cliq" => BuildWebhookUrlOnly(form, "Enter the Zoho Cliq webhook URL generated from Webhook Tokens."),
            "google_chat" => BuildGoogleChat(form),
            "mattermost" => BuildMattermost(form),
            "matrix" => BuildMatrix(form),
            "ntfy" => BuildNtfy(form),
            "gotify" => BuildGotify(form),
            "pushover" => BuildPushover(form),
            "pushbullet" => BuildPushbullet(form),
            "twilio_sms" => BuildTwilioSms(form),
            "whatsapp_cloud" => BuildWhatsAppCloud(form),
            "twilio_whatsapp" => BuildTwilioWhatsApp(form),
            "mqtt" => BuildMqtt(form),
            "relay" => BuildRelay(form),
            _ => throw new ArgumentException("Select a supported provider type.")
        };
    }

}
