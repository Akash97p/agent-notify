using AgentNotify.Core.Domain;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Persistence;

/// <summary>Filter for listing interactions. All fields optional.</summary>
public sealed class InteractionQuery
{
    public InteractionStatus? Status { get; set; }
    public bool? PendingOnly { get; set; }
    public string? Agent { get; set; }
    public string? Project { get; set; }
    public string? SessionId { get; set; }
    public int Limit { get; set; } = 100;
}

/// <summary>SQLite-backed interaction storage. Implementations must be safe for
/// concurrent use from the single broker process.</summary>
public interface IInteractionRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<Interaction> CreateAsync(Interaction interaction, CancellationToken ct = default);
    Task<Interaction?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<Interaction?> FindPendingByKeyAsync(string key, CancellationToken ct = default);

    /// <summary>The most recent interaction for a key whatever its status. ARC addresses an
    /// answer by condition key, and must still tell "already answered" apart from "never asked".</summary>
    Task<Interaction?> FindLatestByKeyAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<Interaction>> QueryAsync(InteractionQuery query, CancellationToken ct = default);
    Task<Interaction?> UpdateAsync(Interaction interaction, CancellationToken ct = default);
    /// <summary>Atomically marks pending as expired when past its deadline. Returns the count.</summary>
    Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Deletes terminal interactions updated before the cutoff. Returns the count.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
