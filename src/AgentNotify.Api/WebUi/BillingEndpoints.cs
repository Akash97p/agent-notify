using System.Text.Json;
using AgentNotify.Core.Billing;
using AgentNotify.Core.Delivery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// Stored provider keys shown as balance or spend from each provider's official API.
/// Secrets are write-only: no response carries a key or any part of one.
/// </summary>
public static class BillingEndpoints
{
    public static void Map(WebApplication app, WebUiOptions options)
    {
        var billing = options.Billing ?? CreateDefault(options);

        app.MapGet($"{WebUiEndpoints.BasePath}/api/billing", async (CancellationToken ct) =>
            Results.Json(await billing.GetReportAsync(cancellationToken: ct), WebUiEndpoints.JsonOptions));

        app.MapPost($"{WebUiEndpoints.BasePath}/api/billing/refresh", async (CancellationToken ct) =>
            Results.Json(await billing.GetReportAsync(refresh: true, cancellationToken: ct), WebUiEndpoints.JsonOptions));

        app.MapGet($"{WebUiEndpoints.BasePath}/api/billing/accounts", async (CancellationToken ct) =>
        {
            var accounts = await billing.ListAsync(ct);
            return Results.Json(new
            {
                accounts = accounts.Select(ToListItem).ToArray(),
                providers = BillingCatalog.All.Select(p => new
                {
                    id = p.Id,
                    display_name = p.DisplayName,
                    host = p.Host,
                    shows = p.Shows,
                    key_type = p.KeyType,
                    docs_url = p.DocsUrl
                }).ToArray()
            }, WebUiEndpoints.JsonOptions);
        });

        app.MapPost($"{WebUiEndpoints.BasePath}/api/billing/accounts", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var body = await ReadAsync<CreateBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            if (body.AcknowledgeRisk != true)
                return Error("You must acknowledge the storage risk to add an API key.");
            try
            {
                var saved = await billing.CreateAsync(body.Provider, body.Label, body.ApiKey, http.RequestAborted);
                return Results.Json(ToListItem(saved), WebUiEndpoints.JsonOptions, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException error)
            {
                return Error(error.Message);
            }
        }));

        app.MapPut($"{WebUiEndpoints.BasePath}/api/billing/accounts/{{id}}", async (string id, HttpContext http) =>
        {
            var body = await ReadAsync<UpdateBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                var saved = await billing.UpdateAsync(id, body.Label, body.ApiKey, http.RequestAborted);
                return Results.Json(ToListItem(saved), WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException)
            {
                return Error("That account was not found.", StatusCodes.Status404NotFound);
            }
            catch (ArgumentException error)
            {
                return Error(error.Message);
            }
        });

        app.MapDelete($"{WebUiEndpoints.BasePath}/api/billing/accounts/{{id}}", async (string id, CancellationToken ct) =>
        {
            try
            {
                await billing.DeleteAsync(id, ct);
                return Results.Json(new { deleted = id }, WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException)
            {
                return Error("That account was not found.", StatusCodes.Status404NotFound);
            }
        });
    }

    /// <summary>
    /// The service for hosts that do not supply one. It uses the same persistent protector as provider
    /// profiles; a failure to create it is not papered over with a throwaway key, which would store
    /// keys that no later start could decrypt.
    /// </summary>
    private static BillingService CreateDefault(WebUiOptions options) =>
        new(new BillingAccountRepository(options.ConfigStore.DbPath),
            SecretProtectorFactory.Create(options.ConfigStore.ConfigDir));

    private static object ToListItem(BillingAccount account) => new
    {
        id = account.Id,
        provider = account.Provider,
        label = account.Label,
        created_at = account.CreatedAt,
        updated_at = account.UpdatedAt,
        has_key = true
    };

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, WebUiEndpoints.JsonOptions, statusCode: status);

    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(WebUiEndpoints.JsonOptions, http.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private sealed class CreateBody
    {
        public string? Provider { get; set; }
        public string? Label { get; set; }
        public string? ApiKey { get; set; }
        public bool? AcknowledgeRisk { get; set; }
    }

    private sealed class UpdateBody
    {
        public string? Label { get; set; }
        public string? ApiKey { get; set; }
    }
}
