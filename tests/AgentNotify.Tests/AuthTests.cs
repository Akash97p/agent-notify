using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentNotify.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task HealthProbe_Unauthenticated_Succeeds()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await c.GetAsync($"{fx.BaseUrl}/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task V1_WithoutToken_Returns401()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task V1_WithWrongToken_Returns401()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.ClientWithToken("wrong-token");
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task V1_WithCorrectToken_Succeeds()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.AuthedClient();
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task V1_BearerScheme_CaseInsensitive()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"bearer {fx.Token}");
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task V1_MissingBearerPrefix_401()
    {
        await using var fx = await ApiFixture.StartAsync();
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", fx.Token);
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task TokenAuth_ConstantTime_DifferentLengthsIsFalse()
    {
        // Exercised indirectly: wrong-length token must not compare equal.
        await using var fx = await ApiFixture.StartAsync();
        using var c = fx.ClientWithToken(fx.Token + "extra");
        var resp = await c.GetAsync($"{fx.BaseUrl}/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
