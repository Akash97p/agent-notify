using System.Globalization;
using Microsoft.Data.Sqlite;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;

namespace AgentNotify.Core.Persistence;

/// <summary>SQLite-backed implementation of <see cref="INotificationRepository"/>.</summary>
public sealed class SqliteNotificationRepository : INotificationRepository
{
    private const string ConnectionStringTemplate = "Data Source={0}";
    private readonly string _connectionString;

    public SqliteNotificationRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("dbPath is required", nameof(dbPath));
        _connectionString = string.Format(CultureInfo.InvariantCulture, ConnectionStringTemplate, dbPath);
    }

    private SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS notifications (
                id              TEXT PRIMARY KEY,
                key             TEXT,
                agent           TEXT NOT NULL,
                agent_instance  TEXT,
                project         TEXT,
                type            TEXT NOT NULL,
                priority        TEXT NOT NULL,
                title           TEXT NOT NULL,
                message         TEXT NOT NULL,
                cwd             TEXT,
                pid             INTEGER,
                status          TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                resolved_at     TEXT,
                metadata_json   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_notifications_status ON notifications(status);
            CREATE INDEX IF NOT EXISTS idx_notifications_key ON notifications(key);
            CREATE INDEX IF NOT EXISTS idx_notifications_created ON notifications(created_at);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Notification> CreateAsync(Notification n, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO notifications
                (id, key, agent, agent_instance, project, type, priority, title, message,
                 cwd, pid, status, created_at, updated_at, resolved_at, metadata_json)
            VALUES
                ($id, $key, $agent, $agentInstance, $project, $type, $priority, $title, $message,
                 $cwd, $pid, $status, $createdAt, $updatedAt, $resolvedAt, $metadata);
            """;
        Bind(command, n, hasUpdatedColumn: false);
        await command.ExecuteNonQueryAsync(ct);
        return n;
    }

    public async Task<Notification?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadNotification(reader) : null;
    }

    public async Task<Notification?> FindActiveByKeyAsync(string key, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE key = $key AND status = $status ORDER BY created_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$status", nameof(NotificationStatus.Active));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadNotification(reader) : null;
    }

    public async Task<IReadOnlyList<Notification>> QueryAsync(NotificationQuery query, CancellationToken ct = default)
    {
        var where = new List<string>();
        var parameters = new List<SqliteParameter>();

        var status = query.Status;
        if (query.Unresolved == true)
            status = NotificationStatus.Active;

        if (status is { } s)
        {
            where.Add("status = $status");
            parameters.Add(new SqliteParameter("$status", s.ToString()));
        }
        if (query.Type is { } t)
        {
            where.Add("type = $type");
            parameters.Add(new SqliteParameter("$type", t.ToString()));
        }
        if (!string.IsNullOrWhiteSpace(query.Project))
        {
            where.Add("project = $project");
            parameters.Add(new SqliteParameter("$project", query.Project.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(query.Agent))
        {
            where.Add("agent = $agent");
            parameters.Add(new SqliteParameter("$agent", query.Agent.Trim()));
        }

        var limit = Math.Clamp(query.Limit, 1, 500);

        var sql = SelectColumns;
        if (where.Count > 0)
            sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY created_at DESC LIMIT $limit";

        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<Notification>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadNotification(reader));
        return results;
    }

    public async Task<Notification?> UpdateAsync(Notification n, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notifications SET
                key = $key, agent = $agent, agent_instance = $agentInstance,
                project = $project, type = $type, priority = $priority,
                title = $title, message = $message, cwd = $cwd, pid = $pid,
                status = $status, created_at = $createdAt, updated_at = $updatedAt,
                resolved_at = $resolvedAt, metadata_json = $metadata
            WHERE id = $id;
            """;
        Bind(command, n, hasUpdatedColumn: false);
        var affected = await command.ExecuteNonQueryAsync(ct);
        return affected > 0 ? await GetByIdAsync(n.Id, ct) : null;
    }

    public async Task<Notification?> UpdateStatusAsync(string id, NotificationStatus status, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notifications SET
                status = $status,
                updated_at = $updatedAt,
                resolved_at = CASE WHEN $status = 'Active' THEN NULL ELSE $resolvedAt END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$resolvedAt", now.ToString("O"));
        var affected = await command.ExecuteNonQueryAsync(ct);
        return affected > 0 ? await GetByIdAsync(id, ct) : null;
    }

    public async Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notifications WHERE status = $status";
        command.Parameters.AddWithValue("$status", nameof(NotificationStatus.Active));
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = CreateConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM notifications WHERE status != $active AND updated_at < $olderThan";
        command.Parameters.AddWithValue("$active", nameof(NotificationStatus.Active));
        command.Parameters.AddWithValue("$olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, Notification n, bool hasUpdatedColumn)
    {
        command.Parameters.AddWithValue("$id", n.Id);
        command.Parameters.AddWithValue("$key", (object?)n.Key ?? DBNull.Value);
        command.Parameters.AddWithValue("$agent", n.Agent);
        command.Parameters.AddWithValue("$agentInstance", (object?)n.AgentInstance ?? DBNull.Value);
        command.Parameters.AddWithValue("$project", (object?)n.Project ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", n.Type.ToString());
        command.Parameters.AddWithValue("$priority", n.Priority.ToString());
        command.Parameters.AddWithValue("$title", n.Title);
        command.Parameters.AddWithValue("$message", n.Message);
        command.Parameters.AddWithValue("$cwd", (object?)n.Cwd ?? DBNull.Value);
        command.Parameters.AddWithValue("$pid", n.Pid.HasValue ? (object)n.Pid.Value : DBNull.Value);
        command.Parameters.AddWithValue("$status", n.Status.ToString());
        command.Parameters.AddWithValue("$createdAt", n.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", n.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$resolvedAt", n.ResolvedAt.HasValue ? (object)n.ResolvedAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$metadata", NotificationRow.SerializeMetadata(n.Metadata));
    }

    private static Notification ReadNotification(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Key = reader.IsDBNull(1) ? null : reader.GetString(1),
        Agent = reader.GetString(2),
        AgentInstance = reader.IsDBNull(3) ? null : reader.GetString(3),
        Project = reader.IsDBNull(4) ? null : reader.GetString(4),
        Type = Enum.Parse<NotificationType>(reader.GetString(5)),
        Priority = Enum.Parse<NotificationPriority>(reader.GetString(6)),
        Title = reader.GetString(7),
        Message = reader.GetString(8),
        Cwd = reader.IsDBNull(9) ? null : reader.GetString(9),
        Pid = reader.IsDBNull(10) ? null : reader.GetInt64(10),
        Status = Enum.Parse<NotificationStatus>(reader.GetString(11)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ResolvedAt = reader.IsDBNull(14)
            ? null
            : DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Metadata = NotificationRow.ParseMetadata(reader.IsDBNull(15) ? null : reader.GetString(15))
    };

    private const string SelectColumns =
        "SELECT id, key, agent, agent_instance, project, type, priority, title, message, cwd, pid, status, created_at, updated_at, resolved_at, metadata_json FROM notifications";
}
