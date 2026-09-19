using AgentNotify.Core.Config;
using AgentNotify.Insights.Usage;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Tests;

public sealed class OpenCodeGoBillingCycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"an-go-cycle-{Guid.NewGuid():N}");
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("test+0530", TimeSpan.FromMinutes(330), "test+0530", "test+0530");

    [Theory]
    [InlineData("2026-09-17T12:00:00+05:30", 5, "2026-09-05T00:00:00+05:30", "2026-10-05T00:00:00+05:30")]
    [InlineData("2026-09-17T12:00:00+05:30", 20, "2026-08-20T00:00:00+05:30", "2026-09-20T00:00:00+05:30")]
    [InlineData("2026-09-17T00:00:00+05:30", 17, "2026-09-17T00:00:00+05:30", "2026-10-17T00:00:00+05:30")]
    [InlineData("2026-02-15T09:00:00+05:30", 31, "2026-01-31T00:00:00+05:30", "2026-02-28T00:00:00+05:30")]
    [InlineData("2026-03-01T09:00:00+05:30", 31, "2026-02-28T00:00:00+05:30", "2026-03-31T00:00:00+05:30")]
    [InlineData("2028-02-29T09:00:00+05:30", 30, "2028-02-29T00:00:00+05:30", "2028-03-30T00:00:00+05:30")]
    [InlineData("2028-02-28T23:00:00+05:30", 30, "2028-01-30T00:00:00+05:30", "2028-02-29T00:00:00+05:30")]
    [InlineData("2027-01-03T09:00:00+05:30", 10, "2026-12-10T00:00:00+05:30", "2027-01-10T00:00:00+05:30")]
    public void CycleStartsAtTheLatestRenewalAndEndsAtTheNext(string now, int day, string start, string end)
    {
        var cycle = OpenCodeGoBillingCycle.Current(DateTimeOffset.Parse(now), day, India);
        Assert.Equal(DateTimeOffset.Parse(start), cycle.Start);
        Assert.Equal(DateTimeOffset.Parse(end), cycle.End);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(null)]
    public void OnlyDaysOneToThirtyOneAreRenewalDays(int? day)
    {
        Assert.False(OpenCodeGoBillingCycle.IsValidRenewalDay(day));
        var config = new AgentNotifyConfig { OpenCodeGoRenewalDay = day };
        config.ApplyDefaults();
        Assert.Null(config.OpenCodeGoRenewalDay);
    }

    [Fact]
    public async Task MonthlyWindowFollowsTheBillingCycleWhenARenewalDayIsSet()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "opencode.db");
        var now = DateTimeOffset.Parse("2026-09-17T12:00:00+05:30");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT); " +
                "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT); " +
                "INSERT INTO session VALUES ('s1', '/test/project');";
            command.ExecuteNonQuery();
            void insert(string id, DateTimeOffset at)
            {
                using var row = connection.CreateCommand();
                row.CommandText = "INSERT INTO message VALUES ($id, 's1', $time, $data)";
                row.Parameters.AddWithValue("$id", id);
                row.Parameters.AddWithValue("$time", at.ToUnixTimeMilliseconds());
                row.Parameters.AddWithValue("$data", System.Text.Json.JsonSerializer.Serialize(new
                {
                    role = "assistant", providerID = "opencode-go", modelID = "muse-spark-1.3-contributor",
                    tokens = new { input = 10_000_000, output = 0, cache = new { write = 0 } }
                }));
                row.ExecuteNonQuery();
            }
            insert("before-cycle", DateTimeOffset.Parse("2026-09-04T23:00:00+05:30"));
            insert("in-cycle", DateTimeOffset.Parse("2026-09-05T01:00:00+05:30"));
            insert("recent", now.AddHours(-1));
        }
        var usage = new LocalUsageService([], [], db);

        var rolling = await usage.GetOpenCodeGoEstimateAsync(now, zone: India);
        var monthly = rolling.Models.Single().Windows[2];
        Assert.Equal("thirty_day", monthly.Key);
        Assert.Equal(3m, monthly.ObservedUsd);
        Assert.Null(monthly.ResetsAt);
        Assert.Null(rolling.RenewalDay);

        var cycle = await usage.GetOpenCodeGoEstimateAsync(now, renewalDay: 5, zone: India);
        var billing = cycle.Models.Single().Windows[2];
        Assert.Equal("billing_cycle", billing.Key);
        Assert.Equal(2m, billing.ObservedUsd);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T00:00:00+05:30"), billing.StartsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-05T00:00:00+05:30"), billing.ResetsAt);
        Assert.Equal(5, cycle.RenewalDay);
        Assert.Null(cycle.Models.Single().Windows[0].ResetsAt);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
