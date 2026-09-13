using System.Text.Json;
using AgentNotify.Core.Usage;

namespace AgentNotify.Tests;

public sealed class LocalUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"an-usage-{Guid.NewGuid():N}");
    private static string Line(object value) => JsonSerializer.Serialize(value);

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

        var service = new LocalUsageService([claude], [codex]);
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
        var service = new LocalUsageService([claude], []);
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

        var report = await new LocalUsageService([], [codex]).GetReportAsync(7);

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

        var report = await new LocalUsageService([claude], [codex]).GetReportAsync(7);

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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
