using System.Text.Json;
using AgentNotify.Core.Usage;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Tests;

public sealed class LocalUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"an-usage-{Guid.NewGuid():N}");
    private static string Line(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task SessionsGroupDeduplicatedRowsAndHideProviderSessionIds()
    {
        var root = Path.Combine(_root, "claude");
        var project = Path.Combine(_root, "work", "session-project");
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        string row(string session, string id, DateTimeOffset at, int input) => Line(new
        {
            type = "assistant", timestamp = at, sessionId = session, cwd = project,
            requestId = id, message = new { id, model = "claude-opus-5", usage = new { input_tokens = input } }
        });
        const string first = "private-provider-session-first";
        const string second = "private-provider-session-second";
        var oldest = row(first, "m1", now.AddHours(-2), 10);
        File.WriteAllLines(Path.Combine(root, "one.jsonl"),
            [oldest, oldest, row(first, "m2", now.AddHours(-1), 5), row(second, "m3", now, 7)]);

        var report = await new LocalUsageService([root], [], "").GetReportAsync(7);

        Assert.Equal("3", report.ContractVersion);
        Assert.Equal(3, report.Events);
        Assert.Equal(2, report.SessionCount);
        Assert.Equal(2, report.Sessions.Count);
        Assert.Equal(7, report.Sessions[0].Counts.Total);
        Assert.Equal(15, report.Sessions[1].Counts.Total);
        Assert.Equal(2, report.Sessions[1].Events);
        Assert.Equal("session-project", report.Sessions[1].ProjectName);
        Assert.StartsWith("s_", report.Sessions[1].Id);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(first, json);
        Assert.DoesNotContain(second, json);
        Assert.DoesNotContain(project, json);
    }

    [Fact]
    public async Task OpenCodeGoEstimateUsesPerModelCapsAndMarksIncompleteWindowsUnknown()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "opencode.db");
        var now = DateTimeOffset.UtcNow;
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT); " +
                "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT); " +
                "INSERT INTO session VALUES ('s1', '/test/project');";
            command.ExecuteNonQuery();
            void insert(string id, string model, DateTimeOffset at, long input, long cacheWrite = 0)
            {
                using var row = connection.CreateCommand();
                row.CommandText = "INSERT INTO message VALUES ($id, 's1', $time, $data)";
                row.Parameters.AddWithValue("$id", id);
                row.Parameters.AddWithValue("$time", at.ToUnixTimeMilliseconds());
                row.Parameters.AddWithValue("$data", Line(new { role = "assistant", providerID = "opencode-go",
                    modelID = model, tokens = new { input, output = 0, cache = new { write = cacheWrite } } }));
                row.ExecuteNonQuery();
            }
            insert("m1", "muse-spark-1.3-contributor", now.AddHours(-1), 12_000_000);
            insert("m2", "muse-spark-1.3-contributor", now.AddDays(-2), 100, 5);
            insert("m3", "glm-5.3", now.AddHours(-1), 1_000_000);
            insert("m4", "new-go-model", now.AddHours(-1), 100);
            insert("m5", "glm-5.3-flash", now.AddHours(-1), 1_000_000);
            insert("m6", "minimax-m2.7", now.AddHours(-1), 1_000_000, 1_000_000);
            insert("m7", "gpt-5.6-luna", now.AddHours(-1), 1_000_000);
        }

        var report = await new LocalUsageService([], [], db).GetOpenCodeGoEstimateAsync(now);

        Assert.Equal("estimated", report.Status);
        var muse = Assert.Single(report.Models, model => model.Model == "muse-spark-1.3-contributor");
        Assert.Equal(12m, muse.Windows[0].LimitUsd);
        Assert.Equal(1.2m, muse.Windows[0].ObservedUsd);
        Assert.Equal(10, muse.Windows[0].EstimatedUsedPercent);
        Assert.Null(muse.Windows[1].EstimatedUsedPercent);
        Assert.Equal(1, muse.Windows[1].UnpricedRecords);
        var glm = Assert.Single(report.Models, model => model.Model == "glm-5.3");
        Assert.Equal(3m, glm.Windows[0].LimitUsd);
        var flash = Assert.Single(report.Models, model => model.Model == "glm-5.3-flash");
        Assert.Equal(12m, flash.Windows[0].LimitUsd);
        Assert.Equal(0.15m, flash.Windows[0].ObservedUsd);
        var miniMax = Assert.Single(report.Models, model => model.Model == "minimax-m2.7");
        Assert.Equal(12m, miniMax.Windows[0].LimitUsd);
        Assert.Equal(0.675m, miniMax.Windows[0].ObservedUsd);
        Assert.Equal(0, miniMax.Windows[0].UnpricedRecords);
        Assert.NotNull(miniMax.Windows[0].EstimatedUsedPercent);
        Assert.Null(Assert.Single(report.Models, model => model.Model == "gpt-5.6-luna").Windows[0].EstimatedUsedPercent);
        Assert.Null(Assert.Single(report.Models, model => model.Model == "new-go-model").Windows[0].EstimatedUsedPercent);

        var usage = await new LocalUsageService([], [], db).GetReportAsync(7);
        var miniMaxUsage = Assert.Single(usage.Models, model => model.Model == "minimax-m2.7");
        Assert.Equal(0.675m, miniMaxUsage.Cost.PricedUsd);
        Assert.Equal(0, miniMaxUsage.Cost.UnpricedEvents);
    }

    [Fact]
    public async Task NormalizesClaudeDuplicatesAndCodexCumulativeCounters()
    {
        var claude = Path.Combine(_root, "claude");
        var codex = Path.Combine(_root, "codex");
        Directory.CreateDirectory(claude);
        Directory.CreateDirectory(codex);
        var now = DateTimeOffset.UtcNow;
        var assistant = Line(new
        {
            type = "assistant", timestamp = now, sessionId = "s1", requestId = "r1",
            message = new { id = "m1", model = "claude-test", usage = new
            {
                input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 20, cache_creation_input_tokens = 3
            } }
        });
        File.WriteAllLines(Path.Combine(claude, "s1.jsonl"), ["not json", assistant, assistant,
            Line(new { type = "assistant", timestamp = now, message = new { id = "synthetic", model = "<synthetic>", usage = new { input_tokens = 999 } } })]);
        string count(int input, int cached, int output, int reasoning) => Line(new
        {
            type = "event_msg", timestamp = now,
            payload = new { type = "token_count", info = new
            {
                total_token_usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, reasoning_output_tokens = reasoning },
                last_token_usage = new { input_tokens = 999 }
            } }
        });
        File.WriteAllLines(Path.Combine(codex, "s2.jsonl"), [
            Line(new { type = "turn_context", payload = new { model = "gpt-test" } }),
            count(100, 40, 20, 8), count(100, 40, 20, 8), count(130, 50, 25, 10)
        ]);

        var service = new LocalUsageService([claude], [codex], "");
        var report = await service.GetReportAsync(7);

        Assert.Equal(3, report.Events); // one Claude response, two changing Codex samples
        Assert.Equal(2, report.FilesScanned);
        Assert.Equal(new TokenCounts(90, 30, 70, 3, 10), report.Totals);
        Assert.Equal(2, report.Sources.Count);
        Assert.Contains(report.Models, row => row.Source == "codex" && row.Model == "gpt-test");
        Assert.Single(report.Daily);
    }

    [Fact]
    public async Task ChangedFileIsReparsedAndMissingFileDisappears()
    {
        var claude = Path.Combine(_root, "claude");
        Directory.CreateDirectory(claude);
        var path = Path.Combine(claude, "session.jsonl");
        var now = DateTimeOffset.UtcNow;
        string row(string id, int input) => Line(new
        {
            type = "assistant", timestamp = now,
            message = new { id, model = "claude-test", usage = new { input_tokens = input, output_tokens = 1 } }
        });
        File.WriteAllLines(path, [row("a", 2)]);
        var service = new LocalUsageService([claude], [], "");
        Assert.Equal(3, (await service.GetReportAsync(30)).Totals.Total);
        File.AppendAllLines(path, [row("b", 4)]);
        Assert.Equal(8, (await service.GetReportAsync(30)).Totals.Total);
        File.Delete(path);
        Assert.Equal(0, (await service.GetReportAsync(30)).Totals.Total);
    }

    [Fact]
    public async Task ExcludesMirroredSubagentHistoryButKeepsRealSubagentUsage()
    {
        var codex = Path.Combine(_root, "codex");
        Directory.CreateDirectory(codex);
        var now = DateTimeOffset.UtcNow;
        string count() => Line(new { type = "event_msg", timestamp = now, payload = new
        {
            type = "token_count", info = new { last_token_usage = new { input_tokens = 10, cached_input_tokens = 4, output_tokens = 2 } }
        } });
        File.WriteAllLines(Path.Combine(codex, "mirror.jsonl"), [
            Line(new { type = "session_meta", payload = new { thread_source = "subagent", forked_from_id = "parent" } }), count()
        ]);
        File.WriteAllLines(Path.Combine(codex, "real.jsonl"), [
            Line(new { type = "session_meta", payload = new { thread_source = "subagent" } }), count()
        ]);

        var report = await new LocalUsageService([], [codex], "").GetReportAsync(7);

        Assert.Equal(1, report.Events);
        Assert.Equal(new TokenCounts(6, 2, 4, 0, 0), report.Totals);
    }

    [Fact]
    public async Task EstimatesPublishedApiCostAndKeepsSameNamedProjectsDistinct()
    {
        var claude = Path.Combine(_root, "claude");
        var codex = Path.Combine(_root, "codex");
        Directory.CreateDirectory(claude);
        Directory.CreateDirectory(codex);
        var projectA = Path.Combine(_root, "one", "repo");
        var projectB = Path.Combine(_root, "two", "repo");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(Path.Combine(claude, "session.jsonl"), [
            Line(new { type = "assistant", timestamp = now, cwd = projectA, requestId = "r1", message = new
            {
                id = "m1", model = "claude-opus-5", usage = new
                {
                    input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 20,
                    cache_creation_input_tokens = 3,
                    cache_creation = new { ephemeral_5m_input_tokens = 1, ephemeral_1h_input_tokens = 2 }
                }
            } }),
            Line(new { type = "assistant", timestamp = now, cwd = projectB, requestId = "r2", message = new
            {
                id = "m2", model = "claude-new-model", usage = new { input_tokens = 10, output_tokens = 5 }
            } })
        ]);
        File.WriteAllLines(Path.Combine(codex, "session.jsonl"), [
            Line(new { type = "turn_context", payload = new { model = "gpt-5.6-sol", cwd = projectA } }),
            Line(new { type = "event_msg", timestamp = now, payload = new { type = "token_count", info = new
            {
                total_token_usage = new { input_tokens = 100, cached_input_tokens = 40, output_tokens = 20 }
            } } })
        ]);

        var report = await new LocalUsageService([claude], [codex], "").GetReportAsync(7);

        Assert.Equal(3, report.Events);
        Assert.Equal(0.00086725m, report.Cost.PricedUsd);
        Assert.Equal(1, report.Cost.UnpricedEvents);
        Assert.Equal(15, report.Cost.UnpricedTokens);
        Assert.Equal(2, report.Projects.Count);
        Assert.All(report.Projects, project => Assert.Equal("repo", project.Name));
        Assert.NotEqual(report.Projects[0].Id, report.Projects[1].Id);
        Assert.Equal(0.00086725m, report.Projects[0].Cost.PricedUsd);
        Assert.DoesNotContain(_root, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task ReadsOpenCodeAssistantMessagesFromLiveDatabaseWithoutExposingContents()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "opencode.db");
        var project = Path.Combine(_root, "work", "my-project");
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT);
                CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);
                """;
            schema.ExecuteNonQuery();
        }
        using (var session = connection.CreateCommand())
        {
            session.CommandText = "INSERT INTO session VALUES ('s1', $directory)";
            session.Parameters.AddWithValue("$directory", project);
            session.ExecuteNonQuery();
        }
        void insert(string id, long time, object data)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO message VALUES ($id, 's1', $time, $data)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$time", time);
            command.Parameters.AddWithValue("$data", Line(data));
            command.ExecuteNonQuery();
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        insert("openai", now, new { role = "assistant", providerID = "openai", modelID = "gpt-5.6-sol",
            content = "private-prompt-that-must-not-return", tokens = new
            {
                input = 100, output = 20, reasoning = 5, cache = new { read = 40, write = 0 }
            } });
        insert("go", now, new { role = "assistant", providerID = "opencode-go", modelID = "muse-spark-1.3-contributor",
            tokens = new { input = 10, output = 2, reasoning = 3, cache = new { read = 50, write = 0 } } });
        insert("unknown", now, new { role = "assistant", providerID = "deepseek", modelID = "deepseek-flash",
            tokens = new { input = 7, output = 0 } });
        insert("user", now, new { role = "user", providerID = "openai", modelID = "gpt-5.6-sol",
            tokens = new { input = 999 } });
        insert("old", DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeMilliseconds(),
            new { role = "assistant", providerID = "openai", modelID = "gpt-5.6-sol", tokens = new { input = 1000 } });
        using (var malformed = connection.CreateCommand())
        {
            malformed.CommandText = "INSERT INTO message VALUES ('bad', 's1', $time, '{bad json')";
            malformed.Parameters.AddWithValue("$time", now);
            malformed.ExecuteNonQuery();
        }

        var usage = new LocalUsageService([], [], db);
        var report = await usage.GetReportAsync(30);

        Assert.Equal(3, report.Events);
        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(0, report.FilesSkipped);
        Assert.Equal(new TokenCounts(117, 30, 90, 0, 8), report.Totals);
        Assert.Single(report.Sources);
        Assert.Equal("opencode", report.Sources[0].Source);
        Assert.Equal(0.0009181m, report.Cost.PricedUsd);
        Assert.Equal(1, report.Cost.UnpricedEvents);
        Assert.Equal(7, report.Cost.UnpricedTokens);
        Assert.Contains(report.Models, model => model.Provider == "openai" && model.Model == "gpt-5.6-sol");
        Assert.Contains(report.Models, model => model.Provider == "opencode-go" && model.Cost.PricedUsd == 0.0000021m);
        Assert.Single(report.Projects);
        Assert.Equal("my-project", report.Projects[0].Name);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(project, serialized);
        Assert.DoesNotContain("private-prompt", serialized);

        insert("new", now, new { role = "assistant", providerID = "openai", modelID = "gpt-5.6-sol",
            tokens = new { input = 1 } });
        Assert.Equal(4, (await usage.GetReportAsync(30)).Events);
        Assert.Equal(5, (await usage.GetReportAsync(0)).Events);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
