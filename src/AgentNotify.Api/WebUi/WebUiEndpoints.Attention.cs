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

/// <summary>Web UI routes: attention.</summary>
public sealed partial class WebUiEndpoints
{
    private void MapAttention()
    {
        app.MapGet($"{BasePath}/api/notifications", async (HttpContext http, CancellationToken ct) =>
        {
            var attention = http.Request.Query["view"] != "recent";
            var items = await repository.QueryAsync(new NotificationQuery
            {
                Unresolved = attention ? true : null,
                Limit = int.TryParse(http.Request.Query["limit"], out var limit) ? Math.Clamp(limit, 1, 500) : 200
            }, ct);
            return Results.Json(items.Select(DtoMapper.ToDto), JsonOptions);
        });

        app.MapPost($"{BasePath}/api/notifications/{{id}}/{{action}}", async (string id, string action, CancellationToken ct) =>
        {
            var status = action switch
            {
                "resolve" => NotificationStatus.Resolved,
                "dismiss" => NotificationStatus.Dismissed,
                _ => (NotificationStatus?)null
            };
            if (status is null) return Error("Unknown action.", StatusCodes.Status404NotFound);
            var result = await service.UpdateStatusAsync(id, new UpdateNotificationRequest { Status = status.Value }, ct);
            if (result.NotFound) return Error("That notification no longer exists.", StatusCodes.Status404NotFound);
            if (result.Error is not null) return Error(result.Error);
            try { callbacks?.Updated?.Invoke(result.Value!); }
            catch (Exception exception) { logger?.Error("Notification UI callback failed", exception); }
            return Results.Json(DtoMapper.ToDto(result.Value!), JsonOptions);
        });

        // ---- questions ---------------------------------------------------------------------

        if (interactions is not null)
        {
            app.MapGet($"{BasePath}/api/interactions", async (HttpContext http, CancellationToken ct) =>
            {
                var pendingOnly = http.Request.Query["view"] != "recent";
                var items = await interactions.ListAsync(new InteractionQuery { PendingOnly = pendingOnly, Limit = 100 }, ct);
                return Results.Json(items.Select(ToPageDto), JsonOptions);
            });

            app.MapPost($"{BasePath}/api/interactions/{{id}}/respond", async (string id, HttpContext http) =>
            {
                var body = await ReadAsync<AnswerBody>(http);
                if (body is null) return Error("The request body is not valid JSON.");
                var current = await interactions.GetAsync(id, http.RequestAborted);
                if (current.NotFound) return Error("That question no longer exists.", StatusCodes.Status404NotFound);

                // The digest comes from the page, so an answer can only apply to the exact question
                // the person read. The nonce is filled in here; it never leaves the broker.
                var result = await interactions.RespondAsync(id, new RespondInteractionRequest
                {
                    ResponseId = Guid.NewGuid().ToString("N"),
                    RequestDigest = body.RequestDigest ?? "",
                    Nonce = current.Value!.Nonce,
                    ChoiceId = body.ChoiceId,
                    Text = body.Text,
                    Source = "web"
                }, http.RequestAborted);
                if (result.NotFound) return Error("That question no longer exists.", StatusCodes.Status404NotFound);
                if (result.Error is not null)
                    return Error(result.Error, result.Error == "interaction already answered" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
                return Results.Json(ToPageDto(result.Value!), JsonOptions);
            });

            app.MapPost($"{BasePath}/api/interactions/{{id}}/cancel", async (string id, CancellationToken ct) =>
            {
                var result = await interactions.CancelAsync(id, ct);
                return result.NotFound
                    ? Error("That question no longer exists.", StatusCodes.Status404NotFound)
                    : Results.Json(ToPageDto(result.Value!), JsonOptions);
            });
        }
    }
}
