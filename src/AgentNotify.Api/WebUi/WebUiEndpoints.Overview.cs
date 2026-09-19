using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

/// <summary>Web UI routes: overview.</summary>
public sealed partial class WebUiEndpoints
{
    private void MapOverview()
    {
        app.MapGet($"{BasePath}/api/overview", async (CancellationToken ct) =>
        {
            var providers = await options.Providers.ListAsync(ct);
            var routes = await options.Routes.ListAsync(ct);
            var delivery = await options.Dispatcher.GetDiagnosticsAsync(ct);
            var pending = interactions is null
                ? 0
                : (await interactions.ListAsync(new InteractionQuery { PendingOnly = true, Limit = 500 }, ct)).Count;
            return Results.Json(new
            {
                version,
                pid = Environment.ProcessId,
                uptime_seconds = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                api_url = $"http://127.0.0.1:{port}",
                data_directory = options.ConfigStore.ConfigDir,
                platform = PlatformName(),
                secret_protection = options.SecretProtection,
                desktop_surface = options.DesktopSurface,
                capabilities = new { toast_placement = options.SupportsToastPlacement, sounds = options.SupportsSounds, questions = interactions is not null },
                counts = new
                {
                    active_notifications = await repository.CountActiveAsync(ct),
                    pending_questions = pending,
                    providers = providers.Count,
                    enabled_providers = providers.Count(p => p.Enabled),
                    routes = routes.Count,
                    enabled_routes = routes.Count(r => r.Enabled)
                },
                delivery = DeliveryJson(delivery)
            }, JsonOptions);
        });

        // Local agent logs are read-only inputs. This route never accepts a filesystem path.
        app.MapGet($"{BasePath}/api/usage", async (HttpContext http, CancellationToken ct) =>
        {
            var requested = http.Request.Query["days"].ToString();
            var days = requested switch { "7" => 7, "30" or "" => 30, "all" => 0, _ => -1 };
            if (days < 0) return Error("Choose 7 days, 30 days, or all history.");
            var report = await usage.GetReportAsync(days, ct);
            return Results.Json(report, JsonOptions);
        });

        // Provider/account snapshots are separate from local token history. Manual refresh is
        // a same-origin POST so unrelated pages cannot trigger credential-backed probes.
        app.MapGet($"{BasePath}/api/quota", async (CancellationToken ct) =>
            Results.Json(await quota.GetReportAsync(cancellationToken: ct), JsonOptions));
        app.MapPost($"{BasePath}/api/quota/refresh", async (CancellationToken ct) =>
            Results.Json(await quota.GetReportAsync(refresh: true, cancellationToken: ct), JsonOptions));

        // The native macOS client receives only normalized quota fields and its presentation
        // settings. It never reads config.json, the API bearer token, or an agent credential file.
        app.MapGet($"{BasePath}/api/menu-bar", async (CancellationToken ct) =>
            Results.Json(MenuBarQuotaProjector.Project(
                await quota.GetReportAsync(cancellationToken: ct), config.MacMenuBar), JsonOptions));
        app.MapPost($"{BasePath}/api/menu-bar/refresh", async (CancellationToken ct) =>
            Results.Json(MenuBarQuotaProjector.Project(
                await quota.GetReportAsync(refresh: true, cancellationToken: ct), config.MacMenuBar), JsonOptions));
        app.MapGet($"{BasePath}/api/menu-bar/settings", () =>
            Results.Json(MenuBarSettingsJson(config), JsonOptions));
        app.MapPut($"{BasePath}/api/menu-bar/settings", async (HttpContext http) =>
        {
            var body = await ReadAsync<MenuBarSettingsBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                lock (quotaAccountsGate)
                {
                    var known = MonitoredDetectedAccounts().Concat(config.QuotaAccounts)
                        .Select(account => account.Id).ToHashSet(StringComparer.Ordinal);
                    var previous = config.MacMenuBar;
                    config.MacMenuBar = ValidateMenuBarSettings(previous, body, known);
                    try { options.ConfigStore.Save(config); }
                    catch { config.MacMenuBar = previous; throw; }
                }
                Notify(options, config, false, logger);
                return Results.Json(MenuBarSettingsJson(config), JsonOptions);
            }
            catch (ArgumentException error) { return Error(error.Message); }
        });
        app.MapPost($"{BasePath}/api/menu-bar/disable", () =>
        {
            lock (quotaAccountsGate)
            {
                if (config.MacMenuBar.Enabled)
                {
                    var previous = config.MacMenuBar;
                    config.MacMenuBar = new MacMenuBarSettings
                    {
                        Enabled = false,
                        RefreshMinutes = previous.RefreshMinutes,
                        AccountIds = [.. previous.AccountIds]
                    };
                    try { options.ConfigStore.Save(config); }
                    catch { config.MacMenuBar = previous; throw; }
                }
            }
            Notify(options, config, false, logger);
            return Results.Json(MenuBarSettingsJson(config), JsonOptions);
        });

        static string? WslName(QuotaAccountDefinition account) =>
            QuotaAccountDefinition.DetectedWslDistribution(account.Id);

        app.MapGet($"{BasePath}/api/quota/accounts", () =>
        {
            var detected = DetectedAccounts();
            return Results.Json(new
            {
                accounts = detected.Where(account => !config.RemovedQuotaAccounts.Contains(account.Id))
                    .Select(account => new { account.Id, account.Provider, account.Label, account.Directory, IsDefault = true, Wsl = WslName(account) })
                    .Concat(config.QuotaAccounts.Select(account => new
                        { account.Id, account.Provider, account.Label, account.Directory, IsDefault = false, Wsl = (string?)null })).ToArray(),
                // A removed account keeps its row while its profile is missing, without a directory.
                removed = config.RemovedQuotaAccounts.Select(id => detected.FirstOrDefault(account => account.Id == id) ??
                        new QuotaAccountDefinition(id, id[..id.IndexOf(':')],
                            DetectedLabel(id) ?? QuotaAccountDefinition.FallbackLabel(id), ""))
                    .Select(account => new { account.Id, account.Provider, account.Label, account.Directory, Wsl = WslName(account) })
                    .ToArray()
            }, JsonOptions);
        });

        app.MapPost($"{BasePath}/api/quota/accounts", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var body = await ReadAsync<QuotaAccountBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                QuotaAccountDefinition account;
                lock (quotaAccountsGate)
                {
                    if (config.QuotaAccounts.Count >= 16) return Error("Up to 16 additional accounts can be monitored.");
                    account = QuotaAccountDefinition.Create(body.Provider, body.Label, body.Directory,
                        config.QuotaAccounts.Concat(MonitoredDetectedAccounts()));
                    SaveQuotaAccounts([.. config.QuotaAccounts, account], config.RemovedQuotaAccounts);
                }
                Notify(options, config, false, logger);
                return Results.Json(account, JsonOptions, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException error) { return Error(error.Message); }
        }));

        // Renames an account and, when a directory is given, points it at that profile. A built-in or
        // discovered account moved to another directory becomes an added account and leaves its
        // detected entry removed, so the move survives restarts and rediscovery.
        app.MapPut($"{BasePath}/api/quota/accounts/{{id}}", async (string id, HttpContext http) =>
        {
            var body = await ReadAsync<QuotaAccountBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                var label = QuotaAccountDefinition.NormalizeLabel(body.Label);
                QuotaAccountDefinition saved;
                lock (quotaAccountsGate)
                {
                    if (QuotaAccountDefinition.IsDetectedAccountId(id))
                    {
                        if (config.RemovedQuotaAccounts.Contains(id))
                            return Error("That account was removed. Restore it first.", StatusCodes.Status404NotFound);
                        var provider = id[..id.IndexOf(':')];
                        var current = DetectedAccounts().FirstOrDefault(account => account.Id == id);
                        var moved = !string.IsNullOrWhiteSpace(body.Directory) && current is not null &&
                            !QuotaAccountDefinition.SameDirectory(body.Directory.Trim(), current.Directory) &&
                            !QuotaAccountDefinition.SameDirectory(QuotaAccountDefinition.NormalizeDirectory(body.Directory), current.Directory);
                        if (moved)
                        {
                            if (config.QuotaAccounts.Count >= 16) return Error("Up to 16 additional accounts can be monitored.");
                            saved = QuotaAccountDefinition.Create(provider, label, body.Directory,
                                config.QuotaAccounts.Concat(MonitoredDetectedAccounts().Where(account => account.Id != id)));
                            SaveQuotaAccounts([.. config.QuotaAccounts, saved], [.. config.RemovedQuotaAccounts, id]);
                        }
                        else
                        {
                            // Built-in accounts are keyed by provider; discovered WSL accounts by their full ID.
                            var key = id.EndsWith(":default", StringComparison.Ordinal) ? provider : id;
                            var previous = config.DefaultQuotaAccountLabels;
                            config.DefaultQuotaAccountLabels = new Dictionary<string, string>(previous, StringComparer.Ordinal)
                                { [key] = label };
                            try { options.ConfigStore.Save(config); }
                            catch { config.DefaultQuotaAccountLabels = previous; throw; }
                            saved = (current ?? new QuotaAccountDefinition(id, provider, label, "")) with { Label = label };
                        }
                    }
                    else
                    {
                        var index = config.QuotaAccounts.FindIndex(account => account.Id == id);
                        if (index < 0) return Error("That account was not found.", StatusCodes.Status404NotFound);
                        var updated = config.QuotaAccounts.ToList();
                        saved = string.IsNullOrWhiteSpace(body.Directory)
                            ? updated[index] with { Label = label }
                            : QuotaAccountDefinition.Create(updated[index].Provider, label, body.Directory,
                                updated.Where(account => account.Id != id).Concat(MonitoredDetectedAccounts())) with { Id = id };
                        updated[index] = saved;
                        SaveQuotaAccounts(updated, config.RemovedQuotaAccounts);
                    }
                }
                Notify(options, config, false, logger);
                return Results.Json(saved, JsonOptions);
            }
            catch (ArgumentException error) { return Error(error.Message); }
        });

        // Added accounts are deleted. Built-in and discovered ones are only hidden, because they would
        // otherwise reappear on the next discovery; the agent profile and its sign-in stay untouched.
        app.MapDelete($"{BasePath}/api/quota/accounts/{{id}}", (string id) =>
        {
            lock (quotaAccountsGate)
            {
                if (QuotaAccountDefinition.IsDetectedAccountId(id))
                {
                    if (config.RemovedQuotaAccounts.Contains(id))
                        return Error("That account was already removed.", StatusCodes.Status404NotFound);
                    SaveQuotaAccounts(config.QuotaAccounts, [.. config.RemovedQuotaAccounts, id]);
                }
                else
                {
                    var updated = config.QuotaAccounts.Where(account => account.Id != id).ToList();
                    if (updated.Count == config.QuotaAccounts.Count)
                        return Error("That account was not found.", StatusCodes.Status404NotFound);
                    SaveQuotaAccounts(updated, config.RemovedQuotaAccounts);
                }
            }
            Notify(options, config, false, logger);
            return Results.Json(new { deleted = id }, JsonOptions);
        });

        // The OpenCode Go plan's renewal day anchors the monthly estimate to the billing cycle.
        app.MapPut($"{BasePath}/api/quota/opencode-go", async (HttpContext http) =>
        {
            var body = await ReadAsync<OpenCodeGoBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            if (body.RenewalDay is not null && !AgentNotify.Core.Config.OpenCodeGoBillingCycle.IsValidRenewalDay(body.RenewalDay))
                return Error("Choose a renewal day from 1 to 31, or clear it.");
            lock (quotaAccountsGate)
            {
                var previous = config.OpenCodeGoRenewalDay;
                config.OpenCodeGoRenewalDay = body.RenewalDay;
                try { options.ConfigStore.Save(config); }
                catch { config.OpenCodeGoRenewalDay = previous; throw; }
            }
            Notify(options, config, false, logger);
            return Results.Json(new { renewal_day = config.OpenCodeGoRenewalDay }, JsonOptions);
        });

        app.MapPost($"{BasePath}/api/quota/accounts/{{id}}/restore", (string id) =>
        {
            lock (quotaAccountsGate)
            {
                if (!config.RemovedQuotaAccounts.Contains(id))
                    return Error("That account is not removed.", StatusCodes.Status404NotFound);
                SaveQuotaAccounts(config.QuotaAccounts, config.RemovedQuotaAccounts.Where(item => item != id).ToList());
            }
            Notify(options, config, false, logger);
            return Results.Json(new { restored = id }, JsonOptions);
        });
        BillingEndpoints.Map(app, options);
        AgentNotify.Api.Router.RouterEndpoints.Map(app, options, config, port);
    }
}
