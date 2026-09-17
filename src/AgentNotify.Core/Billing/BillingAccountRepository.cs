using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Core.Billing;

/// <summary>SQLite-backed API account store. Secrets reach this class only as a sealed envelope.</summary>
public sealed class BillingAccountRepository
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public BillingAccountRepository(string dbPath)
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
            CREATE TABLE IF NOT EXISTS billing_accounts (
                id            TEXT PRIMARY KEY,
                provider      TEXT NOT NULL,
                label         TEXT NOT NULL,
                encrypted_key TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_billing_accounts_provider ON billing_accounts(provider);
            """;
        await command.ExecuteNonQueryAsync(ct);

        UnixFilePermissions.RestrictFile(_dbPath);
    }

    public async Task<IReadOnlyList<StoredBillingAccount>> ListStoredAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY created_at ASC";
        var results = new List<StoredBillingAccount>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(Read(reader));
        return results;
    }

    public async Task<StoredBillingAccount?> GetStoredAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM billing_accounts";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task InsertAsync(StoredBillingAccount account, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO billing_accounts (id, provider, label, encrypted_key, created_at, updated_at)
            VALUES ($id, $provider, $label, $key, $createdAt, $updatedAt);
            """;
        Bind(command, account);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(StoredBillingAccount account, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE billing_accounts SET
                provider = $provider, label = $label, encrypted_key = $key,
                created_at = $createdAt, updated_at = $updatedAt
            WHERE id = $id;
            """;
        Bind(command, account);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM billing_accounts WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    private static void Bind(SqliteCommand command, StoredBillingAccount account)
    {
        command.Parameters.AddWithValue("$id", account.Id);
        command.Parameters.AddWithValue("$provider", account.Provider);
        command.Parameters.AddWithValue("$label", account.Label);
        command.Parameters.AddWithValue("$key", account.EncryptedKey);
        command.Parameters.AddWithValue("$createdAt", account.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", account.UpdatedAt.ToString("O"));
    }

    private static StoredBillingAccount Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private const string SelectColumns =
        "SELECT id, provider, label, encrypted_key, created_at, updated_at FROM billing_accounts";
}
