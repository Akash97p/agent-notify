using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotify.Protocol;
using AgentNotify.Core.Domain;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Publishes interactions to Relay-enabled routes through the durable outbox.
/// </summary>
/// <remarks>
/// An interaction reaches the phone as a sealed <c>interaction-request</c> payload
/// riding the existing envelope flow — no delivery-pipeline changes. Only routes
/// that already opted into message bodies (<c>IncludeMessage</c>) carry questions:
/// choices, digests, and nonces ARE message content, so a bodyless route must
/// never receive them. Outbox ids are deterministic per interaction+provider, so
/// republishing is naturally idempotent.
/// </remarks>
public sealed class InteractionRelayPublisher
{
    private readonly IDeliveryRepository _repository;
    private readonly Action _signalDispatcher;
    public InteractionRelayPublisher(IDeliveryRepository repository, Action? signalDispatcher = null)
    {
        _repository = repository;
        _signalDispatcher = signalDispatcher ?? (() => { });
    }

    /// <summary>Enqueues one outbox row per matching Relay route. Returns the count.</summary>
    public async Task<int> PublishAsync(InteractionDto interaction, CancellationToken ct = default)
    {
        var routes = await _repository.ListRoutesAsync(ct);
        if (routes.Count == 0)
            return 0;
        var providers = (await _repository.ListProvidersAsync(ct))
            .Where(profile => profile.Enabled && string.Equals(profile.Kind, "relay", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        if (providers.Count == 0)
            return 0;

        var priority = interaction.Kind == InteractionKind.Text
            ? NotificationPriority.Normal
            : NotificationPriority.High;
        // The notification type this question projects to. A route filtered to a
        // non-answerable type (e.g. error) never carries questions; a route filtered
        // to the other answerable type does not take this kind either.
        var routeType = interaction.Kind == InteractionKind.Permission
            ? NotificationTypes.PermissionRequired
            : NotificationTypes.InputRequired;
        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;

        foreach (var route in routes)
        {
            if (!route.Enabled || !providers.ContainsKey(route.ProviderId))
                continue;
            if (!route.IncludeMessage)
                continue;
            if (route.TypeId is not null &&
                !string.Equals(NotificationTypes.Normalize(route.TypeId), routeType, StringComparison.Ordinal))
                continue;
            if (route.Project is not null &&
                !string.Equals(route.Project, interaction.Project, StringComparison.OrdinalIgnoreCase))
                continue;
            if (route.Agent is not null &&
                !string.Equals(route.Agent, interaction.Agent, StringComparison.OrdinalIgnoreCase))
                continue;
            if (priority < route.MinimumPriority)
                continue;

            var item = new OutboxItem
            {
                Id = OutboxIdFor(interaction.Id, route.ProviderId),
                NotificationId = interaction.Id,
                RouteId = route.Id,
                ProviderId = route.ProviderId,
                PayloadJson = BuildPayload(interaction),
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            if (await _repository.EnqueueAsync(item, ct))
                enqueued++;
        }

        if (enqueued > 0)
            _signalDispatcher();
        return enqueued;
    }

    public static string OutboxIdFor(string interactionId, string providerId) =>
        $"interaction:{interactionId}:{providerId}";

    /// <summary>
    /// The exact JSON sealed into the Relay envelope. Shape authority is
    /// <see cref="InteractionDto"/>; this mirrors it for the phone.
    /// </summary>
    public static string BuildPayload(InteractionDto interaction)
    {
        var payload = new JsonObject
        {
            ["payload_kind"] = InteractionRelayContract.RequestPayloadKind,
            ["contract_version"] = InteractionRelayContract.Version,
            ["interaction"] = new JsonObject
            {
                ["id"] = interaction.Id,
                ["key"] = interaction.Key,
                ["agent"] = interaction.Agent,
                ["agent_instance"] = interaction.AgentInstance,
                ["project"] = interaction.Project,
                ["session_id"] = interaction.SessionId,
                ["kind"] = interaction.Kind switch
                {
                    InteractionKind.Permission => "permission",
                    InteractionKind.SingleChoice => "single_choice",
                    _ => "text"
                },
                ["prompt"] = interaction.Prompt,
                ["choices"] = new JsonArray(interaction.Choices
                    .Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["label"] = c.Label,
                        ["detail"] = c.Detail
                    }).ToArray()),
                ["text_max_length"] = interaction.TextMaxLength,
                ["status"] = interaction.Status.ToString().ToLowerInvariant(),
                ["request_digest"] = interaction.RequestDigest,
                ["nonce"] = interaction.Nonce,
                ["created_at"] = interaction.CreatedAt.ToString("O"),
                ["expires_at"] = interaction.ExpiresAt.ToString("O")
            }
        };
        return payload.ToJsonString(Protocol.Json.Options);
    }
}
