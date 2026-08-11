using System.Globalization;
using System.Text.Json;
using AgentNotify.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Persists outbound provider profiles, routes, queued deliveries, and attempt history.
/// Secret values reach this class only as an encrypted envelope.
/// </summary>
public sealed class SqliteDeliveryRepository : IDeliveryRepository
{
    private const int CurrentSchemaVersion = 1;
    private const string ProviderColumns =
        "id, name, kind, enabled, config_json, encrypted_secrets, secret_names_json, created_at, updated_at";
    private const string OutboxColumns =
        "id, notification_id, route_id, provider_id, payload_json, status, attempt_count, next_attempt_at, created_at, updated_at";

    private readonly string _connectionString;

    public SqliteDeliveryRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is required.", nameof(dbPath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using (var bootstrap = connection.CreateCommand())
        {
            bootstrap.CommandText =
                "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
            await bootstrap.ExecuteNonQueryAsync(ct);
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"The delivery database schema is version {version}, but this build supports up to {CurrentSchemaVersion}.");
        if (version == CurrentSchemaVersion)
            return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS provider_profiles (
                id                 TEXT PRIMARY KEY,
                name               TEXT NOT NULL,
                kind               TEXT NOT NULL,
                enabled            INTEGER NOT NULL,
                config_json        TEXT NOT NULL,
                encrypted_secrets  TEXT NOT NULL,
                secret_names_json  TEXT NOT NULL,
                created_at         TEXT NOT NULL,
                updated_at         TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS delivery_routes (
                id                 TEXT PRIMARY KEY,
                name               TEXT NOT NULL,
                provider_id        TEXT NOT NULL,
                enabled            INTEGER NOT NULL,
                minimum_priority   TEXT NOT NULL,
                type_id            TEXT,
                project            TEXT,
                agent              TEXT,
                include_message    INTEGER NOT NULL,
                created_at         TEXT NOT NULL,
                updated_at         TEXT NOT NULL,
                FOREIGN KEY(provider_id) REFERENCES provider_profiles(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS delivery_outbox (
                id                 TEXT PRIMARY KEY,
                notification_id    TEXT NOT NULL,
                route_id           TEXT NOT NULL,
                provider_id        TEXT NOT NULL,
                payload_json       TEXT NOT NULL,
                status             TEXT NOT NULL,
                attempt_count      INTEGER NOT NULL,
                next_attempt_at    TEXT NOT NULL,
                created_at         TEXT NOT NULL,
                updated_at         TEXT NOT NULL,
                FOREIGN KEY(route_id) REFERENCES delivery_routes(id) ON DELETE CASCADE,
                FOREIGN KEY(provider_id) REFERENCES provider_profiles(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS delivery_attempts (
                id                 TEXT PRIMARY KEY,
                outbox_id          TEXT NOT NULL,
                attempt_number     INTEGER NOT NULL,
                succeeded          INTEGER NOT NULL,
                status_code        INTEGER,
                error_code         TEXT,
                started_at         TEXT NOT NULL,
                completed_at       TEXT NOT NULL,
                FOREIGN KEY(outbox_id) REFERENCES delivery_outbox(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_outbox_due
                ON delivery_outbox(status, next_attempt_at);
            CREATE INDEX IF NOT EXISTS idx_attempts_outbox
                ON delivery_attempts(outbox_id, attempt_number);
            INSERT INTO schema_migrations(version, applied_at)
                VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task UpsertProviderAsync(StoredProviderProfile profile, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO provider_profiles
                (id, name, kind, enabled, config_json, encrypted_secrets, secret_names_json, created_at, updated_at)
            VALUES
                ($id, $name, $kind, $enabled, $config, $secrets, $secretNames, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                kind = excluded.kind,
                enabled = excluded.enabled,
                config_json = excluded.config_json,
                encrypted_secrets = excluded.encrypted_secrets,
                secret_names_json = excluded.secret_names_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$kind", profile.Kind);
        command.Parameters.AddWithValue("$enabled", profile.Enabled);
        command.Parameters.AddWithValue("$config", profile.ConfigJson);
        command.Parameters.AddWithValue("$secrets", profile.EncryptedSecrets);
        command.Parameters.AddWithValue(
            "$secretNames",
            JsonSerializer.Serialize(profile.SecretNames, Json.Options));
        command.Parameters.AddWithValue("$created", DatabaseTime(profile.CreatedAt));
        command.Parameters.AddWithValue("$updated", DatabaseTime(profile.UpdatedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredProviderProfile?> GetProviderAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProviderColumns} FROM provider_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProvider(reader) : null;
    }

    public async Task<IReadOnlyList<ProviderProfile>> ListProvidersAsync(CancellationToken ct = default)
    {
        var providers = new List<ProviderProfile>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ProviderColumns} FROM provider_profiles ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            providers.Add(ToPublicProfile(ReadProvider(reader)));
        return providers;
    }

    public async Task DeleteProviderAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM provider_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertRouteAsync(DeliveryRoute route, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO delivery_routes
                (id, name, provider_id, enabled, minimum_priority, type_id, project, agent,
                 include_message, created_at, updated_at)
            VALUES
                ($id, $name, $provider, $enabled, $priority, $type, $project, $agent,
                 $include, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                provider_id = excluded.provider_id,
                enabled = excluded.enabled,
                minimum_priority = excluded.minimum_priority,
                type_id = excluded.type_id,
                project = excluded.project,
                agent = excluded.agent,
                include_message = excluded.include_message,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", route.Id);
        command.Parameters.AddWithValue("$name", route.Name);
        command.Parameters.AddWithValue("$provider", route.ProviderId);
        command.Parameters.AddWithValue("$enabled", route.Enabled);
        command.Parameters.AddWithValue("$priority", route.MinimumPriority.ToString());
        command.Parameters.AddWithValue("$type", DatabaseValue(route.TypeId));
        command.Parameters.AddWithValue("$project", DatabaseValue(route.Project));
        command.Parameters.AddWithValue("$agent", DatabaseValue(route.Agent));
        command.Parameters.AddWithValue("$include", route.IncludeMessage);
        command.Parameters.AddWithValue("$created", DatabaseTime(route.CreatedAt));
        command.Parameters.AddWithValue("$updated", DatabaseTime(route.UpdatedAt));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DeliveryRoute>> ListRoutesAsync(CancellationToken ct = default)
    {
        var routes = new List<DeliveryRoute>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, provider_id, enabled, minimum_priority, type_id, project, agent,
                   include_message, created_at, updated_at
            FROM delivery_routes
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            routes.Add(new DeliveryRoute
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                ProviderId = reader.GetString(2),
                Enabled = reader.GetBoolean(3),
                MinimumPriority = Enum.Parse<NotificationPriority>(reader.GetString(4)),
                TypeId = ReadNullableText(reader, 5),
                Project = ReadNullableText(reader, 6),
                Agent = ReadNullableText(reader, 7),
                IncludeMessage = reader.GetBoolean(8),
                CreatedAt = ReadTime(reader, 9),
                UpdatedAt = ReadTime(reader, 10)
            });
        }
        return routes;
    }

    public async Task EnqueueAsync(OutboxItem item, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO delivery_outbox
                (id, notification_id, route_id, provider_id, payload_json, status,
                 attempt_count, next_attempt_at, created_at, updated_at)
            VALUES
                ($id, $notification, $route, $provider, $payload, $status,
                 $count, $next, $created, $updated);
            """;
        BindOutbox(command, item);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<OutboxItem?> ClaimDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE delivery_outbox
            SET status = 'Processing', updated_at = $now
            WHERE id = (
                SELECT id
                FROM delivery_outbox
                WHERE status IN ('Pending', 'Retry') AND next_attempt_at <= $now
                ORDER BY next_attempt_at, created_at
                LIMIT 1
            )
            AND status IN ('Pending', 'Retry')
            RETURNING {OutboxColumns};
            """;
        command.Parameters.AddWithValue("$now", DatabaseTime(now));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadOutbox(reader) : null;
    }

    public async Task CompleteAttemptAsync(
        OutboxItem item,
        DeliveryAttempt attempt,
        CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO delivery_attempts
                    (id, outbox_id, attempt_number, succeeded, status_code, error_code, started_at, completed_at)
                VALUES
                    ($id, $outbox, $number, $success, $code, $error, $started, $completed);
                """;
            command.Parameters.AddWithValue("$id", attempt.Id);
            command.Parameters.AddWithValue("$outbox", attempt.OutboxId);
            command.Parameters.AddWithValue("$number", attempt.AttemptNumber);
            command.Parameters.AddWithValue("$success", attempt.Succeeded);
            command.Parameters.AddWithValue(
                "$code",
                attempt.StatusCode.HasValue ? attempt.StatusCode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$error", DatabaseValue(attempt.ErrorCode));
            command.Parameters.AddWithValue("$started", DatabaseTime(attempt.StartedAt));
            command.Parameters.AddWithValue("$completed", DatabaseTime(attempt.CompletedAt));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE delivery_outbox
                SET status = $status,
                    attempt_count = $count,
                    next_attempt_at = $next,
                    updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$status", item.Status.ToString());
            command.Parameters.AddWithValue("$count", item.AttemptCount);
            command.Parameters.AddWithValue("$next", DatabaseTime(item.NextAttemptAt));
            command.Parameters.AddWithValue("$updated", DatabaseTime(item.UpdatedAt));
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DeliveryAttempt>> ListAttemptsAsync(
        string outboxId,
        CancellationToken ct = default)
    {
        var attempts = new List<DeliveryAttempt>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, outbox_id, attempt_number, succeeded, status_code, error_code,
                   started_at, completed_at
            FROM delivery_attempts
            WHERE outbox_id = $id
            ORDER BY attempt_number;
            """;
        command.Parameters.AddWithValue("$id", outboxId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            attempts.Add(new DeliveryAttempt
            {
                Id = reader.GetString(0),
                OutboxId = reader.GetString(1),
                AttemptNumber = reader.GetInt32(2),
                Succeeded = reader.GetBoolean(3),
                StatusCode = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                ErrorCode = ReadNullableText(reader, 5),
                StartedAt = ReadTime(reader, 6),
                CompletedAt = ReadTime(reader, 7)
            });
        }
        return attempts;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static StoredProviderProfile ReadProvider(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        Kind = reader.GetString(2),
        Enabled = reader.GetBoolean(3),
        ConfigJson = reader.GetString(4),
        EncryptedSecrets = reader.GetString(5),
        SecretNames = JsonSerializer.Deserialize<List<string>>(reader.GetString(6), Json.Options) ?? [],
        CreatedAt = ReadTime(reader, 7),
        UpdatedAt = ReadTime(reader, 8)
    };

    private static ProviderProfile ToPublicProfile(StoredProviderProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Kind = profile.Kind,
        Enabled = profile.Enabled,
        ConfigJson = profile.ConfigJson,
        SecretNames = profile.SecretNames,
        CreatedAt = profile.CreatedAt,
        UpdatedAt = profile.UpdatedAt
    };

    private static OutboxItem ReadOutbox(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        NotificationId = reader.GetString(1),
        RouteId = reader.GetString(2),
        ProviderId = reader.GetString(3),
        PayloadJson = reader.GetString(4),
        Status = Enum.Parse<OutboxStatus>(reader.GetString(5)),
        AttemptCount = reader.GetInt32(6),
        NextAttemptAt = ReadTime(reader, 7),
        CreatedAt = ReadTime(reader, 8),
        UpdatedAt = ReadTime(reader, 9)
    };

    private static void BindOutbox(SqliteCommand command, OutboxItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$notification", item.NotificationId);
        command.Parameters.AddWithValue("$route", item.RouteId);
        command.Parameters.AddWithValue("$provider", item.ProviderId);
        command.Parameters.AddWithValue("$payload", item.PayloadJson);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$count", item.AttemptCount);
        command.Parameters.AddWithValue("$next", DatabaseTime(item.NextAttemptAt));
        command.Parameters.AddWithValue("$created", DatabaseTime(item.CreatedAt));
        command.Parameters.AddWithValue("$updated", DatabaseTime(item.UpdatedAt));
    }

    private static object DatabaseValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static string DatabaseTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ReadTime(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
