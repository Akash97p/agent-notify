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

/// <summary>Web UI routes: channels.</summary>
public sealed partial class WebUiEndpoints
{
    private void MapChannels()
    {
        app.MapGet($"{BasePath}/api/provider-kinds", () => Results.Json(ProviderFormCatalog.All, JsonOptions));

        app.MapGet($"{BasePath}/api/providers", async (CancellationToken ct) =>
            Results.Json((await options.Providers.ListAsync(ct)).Select(ProviderJson), JsonOptions));

        // Typed explicitly: an expression-bodied (HttpContext) lambda binds to RequestDelegate, which
        // discards the returned IResult and answers 200 with an empty body.
        app.MapPost($"{BasePath}/api/providers", (Func<HttpContext, Task<IResult>>)(http => SaveProviderAsync(http, null)));

        app.MapPut($"{BasePath}/api/providers/{{id}}", (string id, HttpContext http) => SaveProviderAsync(http, id));

        app.MapDelete($"{BasePath}/api/providers/{{id}}", async (string id, CancellationToken ct) =>
        {
            await options.Providers.DeleteAsync(id, ct);
            return Results.Json(new { deleted = id }, JsonOptions);
        });

        app.MapPost($"{BasePath}/api/providers/{{id}}/test", async (string id, CancellationToken ct) =>
        {
            if ((await options.Providers.ListAsync(ct)).All(p => p.Id != id))
                return Error("That provider no longer exists.", StatusCodes.Status404NotFound);
            var result = await options.Dispatcher.TestProviderAsync(id, ct: ct);
            return Results.Json(new
            {
                succeeded = result.Succeeded,
                status_code = result.StatusCode,
                error_code = result.ErrorCode,
                message = result.Succeeded
                    ? $"Test delivered (provider status {result.StatusCode?.ToString() ?? "ok"})."
                    : result.ErrorCode switch
                    {
                        "no_devices_paired" => "Connected to the relay, but no phone is paired yet. Pair a phone from the relay console, then send a test.",
                        "relay_device_not_found" => "The selected Relay phone is no longer paired. Pair it again, then send a test.",
                        "relay_installation_identity_missing" => "This Relay profile predates installation identity. Connect it again.",
                        "provider_disabled" => "Enable the provider before sending a test.",
                        _ => $"Test failed: {result.ErrorCode ?? "unspecified"}."
                    }
            }, JsonOptions);
        });

        async Task<IResult> SaveProviderAsync(HttpContext http, string? id)
        {
            var body = await ReadAsync<ProviderBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            RelayPairingOutcome? pairing = null;
            if (!string.IsNullOrWhiteSpace(body.PairingId))
            {
                pairing = pairings.Take(body.PairingId);
                if (pairing is null)
                    return Error("That Relay connection is no longer waiting to be saved. Connect again.");
            }

            try
            {
                var saved = await forms.SaveAsync(id, body.Kind ?? "", new ProviderFormInput
                {
                    Name = body.Name ?? "",
                    Enabled = body.Enabled,
                    Values = body.Values ?? new Dictionary<string, string?>(),
                    Secrets = body.Secrets ?? new Dictionary<string, string?>(),
                    ClearSecrets = body.ClearSecrets ?? [],
                    Pairing = pairing
                }, http.RequestAborted);
                return Results.Json(ProviderJson(saved), JsonOptions);
            }
            catch (KeyNotFoundException)
            {
                return Error("That provider no longer exists.", StatusCodes.Status404NotFound);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                return Error(exception.Message);
            }
        }

        // ---- relay pairing -----------------------------------------------------------------

        app.MapPost($"{BasePath}/api/relay/pairings", async (HttpContext http) =>
        {
            var body = await ReadAsync<PairingBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            string? installId = null;
            if (!string.IsNullOrWhiteSpace(body.ProviderId))
            {
                var profile = (await options.Providers.ListAsync(http.RequestAborted)).FirstOrDefault(p => p.Id == body.ProviderId);
                installId = ProviderFormReader.ReadConfigString(profile, "install_id");
            }

            try
            {
                var snapshot = await pairings.StartAsync(body.SenderName, installId, http.RequestAborted);
                return Results.Json(snapshot, JsonOptions);
            }
            catch (RelayPairingException exception)
            {
                return Error(RelayPairingSessions.Describe(exception), StatusCodes.Status502BadGateway);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        });

        app.MapGet($"{BasePath}/api/relay/pairings/{{id}}", (string id) =>
            pairings.Get(id) is { } snapshot
                ? Results.Json(snapshot, JsonOptions)
                : Error("That connection request is gone. Connect again.", StatusCodes.Status404NotFound));

        app.MapDelete($"{BasePath}/api/relay/pairings/{{id}}", (string id) =>
            Results.Json(new { cancelled = pairings.Cancel(id) }, JsonOptions));

        // ---- routes ------------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/routes", async (CancellationToken ct) =>
            Results.Json(await options.Routes.ListAsync(ct), JsonOptions));

        app.MapPost($"{BasePath}/api/routes", (Func<HttpContext, Task<IResult>>)(http => SaveRouteAsync(http, null)));
        app.MapPut($"{BasePath}/api/routes/{{id}}", (string id, HttpContext http) => SaveRouteAsync(http, id));

        app.MapDelete($"{BasePath}/api/routes/{{id}}", async (string id, CancellationToken ct) =>
        {
            await options.Routes.DeleteAsync(id, ct);
            return Results.Json(new { deleted = id }, JsonOptions);
        });

        async Task<IResult> SaveRouteAsync(HttpContext http, string? id)
        {
            var body = await ReadAsync<RouteBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            if (id is not null && (await options.Routes.ListAsync(http.RequestAborted)).All(r => r.Id != id))
                return Error("That route no longer exists.", StatusCodes.Status404NotFound);
            if (!Enum.TryParse<NotificationPriority>(body.MinimumPriority, ignoreCase: true, out var priority))
                return Error("Select a minimum priority.");
            try
            {
                var route = await options.Routes.SaveAsync(id, body.Name ?? "", body.ProviderId ?? "", body.Enabled,
                    priority, body.TypeId, body.Project, body.Agent, body.IncludeMessage, http.RequestAborted);
                return Results.Json(route, JsonOptions);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        }

        app.MapGet($"{BasePath}/api/delivery", async (CancellationToken ct) =>
            Results.Json(DeliveryJson(await options.Dispatcher.GetDiagnosticsAsync(ct)), JsonOptions));
    }
}
