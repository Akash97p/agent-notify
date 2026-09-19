using System.Globalization;
using System.Text.Json;
using AgentNotify.Protocol;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core.Router;

/// <summary>SQLite persistence for router configuration and ledger.</summary>
public sealed class RouterRepository
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public RouterRepository(string dbPath)
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
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS router_upstreams (
                id            TEXT PRIMARY KEY,
                slug          TEXT NOT NULL,
                label         TEXT NOT NULL,
                wire          TEXT NOT NULL,
                base_url      TEXT NOT NULL,
                encrypted_key TEXT,
                models        TEXT NOT NULL,
                enabled       INTEGER NOT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_router_upstreams_slug ON router_upstreams(slug);
            CREATE TABLE IF NOT EXISTS router_routes (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                kind       TEXT NOT NULL,
                targets    TEXT NOT NULL,
                enabled    INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_router_routes_name ON router_routes(name);
            CREATE TABLE IF NOT EXISTS router_settings (
                id            INTEGER PRIMARY KEY CHECK (id = 1),
                default_route TEXT
            );
            INSERT OR IGNORE INTO router_settings(id, default_route) VALUES (1, NULL);
            CREATE TABLE IF NOT EXISTS router_requests (
                id                  TEXT PRIMARY KEY,
                started_at          TEXT NOT NULL,
                finished_at         TEXT,
                inbound_wire        TEXT NOT NULL,
                requested_model     TEXT,
                route_kind          TEXT,
                route_name          TEXT,
                upstream_slug       TEXT,
                model               TEXT,
                stream              INTEGER NOT NULL,
                status              INTEGER,
                outcome             TEXT,
                input_tokens        INTEGER,
                cached_input_tokens INTEGER,
                output_tokens       INTEGER,
                reasoning_tokens    INTEGER,
                usage_status        TEXT,
                error_code          TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_router_requests_started_at ON router_requests(started_at);
            CREATE TABLE IF NOT EXISTS router_attempts (
                request_id     TEXT NOT NULL,
                ordinal        INTEGER NOT NULL,
                upstream_slug  TEXT NOT NULL,
                model          TEXT NOT NULL,
                upstream_wire  TEXT NOT NULL,
                status         INTEGER,
                error_code     TEXT,
                duration_ms    INTEGER,
                bytes_streamed INTEGER NOT NULL,
                started_at     TEXT NOT NULL,
                PRIMARY KEY (request_id, ordinal),
                FOREIGN KEY (request_id) REFERENCES router_requests(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_router_attempts_request_id ON router_attempts(request_id);
            CREATE TABLE IF NOT EXISTS router_effort_mappings (
                upstream_id     TEXT NOT NULL,
                model           TEXT NOT NULL,
                supported_values TEXT NOT NULL,
                level_map       TEXT NOT NULL,
                default_value   TEXT,
                PRIMARY KEY (upstream_id, model),
                FOREIGN KEY (upstream_id) REFERENCES router_upstreams(id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
        await AddColumnIfMissingAsync(connection, "router_upstreams", "auth", "TEXT NOT NULL DEFAULT 'api_key'", ct);
        await AddColumnIfMissingAsync(connection, "router_upstreams", "model_wires", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "router_upstreams", "credential_ref", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "router_settings", "smart_routing", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "router_settings", "switch_strategy", "TEXT NOT NULL DEFAULT 'off'", ct);
        await AddColumnIfMissingAsync(connection, "router_settings", "claude_fallback_route", "TEXT", ct);
        await using (var migrate = connection.CreateCommand())
        {
            migrate.CommandText = "UPDATE router_settings SET switch_strategy = 'ordered' WHERE smart_routing = 1 AND switch_strategy = 'off'";
            await migrate.ExecuteNonQueryAsync(ct);
        }
        UnixFilePermissions.RestrictFile(_dbPath);
    }

    /// <summary>Adds a column an older database lacks. Existing rows keep every value they had.</summary>
    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string table, string column,
        string definition, CancellationToken ct)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $name";
            probe.Parameters.AddWithValue("$name", column);
            if (await probe.ExecuteScalarAsync(ct) is not null) return;
        }
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        try { await alter.ExecuteNonQueryAsync(ct); }
        // Two initializations racing: the other one already added it.
        catch (SqliteException error) when (error.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }


    public async Task<IReadOnlyList<StoredRouterUpstream>> ListUpstreamsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectUpstreamColumns + " ORDER BY created_at ASC";
        var results = new List<StoredRouterUpstream>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadUpstream(reader));
        return results;
    }

    public async Task<StoredRouterUpstream?> GetUpstreamAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectUpstreamColumns + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUpstream(reader) : null;
    }

    public async Task<StoredRouterUpstream?> GetUpstreamBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectUpstreamColumns + " WHERE slug = $slug";
        command.Parameters.AddWithValue("$slug", slug);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadUpstream(reader) : null;
    }

    public async Task InsertUpstreamAsync(StoredRouterUpstream upstream, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO router_upstreams (id, slug, label, wire, base_url, encrypted_key, models, enabled, created_at, updated_at, auth, model_wires, credential_ref)
            VALUES ($id, $slug, $label, $wire, $baseUrl, $key, $models, $enabled, $createdAt, $updatedAt, $auth, $modelWires, $credentialRef);
            """;
        BindUpstream(command, upstream);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateUpstreamAsync(StoredRouterUpstream upstream, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE router_upstreams SET
                slug = $slug, label = $label, wire = $wire, base_url = $baseUrl,
                encrypted_key = $key, models = $models, enabled = $enabled,
                created_at = $createdAt, updated_at = $updatedAt, auth = $auth, model_wires = $modelWires,
                credential_ref = $credentialRef
            WHERE id = $id;
            """;
        BindUpstream(command, upstream);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteUpstreamAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM router_upstreams WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }


    public async Task<IReadOnlyList<RouterRoute>> ListRoutesAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectRouteColumns + " ORDER BY created_at ASC";
        var results = new List<RouterRoute>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRoute(reader));
        return results;
    }

    public async Task<RouterRoute?> GetRouteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectRouteColumns + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRoute(reader) : null;
    }

    public async Task<RouterRoute?> GetRouteByNameAsync(string name, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectRouteColumns + " WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRoute(reader) : null;
    }

    public async Task InsertRouteAsync(RouterRoute route, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO router_routes (id, name, kind, targets, enabled, created_at, updated_at)
            VALUES ($id, $name, $kind, $targets, $enabled, $createdAt, $updatedAt);
            """;
        BindRoute(command, route);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRouteAsync(RouterRoute route, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE router_routes SET
                name = $name, kind = $kind, targets = $targets, enabled = $enabled,
                created_at = $createdAt, updated_at = $updatedAt
            WHERE id = $id;
            """;
        BindRoute(command, route);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteRouteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM router_routes WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }


    public async Task<RouterSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT default_route, switch_strategy, claude_fallback_route FROM router_settings WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return new RouterSettings(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                RouterSwitchStrategy.Normalize(reader.IsDBNull(1) ? null : reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        return new RouterSettings(null);
    }

    public async Task SetSettingsAsync(RouterSettings settings, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE router_settings SET default_route = $route, smart_routing = $smart, switch_strategy = $strategy, claude_fallback_route = $claudeFallback WHERE id = 1";
        command.Parameters.AddWithValue("$route", settings.DefaultRoute is null ? DBNull.Value : settings.DefaultRoute);
        command.Parameters.AddWithValue("$smart", settings.SmartRouting ? 1 : 0);
        command.Parameters.AddWithValue("$strategy", RouterSwitchStrategy.Normalize(settings.SwitchStrategy));
        command.Parameters.AddWithValue("$claudeFallback", settings.ClaudeFallbackRoute is null ? DBNull.Value : settings.ClaudeFallbackRoute);
        await command.ExecuteNonQueryAsync(ct);
    }


    public async Task<IReadOnlyList<RouterEffortMapping>> ListEffortMappingsAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT upstream_id, model, supported_values, level_map, default_value FROM router_effort_mappings ORDER BY upstream_id, model";
        var results = new List<RouterEffortMapping>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RouterEffortMapping(
                reader.GetString(0), reader.GetString(1),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(2), Json.Options) ?? [],
                JsonSerializer.Deserialize<List<string>>(reader.GetString(3), Json.Options) ?? [],
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return results;
    }

    public async Task SetEffortMappingAsync(RouterEffortMapping mapping, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO router_effort_mappings(upstream_id, model, supported_values, level_map, default_value) VALUES ($upstream, $model, $supported, $map, $default) " +
            "ON CONFLICT(upstream_id, model) DO UPDATE SET supported_values = excluded.supported_values, level_map = excluded.level_map, default_value = excluded.default_value";
        command.Parameters.AddWithValue("$upstream", mapping.UpstreamId);
        command.Parameters.AddWithValue("$model", mapping.Model);
        command.Parameters.AddWithValue("$supported", JsonSerializer.Serialize(mapping.SupportedValues, Json.Options));
        command.Parameters.AddWithValue("$map", JsonSerializer.Serialize(mapping.LevelMap, Json.Options));
        command.Parameters.AddWithValue("$default", mapping.DefaultValue is null ? DBNull.Value : mapping.DefaultValue);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteEffortMappingAsync(string upstreamId, string model, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM router_effort_mappings WHERE upstream_id = $upstream AND model = $model";
        command.Parameters.AddWithValue("$upstream", upstreamId);
        command.Parameters.AddWithValue("$model", model);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task InsertRequestAsync(RouterRequestRecord request, IReadOnlyList<RouterAttemptRecord> attempts, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO router_requests
                    (id, started_at, finished_at, inbound_wire, requested_model, route_kind, route_name,
                     upstream_slug, model, stream, status, outcome,
                     input_tokens, cached_input_tokens, output_tokens, reasoning_tokens,
                     usage_status, error_code)
                VALUES
                    ($id, $startedAt, $finishedAt, $inboundWire, $requestedModel, $routeKind, $routeName,
                     $upstreamSlug, $model, $stream, $status, $outcome,
                     $inputTokens, $cachedInputTokens, $outputTokens, $reasoningTokens,
                     $usageStatus, $errorCode);
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$startedAt", request.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$finishedAt", request.FinishedAt.HasValue ? request.FinishedAt.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$inboundWire", request.InboundWire);
            cmd.Parameters.AddWithValue("$requestedModel", request.RequestedModel is null ? DBNull.Value : request.RequestedModel);
            cmd.Parameters.AddWithValue("$routeKind", request.RouteKind is null ? DBNull.Value : request.RouteKind);
            cmd.Parameters.AddWithValue("$routeName", request.RouteName is null ? DBNull.Value : request.RouteName);
            cmd.Parameters.AddWithValue("$upstreamSlug", request.UpstreamSlug is null ? DBNull.Value : request.UpstreamSlug);
            cmd.Parameters.AddWithValue("$model", request.Model is null ? DBNull.Value : request.Model);
            cmd.Parameters.AddWithValue("$stream", request.Stream ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", request.Status.HasValue ? request.Status.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", request.Outcome is null ? DBNull.Value : request.Outcome);
            cmd.Parameters.AddWithValue("$inputTokens", request.InputTokens.HasValue ? request.InputTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$cachedInputTokens", request.CachedInputTokens.HasValue ? request.CachedInputTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$outputTokens", request.OutputTokens.HasValue ? request.OutputTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$reasoningTokens", request.ReasoningTokens.HasValue ? request.ReasoningTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$usageStatus", request.UsageStatus is null ? DBNull.Value : request.UsageStatus);
            cmd.Parameters.AddWithValue("$errorCode", request.ErrorCode is null ? DBNull.Value : request.ErrorCode);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var attempt in attempts)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO router_attempts
                    (request_id, ordinal, upstream_slug, model, upstream_wire, status, error_code, duration_ms, bytes_streamed, started_at)
                VALUES
                    ($requestId, $ordinal, $upstreamSlug, $model, $upstreamWire, $status, $errorCode, $durationMs, $bytesStreamed, $startedAt);
                """;
            cmd.Parameters.AddWithValue("$requestId", attempt.RequestId);
            cmd.Parameters.AddWithValue("$ordinal", attempt.Ordinal);
            cmd.Parameters.AddWithValue("$upstreamSlug", attempt.UpstreamSlug);
            cmd.Parameters.AddWithValue("$model", attempt.Model);
            cmd.Parameters.AddWithValue("$upstreamWire", attempt.UpstreamWire);
            cmd.Parameters.AddWithValue("$status", attempt.Status.HasValue ? attempt.Status.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$errorCode", attempt.ErrorCode is null ? DBNull.Value : attempt.ErrorCode);
            cmd.Parameters.AddWithValue("$durationMs", attempt.DurationMs.HasValue ? attempt.DurationMs.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$bytesStreamed", attempt.BytesStreamed ? 1 : 0);
            cmd.Parameters.AddWithValue("$startedAt", attempt.StartedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<RouterLedgerEntry>> ListRecentRequestsAsync(int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = Open();
        var requests = new List<RouterRequestRecord>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SelectRequestColumns + " ORDER BY started_at DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                requests.Add(ReadRequest(reader));
        }
        if (requests.Count == 0) return [];
        var entries = new List<RouterLedgerEntry>();
        foreach (var req in requests)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT request_id, ordinal, upstream_slug, model, upstream_wire, status, error_code, duration_ms, bytes_streamed, started_at
                FROM router_attempts WHERE request_id = $id ORDER BY ordinal ASC;
                """;
            cmd.Parameters.AddWithValue("$id", req.Id);
            var attempts = new List<RouterAttemptRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                attempts.Add(ReadAttempt(reader));
            entries.Add(new RouterLedgerEntry(req, attempts));
        }
        return entries;
    }

    public async Task<IReadOnlyList<RouterUsageSummary>> SummarizeAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT upstream_slug, model,
                   COUNT(*) as cnt,
                   SUM(CASE WHEN outcome = 'ok' THEN 1 ELSE 0 END) as ok_cnt,
                   COALESCE(SUM(input_tokens), 0),
                   COALESCE(SUM(cached_input_tokens), 0),
                   COALESCE(SUM(output_tokens), 0),
                   COALESCE(SUM(reasoning_tokens), 0)
            FROM router_requests
            WHERE started_at >= $since AND upstream_slug IS NOT NULL AND model IS NOT NULL
            GROUP BY upstream_slug, model
            ORDER BY upstream_slug, model;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        var results = new List<RouterUsageSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RouterUsageSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }
        return results;
    }

    public async Task<int> PruneLedgerAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM router_requests WHERE started_at < $olderThan";
        command.Parameters.AddWithValue("$olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void BindUpstream(SqliteCommand command, StoredRouterUpstream u)
    {
        command.Parameters.AddWithValue("$id", u.Id);
        command.Parameters.AddWithValue("$slug", u.Slug);
        command.Parameters.AddWithValue("$label", u.Label);
        command.Parameters.AddWithValue("$wire", u.Wire);
        command.Parameters.AddWithValue("$baseUrl", u.BaseUrl);
        command.Parameters.AddWithValue("$key", u.EncryptedKey is null ? DBNull.Value : u.EncryptedKey);
        command.Parameters.AddWithValue("$models", JsonSerializer.Serialize(u.Models, Json.Options));
        command.Parameters.AddWithValue("$enabled", u.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", u.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", u.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$auth", u.Auth);
        command.Parameters.AddWithValue("$modelWires", u.ModelWires.Count == 0
            ? DBNull.Value
            : JsonSerializer.Serialize(u.ModelWires, Json.Options));
        command.Parameters.AddWithValue("$credentialRef", (object?)u.CredentialRef ?? DBNull.Value);
    }

    private static void BindRoute(SqliteCommand command, RouterRoute r)
    {
        command.Parameters.AddWithValue("$id", r.Id);
        command.Parameters.AddWithValue("$name", r.Name);
        command.Parameters.AddWithValue("$kind", r.Kind);
        command.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(r.Targets, Json.Options));
        command.Parameters.AddWithValue("$enabled", r.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", r.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", r.UpdatedAt.ToString("O"));
    }

    private static StoredRouterUpstream ReadUpstream(SqliteDataReader reader) => new StoredRouterUpstream(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(6), Json.Options) ?? [],
        reader.GetInt32(7) != 0,
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
    {
        Auth = reader.GetString(10),
        ModelWires = reader.IsDBNull(11)
            ? RouterUpstreamWires.None
            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(11), Json.Options) ?? RouterUpstreamWires.None,
        CredentialRef = reader.IsDBNull(12) ? null : reader.GetString(12)
    };

    private static RouterRoute ReadRoute(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(3), Json.Options) ?? [],
        reader.GetInt32(4) != 0,
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static RouterRequestRecord ReadRequest(SqliteDataReader reader) => new(
        reader.GetString(0),
        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt32(9) != 0,
        reader.IsDBNull(10) ? null : reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetInt64(12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetInt64(14),
        reader.IsDBNull(15) ? null : reader.GetInt64(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.IsDBNull(17) ? null : reader.GetString(17));

    private static RouterAttemptRecord ReadAttempt(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt32(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.GetInt32(8) != 0,
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private const string SelectUpstreamColumns =
        "SELECT id, slug, label, wire, base_url, encrypted_key, models, enabled, created_at, updated_at, auth, model_wires, credential_ref FROM router_upstreams";
    private const string SelectRouteColumns =
        "SELECT id, name, kind, targets, enabled, created_at, updated_at FROM router_routes";
    private const string SelectRequestColumns =
        "SELECT id, started_at, finished_at, inbound_wire, requested_model, route_kind, route_name, upstream_slug, model, stream, status, outcome, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens, usage_status, error_code FROM router_requests";
}
