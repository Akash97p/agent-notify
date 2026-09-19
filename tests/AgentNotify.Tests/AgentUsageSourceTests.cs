using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Insights.Usage;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Tests;

public sealed class AgentUsageSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"an-sources-{Guid.NewGuid():N}");
    private static string Line(object value) => JsonSerializer.Serialize(value);

    private LocalUsageService Service(IEnumerable<string>? muse = null, IEnumerable<string>? kilo = null,
        IEnumerable<string>? gemini = null) =>
        new([], [], Path.Combine(_root, "missing-opencode.db"), museRoots: muse ?? [], kiloDatabases: kilo ?? [],
            geminiRoots: gemini ?? []);

    [Fact]
    public async Task MuseCountsEveryModelCallOnceAndGroupsSubagentsUnderTheParentProject()
    {
        var sessions = Path.Combine(_root, "muse", "sessions");
        var parent = Path.Combine(sessions, "2026", "09", "16", "parent-session");
        var child = Path.Combine(parent, "subagent", "child-session");
        Directory.CreateDirectory(child);
        var project = Path.Combine(_root, "work", "muse-project");
        var micros = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        string call(string id, int input, int cached, int output, int reasoning) => Line(new
        {
            id, recorded_at = micros, record_type = "event", payload_type = "runtime.session",
            payload = new
            {
                kind = "run",
                @event = new
                {
                    kind = "model_completed", model = "muse-spark-1.2-contributor",
                    usage = new { input_tokens = input, cached_tokens = cached, cache_read_tokens = cached,
                        cache_write_tokens = 0, output_tokens = output, reasoning_tokens = reasoning }
                }
            }
        });
        File.WriteAllLines(Path.Combine(parent, "session.jsonl"),
        [
            Line(new { id = "r0", payload_type = "runtime.session.route_facts", payload = new { kind = "route_facts", record = new { cwd = project } } }),
            Line(new { id = "p0", payload_type = "runtime.session", payload = new { @event = new { kind = "user_prompt_display", text = "private-muse-prompt model_completed" } } }),
            call("c1", 1000, 800, 50, 20),
            "{broken json model_completed",
            call("c2", 500, 0, 10, 0)
        ]);
        File.WriteAllLines(Path.Combine(child, "session.jsonl"), [call("c3", 300, 100, 30, 5)]);

        var report = await Service(muse: [sessions]).GetReportAsync(30);

        Assert.Equal(3, report.Events);
        Assert.Equal(new TokenCounts(900, 90, 900, 0, 25), report.Totals);
        Assert.Equal("muse", Assert.Single(report.Sources).Source);
        var model = Assert.Single(report.Models);
        Assert.Equal("meta", model.Provider);
        Assert.True(model.Cost.Complete);
        Assert.Equal(new[] { "muse-project" }, report.Projects.Select(item => item.Name));
        Assert.Equal(3, Assert.Single(report.Sessions).Events);
        Assert.DoesNotContain("private-muse-prompt", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task GeminiChatsMapProjectFoldersFromProjectsJson()
    {
        var gemini = Path.Combine(_root, ".gemini");
        var tmp = Path.Combine(gemini, "tmp");
        var named = Path.Combine(_root, "work", "named-project");
        var hashed = Path.Combine(_root, "work", "hashed-project");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(gemini).FullName, "projects.json"),
            Line(new { projects = new Dictionary<string, string> { [named] = "named-project", [hashed] = "hashed-project" } }));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashed))).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        void chat(string folder, string file, string session, params object[] messages) =>
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(tmp, folder, "chats")).FullName, file),
                Line(new { sessionId = session, messages }));
        chat("named-project", "session-2026-09-16T10-00-aaaa.json", "s1",
            new { id = "u1", timestamp = now, type = "user", content = "private-gemini-prompt" },
            new { id = "g1", timestamp = now, type = "gemini", model = "gemini-2.5-flash", content = "answer",
                tokens = new { input = 1000, output = 100, cached = 600, thoughts = 40, tool = 10, total = 1150 } });
        chat(hash, "session-2026-09-16T11-00-bbbb.json", "s2",
            new { id = "g2", timestamp = now, type = "gemini", model = "gemini-2.5-flash",
                tokens = new { input = 200, output = 20, cached = 0, thoughts = 0, tool = 0, total = 220 } });
        File.WriteAllText(Path.Combine(tmp, "named-project", "logs.json"), "[]");

        var report = await Service(gemini: [tmp]).GetReportAsync(30);

        Assert.Equal(2, report.Events);
        Assert.Equal(new TokenCounts(610, 160, 600, 0, 40), report.Totals);
        Assert.Equal("gemini_cli", Assert.Single(report.Sources).Source);
        Assert.Equal(new[] { "hashed-project", "named-project" }, report.Projects.Select(item => item.Name).Order());
        Assert.True(report.Cost.Complete);
        Assert.DoesNotContain("private-gemini-prompt", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task KiloDatabasesAreReadWithOpenCodesSchemaAsTheirOwnSource()
    {
        var db = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "kilo")).FullName, "kilo.db");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE session (id TEXT PRIMARY KEY, directory TEXT);
                CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);
                INSERT INTO session VALUES ('s1', '/home/tester/kilo-project');
                """;
            command.ExecuteNonQuery();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var (id, provider, model) in new[] { ("m1", "zai", "glm-5.1"), ("m2", "kilo", "kilo-auto/free"), ("m3", "legacy", "legacy") })
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO message VALUES ($id, 's1', $time, $data)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$time", now);
                insert.Parameters.AddWithValue("$data", Line(new
                {
                    role = "assistant", providerID = provider, modelID = model,
                    tokens = model == "legacy" ? new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } }
                        : new { input = 1_000_000, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } }
                }));
                insert.ExecuteNonQuery();
            }
        }

        var report = await Service(kilo: [db]).GetReportAsync(30);

        Assert.Equal(2, report.Events);
        Assert.Equal("kilo", Assert.Single(report.Sources).Source);
        Assert.Equal(1.4m, report.Cost.PricedUsd);
        Assert.True(report.Cost.Complete);
        Assert.Empty((await Service(kilo: [db]).GetOpenCodeGoEstimateAsync(DateTimeOffset.UtcNow)).Models);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
