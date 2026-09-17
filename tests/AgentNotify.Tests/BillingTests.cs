using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Billing;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Tests;

public sealed class BillingTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"an-billing-{Guid.NewGuid():N}.db");

    private static BillingService CreateService(
        string db,
        HttpMessageHandler? handler = null,
        TimeProvider? clock = null)
    {
        var repository = new BillingAccountRepository(db);
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        return new BillingService(repository, protector, handler, clock);
    }

    private static void Cleanup(string db)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(db + suffix); } catch { }
        }
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
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [Fact]
    public async Task DeepSeekParsesBalancesAndAvailability()
    {
        var db = TempDb();
        try
        {
            HttpRequestMessage? seen = null;
            var handler = new StubHandler((request, _) =>
            {
                seen = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"10.50","granted_balance":"2.00","topped_up_balance":"8.50"},{"currency":"USD","total_balance":"1.25","granted_balance":"0.25","topped_up_balance":"1.00"}]}""",
                        Encoding.UTF8, "application/json")
                };
            });
            using var service = CreateService(db, handler);
            var account = await service.CreateAsync("deepseek", "Main", "sk-test-key-12345678");
            var report = await service.GetReportAsync();
            var snapshot = Assert.Single(report.Accounts);
            Assert.Equal("ok", snapshot.Status);
            Assert.True(snapshot.Available);
            Assert.Equal(6, snapshot.Balances.Count);
            Assert.Contains(snapshot.Balances, b => b.Kind == "total" && b.Currency == "CNY" && b.Amount == 10.50m);
            Assert.Contains(snapshot.Balances, b => b.Kind == "granted" && b.Currency == "USD" && b.Amount == 0.25m);
            Assert.Equal("https://api.deepseek.com/user/balance", seen!.RequestUri!.ToString());
            Assert.Equal("Bearer", seen.Headers.Authorization?.Scheme);
            Assert.Equal("1", report.ContractVersion);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task MoonshotParsesUsdBalances()
    {
        var db = TempDb();
        try
        {
            HttpRequestMessage? seen = null;
            var handler = new StubHandler((request, _) =>
            {
                seen = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"code":0,"data":{"available_balance":12.5,"voucher_balance":3,"cash_balance":9.5},"scode":"0x0","status":true}""",
                        Encoding.UTF8, "application/json")
                };
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("moonshot", "Kimi", "moonshot-key-12345678");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", snapshot.Status);
            Assert.Equal(3, snapshot.Balances.Count);
            Assert.All(snapshot.Balances, b => Assert.Equal("USD", b.Currency));
            Assert.Contains(snapshot.Balances, b => b.Kind == "available" && b.Amount == 12.5m);
            Assert.Equal("https://api.moonshot.ai/v1/users/me/balance", seen!.RequestUri!.ToString());
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task SiliconFlowReportsNullCurrencyAndIgnoresOtherFields()
    {
        var db = TempDb();
        try
        {
            var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"code":20000,"message":"ok","status":true,"data":{"balance":"0.88","chargeBalance":"88.00","totalBalance":"88.88","email":"someone@example.com","name":"Somebody"}}""",
                    Encoding.UTF8, "application/json")
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("siliconflow", "SF", "sf-key-1234567890");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", snapshot.Status);
            Assert.Equal(3, snapshot.Balances.Count);
            Assert.All(snapshot.Balances, b => Assert.Null(b.Currency));
            Assert.Contains(snapshot.Balances, b => b.Kind == "total_balance" && b.Amount == 88.88m);
            var json = JsonSerializer.Serialize(snapshot);
            Assert.DoesNotContain("someone@example.com", json);
            Assert.DoesNotContain("Somebody", json);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task OpenRouterParsesSpendAndLimitRemaining()
    {
        var db = TempDb();
        try
        {
            var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"usage":10.5,"usage_daily":1.5,"usage_weekly":4.25,"usage_monthly":9.75,"limit":100,"limit_remaining":89.5,"label":"must-never-return"}}""",
                    Encoding.UTF8, "application/json")
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("openrouter", "OR", "or-key-1234567890abcdef");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", snapshot.Status);
            Assert.Equal(4, snapshot.Spend.Count);
            Assert.Contains(snapshot.Spend, s => s.Period == "today" && s.Amount == 1.5m && s.Currency == "USD");
            Assert.Contains(snapshot.Spend, s => s.Period == "this_week" && s.Amount == 4.25m);
            Assert.Contains(snapshot.Spend, s => s.Period == "this_month" && s.Amount == 9.75m);
            Assert.Contains(snapshot.Spend, s => s.Period == "all_time" && s.Amount == 10.5m);
            var remaining = Assert.Single(snapshot.Balances);
            Assert.Equal("limit_remaining", remaining.Kind);
            Assert.Equal(89.5m, remaining.Amount);
            Assert.DoesNotContain("must-never-return", JsonSerializer.Serialize(snapshot));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task OpenRouterOmitsLimitRemainingWhenNull()
    {
        var db = TempDb();
        try
        {
            var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"usage":1,"usage_daily":0.1,"usage_weekly":0.2,"usage_monthly":0.5,"limit":null,"limit_remaining":null}}""",
                    Encoding.UTF8, "application/json")
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("openrouter", "OR", "or-key-1234567890abcdef");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Empty(snapshot.Balances);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task OpenAIPaginatesAndSumsDaily()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((request, _) =>
            {
                calls++;
                var query = request.RequestUri!.Query;
                if (!query.Contains("start_time=") || !query.Contains("bucket_width=1d"))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                if (query.Contains("page="))
                {
                    Assert.Contains("page=p2", query);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"object":"page","data":[{"object":"bucket","start_time":1789948800,"end_time":1790035200,"results":[{"object":"organization.costs.result","amount":{"value":3.0,"currency":"usd"}}]}],"has_more":false,"next_page":null}""",
                            Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"object":"page","data":[{"object":"bucket","start_time":1789776000,"end_time":1789862400,"results":[{"object":"organization.costs.result","amount":{"value":1.0,"currency":"usd"}},{"object":"organization.costs.result","amount":{"value":2.0,"currency":"usd"}}]}],"has_more":true,"next_page":"p2"}""",
                        Encoding.UTF8, "application/json")
                };
            });
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
            using var service = CreateService(db, handler, clock);
            await service.CreateAsync("openai_admin", "OA", "adminkey-1234567890abcdefgh");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", snapshot.Status);
            Assert.Equal(2, calls);
            // 1789776000 = 2026-09-16, 1789948800 = 2026-09-18 (UTC dates from fixed stamps).
            Assert.Equal(2, snapshot.Daily.Count);
            var total = snapshot.Spend.Single(s => s.Period == "last_30_days").Amount;
            Assert.Equal(6.0m, total);
            var month = snapshot.Spend.Single(s => s.Period == "month_to_date").Amount;
            Assert.Equal(snapshot.Daily.Where(d => d.Date.StartsWith("2026-09", StringComparison.Ordinal)).Sum(d => d.Amount), month);
            Assert.All(snapshot.Daily, d => Assert.Equal("USD", d.Currency));
            Assert.StartsWith("https://api.openai.com/v1/organization/costs?", handler.LastUri!.ToString());
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task AnthropicConvertsCentsAndPaginates()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((request, _) =>
            {
                calls++;
                Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
                Assert.NotNull(request.Headers.GetValues("x-api-key").Single());
                if (request.RequestUri!.Query.Contains("page="))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"data":[{"starting_at":"2026-09-16T00:00:00Z","ending_at":"2026-09-17T00:00:00Z","results":[{"amount":"250","currency":"USD"}]}],"has_more":false}""",
                            Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"starting_at":"2026-09-15T00:00:00Z","ending_at":"2026-09-16T00:00:00Z","results":[{"amount":"100","currency":"USD"},{"amount":"50","currency":"USD"}]}],"has_more":true,"next_page":"n2"}""",
                        Encoding.UTF8, "application/json")
                };
            });
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
            using var service = CreateService(db, handler, clock);
            await service.CreateAsync("anthropic_admin", "AA", "anthropic-admin-12345678");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal(2, calls);
            Assert.Equal(2, snapshot.Daily.Count);
            Assert.Contains(snapshot.Daily, d => d.Date == "2026-09-15" && d.Amount == 1.50m);
            Assert.Contains(snapshot.Daily, d => d.Date == "2026-09-16" && d.Amount == 2.50m);
            Assert.Equal(4.00m, snapshot.Spend.Single(s => s.Period == "last_30_days").Amount);
            Assert.StartsWith("https://api.anthropic.com/v1/organizations/cost_report?", handler.LastUri!.ToString());
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task AnthropicAuthFailuresSurfaceAsNotAvailable()
    {
        var db = TempDb();
        try
        {
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(db); } catch { }
                var handler = new StubHandler((_, _) => new HttpResponseMessage(code));
                using var service = CreateService(db, handler);
                await service.CreateAsync("anthropic_admin", "AA", "anthropic-admin-12345678");
                var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
                Assert.Equal("Not available for this key/organization.", snapshot.Message);
            }
        }
        finally { Cleanup(db); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", "The provider rejected this key.")]
    [InlineData(HttpStatusCode.Forbidden, "unauthorized", "The provider rejected this key.")]
    [InlineData(HttpStatusCode.NotFound, "unavailable", "This key cannot read that data.")]
    [InlineData(HttpStatusCode.InternalServerError, "unavailable", "The provider returned HTTP 500.")]
    public async Task ErrorMappingCoversStatusCodes(HttpStatusCode code, string status, string message)
    {
        var db = TempDb();
        try
        {
            var handler = new StubHandler((_, _) => new HttpResponseMessage(code));
            using var service = CreateService(db, handler);
            await service.CreateAsync("deepseek", "DS", "deepseek-key-12345678");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal(status, snapshot.Status);
            Assert.Equal(message, snapshot.Message);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task RateLimitedRespectsRetryAfterAndCaches()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((_, _) =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return response;
            });
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            using var service = CreateService(db, handler, clock);
            await service.CreateAsync("deepseek", "DS", "deepseek-key-12345678");
            var first = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("rate_limited", first.Status);
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(61));
            var second = Assert.Single((await service.GetReportAsync(refresh: true)).Accounts);
            Assert.Equal("rate_limited", second.Status);
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(120));
            var third = Assert.Single((await service.GetReportAsync(refresh: true)).Accounts);
            Assert.Equal("rate_limited", third.Status);
            Assert.Equal(2, calls);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task OversizeInvalidAndTimeoutMapToGenericMessages()
    {
        var db = TempDb();
        try
        {
            var big = new string('x', 1024 * 1024 + 1);
            var oversize = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(big, Encoding.UTF8, "application/json")
            });
            using (var service = CreateService(db, oversize))
            {
                await service.CreateAsync("deepseek", "DS", "deepseek-key-12345678");
                var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
                Assert.Equal("The provider's response could not be read.", snapshot.Message);
            }
            Cleanup(db);

            var invalid = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", Encoding.UTF8, "application/json")
            });
            using (var service = CreateService(db, invalid))
            {
                await service.CreateAsync("deepseek", "DS", "deepseek-key-12345678");
                var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
                Assert.Equal("The provider's response could not be read.", snapshot.Message);
            }
            Cleanup(db);

            var timeout = new StubHandler((_, _) => throw new TaskCanceledException("simulated timeout"));
            using (var service = CreateService(db, timeout))
            {
                await service.CreateAsync("deepseek", "DS", "deepseek-key-12345678");
                var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
                Assert.Equal("The provider could not be reached.", snapshot.Message);
            }
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((_, _) =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://evil.example/steal");
                return response;
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("deepseek", "DS", "deepseek-key-12345678");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("unavailable", snapshot.Status);
            Assert.Equal("The provider returned HTTP 302.", snapshot.Message);
            Assert.Equal(1, calls);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task KeyNeverAppearsInSnapshotsOrDatabaseFile()
    {
        var db = TempDb();
        const string secret = "sk-live-secret-value-9f8e7d6c5b4a394857";
        try
        {
            var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"5.00"}]}""",
                    Encoding.UTF8, "application/json")
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("deepseek", "Main", secret);
            var report = await service.GetReportAsync();
            var list = await service.ListAsync();
            var reportJson = JsonSerializer.Serialize(report);
            var listJson = JsonSerializer.Serialize(list);
            Assert.DoesNotContain(secret, reportJson);
            Assert.DoesNotContain(secret, listJson);
            foreach (var snapshot in report.Accounts)
                Assert.DoesNotContain(secret, JsonSerializer.Serialize(snapshot));
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.False(FileContains(db, secret));
        }
        finally { Cleanup(db); }
    }

    [Theory]
    [InlineData("nope", "Label", "valid-key-12345678")]
    [InlineData("deepseek", "", "valid-key-12345678")]
    [InlineData("deepseek", "ok", "short")]
    [InlineData("deepseek", "ok", "has space key 12345678")]
    [InlineData("deepseek", "ok", "tab\tkey-12345678")]
    [InlineData("deepseek", "ok", "non-ascii-é-12345678")]
    public async Task ValidationRejectsBadShapes(string provider, string label, string key)
    {
        var db = TempDb();
        try
        {
            using var service = CreateService(db);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(provider, label, key));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task AtMostSixteenAccounts()
    {
        var db = TempDb();
        try
        {
            using var service = CreateService(db);
            for (var i = 0; i < 16; i++)
                await service.CreateAsync("deepseek", $"A{i}", $"valid-key-number-{i:D4}-xyz");
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("deepseek", "Extra", "valid-key-extra-12345678"));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task RenameKeepsKeyButReplacementChangesIt()
    {
        var db = TempDb();
        try
        {
            var seenKeys = new List<string?>();
            var handler = new StubHandler((request, _) =>
            {
                seenKeys.Add(request.Headers.Authorization?.Parameter);
                if (request.Headers.Authorization?.Parameter == "first-key-12345678")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"is_available":true,"balance_infos":[]}""", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            });
            using var service = CreateService(db, handler);
            var created = await service.CreateAsync("deepseek", "Main", "first-key-12345678");
            Assert.Equal("ok", Assert.Single((await service.GetReportAsync()).Accounts).Status);

            var renamed = await service.UpdateAsync(created.Id, "Renamed", null);
            Assert.Equal("Renamed", renamed.Label);
            Assert.Equal("ok", Assert.Single((await service.GetReportAsync()).Accounts).Status);

            await service.UpdateAsync(created.Id, "Renamed", "second-key-1234567890");
            var after = Assert.Single((await service.GetReportAsync(refresh: true)).Accounts);
            // A replaced key refetches: the rejected key surfaces as unauthorized with a generic message.
            Assert.Equal("unauthorized", after.Status);
            Assert.Equal("The provider rejected this key.", after.Message);
            Assert.Contains("second-key-1234567890", seenKeys);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task DeleteRemovesRowAndCache()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((_, _) =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"is_available":true,"balance_infos":[]}""", Encoding.UTF8, "application/json")
                };
            });
            using var service = CreateService(db, handler);
            var created = await service.CreateAsync("deepseek", "Main", "deepseek-key-12345678");
            Assert.Single((await service.GetReportAsync()).Accounts);
            await service.DeleteAsync(created.Id);
            Assert.Empty(await service.ListAsync());
            Assert.Empty((await service.GetReportAsync()).Accounts);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync(created.Id));
            Assert.Equal(1, calls);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task CacheAndRefreshThrottlePerAccount()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((_, _) =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"is_available":true,"balance_infos":[]}""", Encoding.UTF8, "application/json")
                };
            });
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            using var service = CreateService(db, handler, clock);
            await service.CreateAsync("deepseek", "Main", "deepseek-key-12345678");
            await service.GetReportAsync();
            Assert.Equal(1, calls);
            await service.GetReportAsync();
            Assert.Equal(1, calls);
            await service.GetReportAsync(refresh: true);
            Assert.Equal(1, calls);
            clock.Advance(TimeSpan.FromSeconds(61));
            await service.GetReportAsync(refresh: true);
            Assert.Equal(2, calls);
            clock.Advance(TimeSpan.FromMinutes(5));
            await service.GetReportAsync();
            Assert.Equal(3, calls);
            await Task.WhenAll(service.GetReportAsync(), service.GetReportAsync());
            Assert.Equal(3, calls);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task StaleSnapshotSurvivesTransientFailure()
    {
        var db = TempDb();
        try
        {
            var fail = false;
            var handler = new StubHandler((_, _) =>
            {
                if (fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"5.00"}]}""",
                        Encoding.UTF8, "application/json")
                };
            });
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            using var service = CreateService(db, handler, clock);
            await service.CreateAsync("deepseek", "Main", "deepseek-key-12345678");
            var first = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", first.Status);
            fail = true;
            clock.Advance(TimeSpan.FromMinutes(6));
            var stale = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", stale.Status);
            Assert.Contains("last successful", stale.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(stale.Balances);
        }
        finally { Cleanup(db); }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    [Fact]
    public async Task OverdrawnMoonshotBalanceIsReportedNotRejected()
    {
        var db = TempDb();
        try
        {
            var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"code":0,"data":{"available_balance":-1.25,"voucher_balance":0,"cash_balance":-1.25},"scode":"0x0","status":true}""",
                    Encoding.UTF8, "application/json")
            });
            using var service = CreateService(db, handler);
            await service.CreateAsync("moonshot", "Kimi", "moonshot-key-12345678");
            var snapshot = Assert.Single((await service.GetReportAsync()).Accounts);
            Assert.Equal("ok", snapshot.Status);
            Assert.Contains(snapshot.Balances, b => b.Kind == "available" && b.Amount == -1.25m);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task KeyThatCannotBeDecryptedIsNeverSentAndAsksForReplacement()
    {
        var db = TempDb();
        try
        {
            var calls = 0;
            var handler = new StubHandler((_, _) => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });
            using (var writer = CreateService(db, handler))
                await writer.CreateAsync("deepseek", "Other machine", "deepseek-key-12345678");
            // A different protector key stands in for a database copied from another user or machine.
            using var reader = CreateService(db, handler);
            var snapshot = Assert.Single((await reader.GetReportAsync()).Accounts);
            Assert.Equal("unavailable", snapshot.Status);
            Assert.Contains("could not be decrypted", snapshot.Message);
            Assert.Equal(0, calls);
        }
        finally { Cleanup(db); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public int Calls { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => respond(request))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(respond(request, cancellationToken));
        }
    }
}
