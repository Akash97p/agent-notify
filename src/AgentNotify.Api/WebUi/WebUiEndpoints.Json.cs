using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

/// <summary>Web UI routes: request/response helpers and JSON shapes.</summary>
public sealed partial class WebUiEndpoints
{

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, JsonOptions, statusCode: status);

    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(JsonOptions, http.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static void Notify(WebUiOptions options, AgentNotifyConfig config, bool restartRequired, IAppLogger? logger)
    {
        try { options.ConfigSaved?.Invoke(config, restartRequired); }
        catch (Exception exception) { logger?.Error("Web UI configuration callback failed", exception); }
    }

    private static string PlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        return "Linux";
    }

    private static object DeliveryJson(DeliveryDiagnosticSnapshot snapshot) => new
    {
        pending = snapshot.Pending,
        processing = snapshot.Processing,
        retry = snapshot.Retry,
        delivered = snapshot.Delivered,
        dead_letter = snapshot.DeadLetter,
        adapters = snapshot.RegisteredAdapters
    };

    private static object ProviderJson(ProviderProfile profile)
    {
        var relay = profile.Kind == "relay"
            ? new
            {
                connected = profile.SecretNames.Contains("installation_token", StringComparer.Ordinal),
                relay_name = ProviderFormReader.ReadConfigString(profile, "relay_name"),
                installation_id = ProviderFormReader.ReadConfigString(profile, "installation_id")
            }
            : null;
        return new
        {
            id = profile.Id,
            name = profile.Name,
            kind = profile.Kind,
            enabled = profile.Enabled,
            secret_names = profile.SecretNames,
            values = ProviderFormReader.ReadValues(profile),
            relay,
            created_at = profile.CreatedAt,
            updated_at = profile.UpdatedAt
        };
    }

    private static InteractionDto ToPageDto(Interaction interaction)
    {
        var dto = DtoMapper.ToDto(interaction);
        // The nonce authorizes Relay answers; the page answers through the broker instead.
        dto.Nonce = "";
        return dto;
    }

    private static object SkillJson(AgentSkillTarget target, WslHome? home)
    {
        string? root = null;
        var state = "unavailable";
        try
        {
            root = AgentSkillCatalog.DefaultSkillsRoot(target, homeDirectory: home?.WindowsHome);
            state = SkillInstaller.Inspect(root, WebUiSkill.Files(target)) switch
            {
                SkillInstallState.UpToDate => "up_to_date",
                SkillInstallState.Outdated => "outdated",
                _ => "not_installed"
            };
        }
        catch (InvalidOperationException)
        {
        }

        return new
        {
            id = target.Id,
            display_name = target.DisplayName,
            note = target.Note,
            wsl = home?.Distribution,
            environment = home is null ? null : "WSL · " + home.Distribution,
            destination = root is null ? null : SkillInstaller.SkillDirectory(root),
            state
        };
    }

}
