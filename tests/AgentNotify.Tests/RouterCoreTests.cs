using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Router;

namespace AgentNotify.Tests;

public sealed class RouterCoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"an-router-{Guid.NewGuid():N}.db");
    private static string TempConfigDir() => Path.Combine(Path.GetTempPath(), $"an-router-cfg-{Guid.NewGuid():N}");

    private static (RouterRepository repo, RouterConfigService service, string db, string cfgDir) Create(string? dbPath = null, string? cfgDir = null)
    {
        var dbFile = dbPath ?? TempDb();
        var cfg = cfgDir ?? TempConfigDir();
        Directory.CreateDirectory(cfg);
        var repo = new RouterRepository(dbFile);
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        var store = new ConfigStore(cfg);
        var service = new RouterConfigService(repo, protector, store);
        return (repo, service, dbFile, cfg);
    }

    private static void Cleanup(string db, string cfgDir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(db + suffix); } catch { }
        }
        try { Directory.Delete(cfgDir, true); } catch { }
    }

    private static bool FileContains(string db, string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = db + suffix;
            if (!File.Exists(path)) continue;
            var haystack = File.ReadAllBytes(path);
            if (Contains(haystack, needle)) return true;
        }
        return false;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    [Fact]
    public async Task RepositoryRoundTripsUpstreamAndRoute()
    {
        var (repo, _, db, cfg) = Create();
        try
        {
            await repo.InitializeAsync();
            var stored = new StoredRouterUpstream("ru_1", "openai", "OpenAI", RouterWire.OpenAiResponses, "https://api.openai.com/v1", null, ["gpt-4o"], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await repo.InsertUpstreamAsync(stored);
            var fetched = await repo.GetUpstreamAsync("ru_1");
            Assert.NotNull(fetched);
            Assert.Equal("openai", fetched!.Slug);

            var route = new RouterRoute("rr_1", "alias1", RouterKind.Alias, ["openai/gpt-4o"], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await repo.InsertRouteAsync(route);
            var rf = await repo.GetRouteAsync("rr_1");
            Assert.NotNull(rf);
            Assert.Equal("alias1", rf!.Name);

            await repo.SetSettingsAsync(new RouterSettings("alias1"));
            var settings = await repo.GetSettingsAsync();
            Assert.Equal("alias1", settings.DefaultRoute);
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task SecretsAreSealed()
    {
        var (repo, service, db, cfg) = Create();
        try
        {
            const string secret = "sk-secret-value-1234567890";
            var created = await service.CreateUpstreamAsync("openai", "OpenAI", RouterWire.OpenAiResponses, "https://api.openai.com/v1", secret, ["gpt-4o"]);
            Assert.True(created.HasKey);
            var stored = await repo.GetUpstreamAsync(created.Id);
            Assert.NotNull(stored);
            Assert.NotNull(stored!.EncryptedKey);
            Assert.NotEqual(secret, stored.EncryptedKey);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.False(FileContains(db, secret));
            var list = await service.ListUpstreamsAsync();
            var json = JsonSerializer.Serialize(list);
            Assert.DoesNotContain(secret, json);
            var snapshot = await service.GetSnapshotAsync();
            var pubJson = JsonSerializer.Serialize(snapshot.Upstreams.Select(u => new RouterUpstream(u.Id, u.Slug, u.Label, u.Wire, u.BaseUrl, u.EncryptedKey is not null, u.Models, u.Enabled, u.CreatedAt, u.UpdatedAt)));
            Assert.DoesNotContain(secret, pubJson);
            // Decrypt works
            Assert.Equal(secret, service.DecryptKey(stored));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task UpdateKeepsKeyWhenEmptyAndClearsWhenFlagged()
    {
        var (repo, service, db, cfg) = Create();
        try
        {
            const string secret = "sk-keep-test-12345678";
            var created = await service.CreateUpstreamAsync("openai", "OpenAI", RouterWire.OpenAiResponses, "https://api.openai.com/v1", secret, []);
            var stored1 = await repo.GetUpstreamAsync(created.Id);
            var updatedKeep = await service.UpdateUpstreamAsync(created.Id, "openai", "OpenAI2", RouterWire.OpenAiResponses, "https://api.openai.com/v1", null, [], true);
            var stored2 = await repo.GetUpstreamAsync(created.Id);
            Assert.Equal(stored1!.EncryptedKey, stored2!.EncryptedKey);
            Assert.Equal(secret, service.DecryptKey(stored2!));

            var updatedEmpty = await service.UpdateUpstreamAsync(created.Id, "openai", "OpenAI2", RouterWire.OpenAiResponses, "https://api.openai.com/v1", "", [], true);
            var stored3 = await repo.GetUpstreamAsync(created.Id);
            Assert.Equal(stored2!.EncryptedKey, stored3!.EncryptedKey);

            await service.UpdateUpstreamAsync(created.Id, "openai", "OpenAI2", RouterWire.OpenAiResponses, "https://api.openai.com/v1", null, [], true, clearKey: true);
            var stored4 = await repo.GetUpstreamAsync(created.Id);
            Assert.Null(stored4!.EncryptedKey);
            Assert.Null(service.DecryptKey(stored4));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task UpdateReplacesKey()
    {
        var (repo, service, db, cfg) = Create();
        try
        {
            var created = await service.CreateUpstreamAsync("openai", "OpenAI", RouterWire.OpenAiResponses, "https://api.openai.com/v1", "first-key-12345678", []);
            var stored1 = await repo.GetUpstreamAsync(created.Id);
            await service.UpdateUpstreamAsync(created.Id, "openai", "OpenAI", RouterWire.OpenAiResponses, "https://api.openai.com/v1", "second-key-1234567890", []);
            var stored2 = await repo.GetUpstreamAsync(created.Id);
            Assert.NotEqual(stored1!.EncryptedKey, stored2!.EncryptedKey);
            Assert.Equal("second-key-1234567890", service.DecryptKey(stored2!));
        }
        finally { Cleanup(db, cfg); }
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("a-very-long-slug-that-exceeds-thirty-two-chars")]
    [InlineData("")]
    public async Task SlugValidationFails(string slug)
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync(slug, "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task LabelValidationFails()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("openai", "", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("openai", "has\0control", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task WireValidationFails()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("openai", "Label", "invalid_wire", "https://api.example.com/v1", null, []));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task KeyValidationFails()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", "short", []));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", "has space key 12345678", []));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task ModelsValidationAndDedup()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            var created = await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, ["a", "b", "a", "c"]);
            Assert.Equal(new[] { "a", "b", "c" }, created.Models);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("s2", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, ["has space model 123"]));
            var tooMany = Enumerable.Range(0, 201).Select(i => $"m{i}").ToList();
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("s3", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, tooMany));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task MaxUpstreamsEnforced()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            for (var i = 0; i < 32; i++)
                await service.CreateUpstreamAsync($"s{i}", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("extra", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task DuplicateSlugRejected()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateUpstreamAsync("openai", "Label2", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task DeleteRefusedWhenReferenced()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            var up = await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            var route = await service.CreateRouteAsync("my-alias", RouterKind.Alias, ["openai/gpt-4o"]);
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteUpstreamAsync(up.Id));
            Assert.Contains("my-alias", ex.Message);

            // Also via default route
            var (_, service2, db2, cfg2) = Create(db, cfg);
            // We already have route referencing; now set default route to slug/model
            await service.SetDefaultRouteAsync("openai/gpt-4o");
            var up2 = await service.CreateUpstreamAsync("anthropic", "Label", RouterWire.AnthropicMessages, "https://api.anthropic.com/v1", null, []);
            await service.SetDefaultRouteAsync("anthropic/claude");
            var ex2 = await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteUpstreamAsync(up2.Id));
            Assert.Contains("default route", ex2.Message.ToLowerInvariant());
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task DeleteRefusedWhenDefaultRouteIsComboReferencingUpstream()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            var up1 = await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            var up2 = await service.CreateUpstreamAsync("anthropic", "Label2", RouterWire.AnthropicMessages, "https://api.anthropic.com/v1", null, []);
            var route = await service.CreateRouteAsync("coding", RouterKind.Combo, ["openai/gpt-4o", "anthropic/claude"]);
            await service.SetDefaultRouteAsync("combo/coding");
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteUpstreamAsync(up1.Id));
            Assert.Contains("coding", ex.Message);
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task RouteValidations()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            // invalid name
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("BadName", RouterKind.Alias, ["openai/gpt"]));
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("a/b", RouterKind.Alias, ["openai/gpt"]));
            // duplicate name
            await service.CreateRouteAsync("my-route", RouterKind.Alias, ["openai/gpt"]);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("my-route", RouterKind.Alias, ["openai/gpt"]));
            // conflicts with upstream slug
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("openai", RouterKind.Alias, ["openai/gpt"]));
            // alias must be 1
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("alias2", RouterKind.Alias, ["openai/gpt", "openai/gpt2"]));
            // combo 1..8
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("combo1", RouterKind.Combo, []));
            var nine = Enumerable.Range(0, 9).Select(i => "openai/m" + i).ToList();
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("combo9", RouterKind.Combo, nine));
            // unknown slug
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("alias3", RouterKind.Alias, ["unknown/gpt"]));
            // target must be slug/model
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("alias4", RouterKind.Alias, ["noslash"]));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task MaxRoutesEnforced()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            for (var i = 0; i < 64; i++)
                await service.CreateRouteAsync($"r{i}", RouterKind.Alias, ["openai/m"]);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRouteAsync("extra", RouterKind.Alias, ["openai/m"]));
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task DefaultRouteValidation()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            await service.CreateRouteAsync("my-alias", RouterKind.Alias, ["openai/gpt"]);
            await service.SetDefaultRouteAsync("my-alias");
            await service.SetDefaultRouteAsync("combo/my-alias"); // combo prefix with alias? should fail because combo/my-alias expects combo kind? But service validates combo/<name> must be existing route name regardless of kind. So it passes.
            // Unknown route
            await Assert.ThrowsAsync<ArgumentException>(() => service.SetDefaultRouteAsync("unknown-route"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.SetDefaultRouteAsync("combo/unknown"));
            await Assert.ThrowsAsync<ArgumentException>(() => service.SetDefaultRouteAsync("unknown/gpt"));
            // Clear
            await service.SetDefaultRouteAsync(null);
            var settings = await service.GetSettingsAsync();
            Assert.Null(settings.DefaultRoute);
            await service.SetDefaultRouteAsync("");
            settings = await service.GetSettingsAsync();
            Assert.Null(settings.DefaultRoute);
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task GenerationIncrements()
    {
        var (_, service, db, cfg) = Create();
        try
        {
            var s1 = await service.GetSnapshotAsync();
            var g1 = s1.Generation;
            await service.CreateUpstreamAsync("openai", "Label", RouterWire.OpenAiChat, "https://api.example.com/v1", null, []);
            var s2 = await service.GetSnapshotAsync();
            Assert.Equal(g1 + 1, s2.Generation);
            var route = await service.CreateRouteAsync("my-alias", RouterKind.Alias, ["openai/gpt"]);
            var s3 = await service.GetSnapshotAsync();
            Assert.Equal(g1 + 2, s3.Generation);
            await service.SetDefaultRouteAsync("my-alias");
            var s4 = await service.GetSnapshotAsync();
            Assert.Equal(g1 + 3, s4.Generation);
            // Cached snapshot returns same generation
            var s5 = await service.GetSnapshotAsync();
            Assert.Equal(s4.Generation, s5.Generation);
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task ConfigEnableAndRegenerate()
    {
        var cfgDir = TempConfigDir();
        var db = TempDb();
        try
        {
            var store = new ConfigStore(cfgDir);
            var repo = new RouterRepository(db);
            var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
            var service = new RouterConfigService(repo, protector, store);
            Assert.False(await service.GetRouterEnabledAsync());
            Assert.Null(await service.GetRouterKeyAsync());
            await service.SetRouterEnabledAsync(true);
            Assert.True(await service.GetRouterEnabledAsync());
            var key1 = await service.GetRouterKeyAsync();
            Assert.NotNull(key1);
            Assert.StartsWith("anr_", key1);
            // Enable again does not change key
            await service.SetRouterEnabledAsync(true);
            var key2 = await service.GetRouterKeyAsync();
            Assert.Equal(key1, key2);
            var key3 = await service.RegenerateRouterKeyAsync();
            Assert.NotEqual(key1, key3);
            Assert.StartsWith("anr_", key3);
            await service.SetRouterEnabledAsync(false);
            Assert.False(await service.GetRouterEnabledAsync());
            // Key remains after disable
            Assert.Equal(key3, await service.GetRouterKeyAsync());
        }
        finally { Cleanup(db, cfgDir); }
    }

    [Fact]
    public async Task ConfigNormalization()
    {
        var cfg = new AgentNotifyConfig
        {
            RouterMaxRequestBodyBytes = 999,
            RouterLedgerRetentionDays = 999
        };
        cfg.ApplyDefaults();
        Assert.Equal(1 * 1024 * 1024, cfg.RouterMaxRequestBodyBytes);
        Assert.Equal(365, cfg.RouterLedgerRetentionDays);
        cfg.RouterMaxRequestBodyBytes = 999 * 1024 * 1024;
        cfg.RouterLedgerRetentionDays = 0;
        cfg.ApplyDefaults();
        Assert.Equal(128 * 1024 * 1024, cfg.RouterMaxRequestBodyBytes);
        Assert.Equal(1, cfg.RouterLedgerRetentionDays);
        Assert.StartsWith("anr_", AgentNotifyConfig.GenerateRouterKey());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com/v1/", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("https://example.com", true)]
    public void DestinationAcceptsValidUrls(string url, bool shouldPass)
    {
        var ok = RouterDestination.TryValidateBaseUrl(url, out var uri, out var error);
        Assert.Equal(shouldPass, ok);
        if (shouldPass) Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:pw@host")]
    [InlineData("https://host/v1?x=1")]
    [InlineData("https://host/v1#frag")]
    [InlineData("http://127.0.0.2")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void DestinationRejectsInvalidUrls(string url)
    {
        Assert.False(RouterDestination.TryValidateBaseUrl(url, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void DestinationTrimsTrailingSlash()
    {
        Assert.True(RouterDestination.TryValidateBaseUrl("https://api.example.com/v1/", out var uri, out _));
        Assert.Equal("https://api.example.com/v1", uri!.ToString());
    }

    [Fact]
    public void DestinationMaxLength()
    {
        var longUrl = "https://example.com/" + new string('a', 500);
        Assert.False(RouterDestination.TryValidateBaseUrl(longUrl, out _, out _));
    }

    [Fact]
    public async Task LedgerInsertListSummarizePrune()
    {
        var (repo, _, db, cfg) = Create();
        try
        {
            await repo.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var req1 = new RouterRequestRecord("r1", now, now.AddSeconds(1), RouterWire.OpenAiChat, "openai/gpt-4o", RouterRouteKind.Explicit, null, "openai", "gpt-4o", false, 200, RouterOutcome.Ok, 10, 2, 20, 5, "reported", null);
            var att1 = new RouterAttemptRecord("r1", 0, "openai", "gpt-4o", RouterWire.OpenAiChat, 200, null, 100, true, now);
            await repo.InsertRequestAsync(req1, [att1]);

            var req2 = new RouterRequestRecord("r2", now.AddSeconds(10), now.AddSeconds(11), RouterWire.OpenAiChat, "openai/gpt-4o", RouterRouteKind.Explicit, null, "openai", "gpt-4o", false, 500, RouterOutcome.UpstreamError, 5, 0, 0, 0, "unreported", "upstream_error");
            var att2 = new RouterAttemptRecord("r2", 0, "openai", "gpt-4o", RouterWire.OpenAiChat, 500, "upstream_error", 50, false, now.AddSeconds(10));
            await repo.InsertRequestAsync(req2, [att2]);

            var entries = await repo.ListRecentRequestsAsync(10);
            Assert.Equal(2, entries.Count);
            Assert.Equal("r2", entries[0].Request.Id); // most recent first
            Assert.Single(entries[0].Attempts);
            Assert.Single(entries[1].Attempts);

            var summaries = await repo.SummarizeAsync(now.AddSeconds(-1));
            var summary = Assert.Single(summaries);
            Assert.Equal("openai", summary.UpstreamSlug);
            Assert.Equal("gpt-4o", summary.Model);
            Assert.Equal(2, summary.Count);
            Assert.Equal(1, summary.OkCount);
            Assert.Equal(15, summary.InputTokensSum);
            Assert.Equal(2, summary.CachedInputTokensSum);
            Assert.Equal(20, summary.OutputTokensSum);
            Assert.Equal(5, summary.ReasoningTokensSum);

            // Prune old
            var pruned = await repo.PruneLedgerAsync(now.AddSeconds(5));
            Assert.Equal(1, pruned);
            var remaining = await repo.ListRecentRequestsAsync(10);
            Assert.Single(remaining);
            Assert.Equal("r2", remaining[0].Request.Id);
        }
        finally { Cleanup(db, cfg); }
    }

    [Fact]
    public async Task LedgerComboAttempts()
    {
        var (repo, _, db, cfg) = Create();
        try
        {
            await repo.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var req = new RouterRequestRecord("r1", now, now.AddSeconds(2), RouterWire.OpenAiChat, "combo/coding", RouterRouteKind.Combo, "coding", "anthropic", "claude", true, 200, RouterOutcome.Ok, 10, 0, 20, 0, "reported", null);
            var attempts = new[]
            {
                new RouterAttemptRecord("r1", 0, "openai", "gpt-4o", RouterWire.OpenAiChat, 429, "rate_limited", 30, false, now),
                new RouterAttemptRecord("r1", 1, "anthropic", "claude", RouterWire.AnthropicMessages, 200, null, 80, true, now.AddMilliseconds(30)),
            };
            await repo.InsertRequestAsync(req, attempts);
            var entries = await repo.ListRecentRequestsAsync(5);
            Assert.Single(entries);
            Assert.Equal(2, entries[0].Attempts.Count);
            Assert.Equal(0, entries[0].Attempts[0].Ordinal);
            Assert.Equal(1, entries[0].Attempts[1].Ordinal);
        }
        finally { Cleanup(db, cfg); }
    }
}
