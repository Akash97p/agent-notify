using System.Globalization;
using System.Text.Json;
using AgentNotify.Protocol;
using AgentNotify.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core.Persistence;

/// <summary>SQLite-backed implementation of <see cref="IInteractionRepository"/>.
/// Shares the broker database file with notifications and delivery tables.</summary>
public sealed class SqliteInteractionRepository : IInteractionRepository
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteInteractionRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("dbPath is required", nameof(dbPath));
        _dbPath = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS interactions (
                id               TEXT PRIMARY KEY,
                key              TEXT,
                agent            TEXT NOT NULL,
                agent_instance   TEXT,
                project          TEXT,
                session_id       TEXT,
                turn_id          TEXT,
                native_request_id TEXT,
                kind             TEXT NOT NULL,
                prompt           TEXT NOT NULL,
                choices_json     TEXT NOT NULL,
                text_max_length  INTEGER NOT NULL,
                status           TEXT NOT NULL,
                request_digest   TEXT NOT NULL,
                nonce            TEXT NOT NULL,
                created_at       TEXT NOT NULL,
                updated_at       TEXT NOT NULL,
                expires_at       TEXT NOT NULL,
                answered_at      TEXT,
                response_json    TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_interactions_status ON interactions(status);
            CREATE INDEX IF NOT EXISTS idx_interactions_key ON interactions(key);
            CREATE INDEX IF NOT EXISTS idx_interactions_session ON interactions(session_id);
            CREATE INDEX IF NOT EXISTS idx_interactions_expires ON interactions(expires_at);
            """;
        await command.ExecuteNonQueryAsync(ct);

        UnixFilePermissions.RestrictFile(_dbPath);
    }

    public async Task<Interaction> CreateAsync(Interaction n, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO interactions
                (id, key, agent, agent_instance, project, session_id, turn_id, native_request_id,
                 kind, prompt, choices_json, text_max_length, status, request_digest, nonce,
                 created_at, updated_at, expires_at, answered_at, response_json)
            VALUES
                ($id, $key, $agent, $agentInstance, $project, $sessionId, $turnId, $nativeRequestId,
                 $kind, $prompt, $choices, $textMax, $status, $digest, $nonce,
                 $createdAt, $updatedAt, $expiresAt, $answeredAt, $response);
            """;
        Bind(command, n);
        await command.ExecuteNonQueryAsync(ct);
        return n;
    }

    public async Task<Interaction?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadInteraction(reader) : null;
    }

    public async Task<Interaction?> FindPendingByKeyAsync(string key, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE key = $key AND status = $status ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$status", nameof(InteractionStatus.Pending));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadInteraction(reader) : null;
    }

    public async Task<Interaction?> FindLatestByKeyAsync(string key, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE key = $key ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadInteraction(reader) : null;
    }

    public async Task<IReadOnlyList<Interaction>> QueryAsync(InteractionQuery query, CancellationToken ct = default)
    {
        var where = new List<string>();
        var parameters = new List<SqliteParameter>();

        var status = query.Status;
        if (query.PendingOnly == true)
            status = InteractionStatus.Pending;
        if (status is { } s)
        {
            where.Add("status = $status");
            parameters.Add(new SqliteParameter("$status", s.ToString()));
        }
        if (!string.IsNullOrWhiteSpace(query.Agent))
        {
            where.Add("agent = $agent");
            parameters.Add(new SqliteParameter("$agent", query.Agent.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            where.Add("project = $project");
            parameters.Add(new SqliteParameter("$project", query.Project.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Add("session_id = $sessionId");
            parameters.Add(new SqliteParameter("$sessionId", query.SessionId.Trim()));
        }

        var limit = Math.Clamp(query.Limit, 1, 500);
        var sql = SelectColumns;
        if (where.Count > 0)
            sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY created_at DESC LIMIT $limit";

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Interaction>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadInteraction(reader));
        return results;
    }

    public async Task<Interaction?> UpdateAsync(Interaction n, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE interactions SET
                key = $key, agent = $agent, agent_instance = $agentInstance,
                project = $project, session_id = $sessionId, turn_id = $turnId,
                native_request_id = $nativeRequestId, kind = $kind, prompt = $prompt,
                choices_json = $choices, text_max_length = $textMax, status = $status,
                request_digest = $digest, nonce = $nonce,
                created_at = $createdAt, updated_at = $updatedAt,
                expires_at = $expiresAt, answered_at = $answeredAt, response_json = $response
            WHERE id = $id;
            """;
        Bind(command, n);
        var affected = await command.ExecuteNonQueryAsync(ct);
        return affected > 0 ? await GetByIdAsync(n.Id, ct) : null;
    }

    public async Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE interactions SET status = $expired, updated_at = $now
            WHERE status = $pending AND expires_at <= $now;
            """;
        command.Parameters.AddWithValue("$expired", nameof(InteractionStatus.Expired));
        command.Parameters.AddWithValue("$pending", nameof(InteractionStatus.Pending));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM interactions WHERE status != $pending AND updated_at < $olderThan";
        command.Parameters.AddWithValue("$pending", nameof(InteractionStatus.Pending));
        command.Parameters.AddWithValue("$olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, Interaction n)
    {
        command.Parameters.AddWithValue("$id", n.Id);
        command.Parameters.AddWithValue("$key", (object?)n.Key ?? DBNull.Value);
        command.Parameters.AddWithValue("$agent", n.Agent);
        command.Parameters.AddWithValue("$agentInstance", (object?)n.AgentInstance ?? DBNull.Value);
        command.Parameters.AddWithValue("$project", (object?)n.Project ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionId", (object?)n.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$turnId", (object?)n.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$nativeRequestId", (object?)n.NativeRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", n.Kind.ToString());
        command.Parameters.AddWithValue("$prompt", n.Prompt);
        command.Parameters.AddWithValue("$choices", JsonSerializer.Serialize(n.Choices, Protocol.Json.Options));
        command.Parameters.AddWithValue("$textMax", n.TextMaxLength);
        command.Parameters.AddWithValue("$status", n.Status.ToString());
        command.Parameters.AddWithValue("$digest", n.RequestDigest);
        command.Parameters.AddWithValue("$nonce", n.Nonce);
        command.Parameters.AddWithValue("$createdAt", n.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", n.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$expiresAt", n.ExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("$answeredAt", n.AnsweredAt.HasValue ? (object)n.AnsweredAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$response", n.Response is null
            ? DBNull.Value
            : JsonSerializer.Serialize(n.Response, Protocol.Json.Options));
    }

    private static Interaction ReadInteraction(SqliteDataReader reader)
    {
        List<InteractionChoice> choices;
        try
        {
            choices = JsonSerializer.Deserialize<List<InteractionChoice>>(reader.GetString(10), Protocol.Json.Options) ?? [];
        }
        catch (JsonException)
        {
            choices = [];
        }

        InteractionResponse? response = null;
        if (!reader.IsDBNull(19))
        {
            try
            {
                response = JsonSerializer.Deserialize<InteractionResponse>(reader.GetString(19), Protocol.Json.Options);
            }
            catch (JsonException)
            {
                response = null;
            }
        }

        return new Interaction
        {
            Id = reader.GetString(0),
            Key = reader.IsDBNull(1) ? null : reader.GetString(1),
            Agent = reader.GetString(2),
            AgentInstance = reader.IsDBNull(3) ? null : reader.GetString(3),
            Project = reader.IsDBNull(4) ? null : reader.GetString(4),
            SessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
            TurnId = reader.IsDBNull(6) ? null : reader.GetString(6),
            NativeRequestId = reader.IsDBNull(7) ? null : reader.GetString(7),
            Kind = Enum.Parse<InteractionKind>(reader.GetString(8)),
            Prompt = reader.GetString(9),
            Choices = choices,
            TextMaxLength = reader.GetInt32(11),
            Status = Enum.Parse<InteractionStatus>(reader.GetString(12)),
            RequestDigest = reader.GetString(13),
            Nonce = reader.GetString(14),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ExpiresAt = DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            AnsweredAt = reader.IsDBNull(18)
                ? null
                : DateTimeOffset.Parse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Response = response
        };
    }

    private const string SelectColumns =
        "SELECT id, key, agent, agent_instance, project, session_id, turn_id, native_request_id, kind, prompt, choices_json, text_max_length, status, request_digest, nonce, created_at, updated_at, expires_at, answered_at, response_json FROM interactions";
}
