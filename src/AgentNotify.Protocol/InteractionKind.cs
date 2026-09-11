namespace AgentNotify.Protocol;

/// <summary>
/// What the human is being asked for. Host adapters must only offer kinds the
/// host can faithfully return; never synthesize a scope the host did not offer.
/// </summary>
public enum InteractionKind
{
    /// <summary>Allow/deny (or host-scoped variants) for one tool action.</summary>
    Permission,
    /// <summary>Exactly one of a bounded choice list.</summary>
    SingleChoice,
    /// <summary>Bounded free text.</summary>
    Text
}
