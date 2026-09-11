using System.Text.Json;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Durable per-provider Relay poll cursors. The cursor is an opaque Relay string;
/// advancing it acknowledges receipt, so it moves forward only after a poll is
/// fully processed. File access mirrors the secret-store posture: owner-only.
/// </summary>
public sealed class RelayCursorStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RelayCursorStore(string configDir)
    {
        if (string.IsNullOrWhiteSpace(configDir))
            throw new ArgumentException("configDir is required", nameof(configDir));
        _filePath = Path.Combine(configDir, "relay_response_cursors.json");
    }

    public async Task<string> GetAsync(string providerId, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        return all.TryGetValue(providerId, out var cursor) ? cursor : "";
    }

    public async Task SetAsync(string providerId, string cursor, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAllUnsafeAsync(ct);
            all[providerId] = cursor ?? "";
            await WriteAllUnsafeAsync(all, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadAllUnsafeAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> ReadAllUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                stream, Protocol.Json.Options, ct);
            return parsed ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAllUnsafeAsync(Dictionary<string, string> all, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporary = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, all, Protocol.Json.Options, ct);
            }
            UnixFilePermissions.RestrictFile(temporary);
            File.Move(temporary, _filePath, overwrite: true);
            UnixFilePermissions.RestrictFile(_filePath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
