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
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Core.Usage;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// The local web UI: a browser front end for everything the Windows Settings window and
/// Notification Center manage, served by the broker itself so it works wherever the broker runs.
/// </summary>
/// <remarks>
/// <para>
/// There is no sign-in, by design: like the Windows tray app, the page trusts whoever is using this
/// computer. What it does defend against is other web sites reaching it through the browser.
/// </para>
/// <para>Checks, in the order <see cref="Guard"/> applies them:</para>
/// <list type="number">
/// <item>The listener is loopback-only, like the rest of the API.</item>
/// <item>The <c>Host</c> header must name this loopback listener. A DNS-rebinding page reaches the
/// port under its own host name and is refused before any handler runs.</item>
/// <item>Every state-changing request must carry <c>X-AgentNotify-UI: 1</c> and, when the browser
/// sends one, a same-origin <c>Origin</c>. A cross-site form cannot set that header, and a
/// cross-site script cannot send it without a CORS preflight this server never grants.</item>
/// <item>Provider secrets are write-only: no response carries a stored secret value.</item>
/// </list>
/// </remarks>
public static class WebUiEndpoints
{
    public const string BasePath = "/ui";
    public const string CsrfHeader = "X-AgentNotify-UI";

    private const long MaximumSoundUploadBytes = ManagedSoundStore.MaxSoundBytes + 64 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>The address of the web interface on a broker listening on <paramref name="port"/>.</summary>
    public static string Url(int port) => $"http://127.0.0.1:{port}{BasePath}/";

    /// <summary>Runs the web UI's host and cross-site checks. Returns false when it has already responded.</summary>
    internal static async Task<bool> Guard(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsLoopbackHost(request.Host))
        {
            response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await response.WriteAsJsonAsync(new { error = "This interface is only served to the local machine address." }, JsonOptions);
            return false;
        }

        ApplySecurityHeaders(response, request.Path);

        // Routing treats "/ui" and "/ui/" alike, so the bare form is redirected here: the page's
        // relative asset and API URLs only resolve beneath the trailing slash.
        if (request.Path.Value == BasePath)
        {
            response.Redirect($"{BasePath}/");
            return false;
        }

        if (!request.Path.StartsWithSegments($"{BasePath}/api"))
            return true;

        response.Headers.CacheControl = "no-store";

        var mutating = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
        if (mutating)
        {
            var origin = request.Headers.Origin.ToString();
            if (request.Headers[CsrfHeader] != "1" ||
                origin.Length > 0 && !IsLoopbackOrigin(origin, request.Host))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                await response.WriteAsJsonAsync(new { error = "Cross-site request refused." }, JsonOptions);
                return false;
            }
        }


        return true;
    }

    internal static bool IsLoopbackHost(HostString host)
    {
        // An SSH local forward keeps the browser's local port in Host, which need not match
        // the broker's listening port. The listener itself remains bound to loopback.
        if (!host.HasValue || host.Port is not > 0) return false;
        return host.Host is "127.0.0.1" or "localhost" or "[::1]";
    }

    private static bool IsLoopbackOrigin(string origin, HostString host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp &&
        string.Equals(uri.Authority, host.Value, StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static void ApplySecurityHeaders(HttpResponse response, PathString path)
    {
        var headers = response.Headers;
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    }

    public static void Map(
        WebApplication app,
        WebUiOptions options,
        AgentNotifyConfig config,
        INotificationRepository repository,
        NotificationService service,
        InteractionService? interactions,
        ApiCallbacks? callbacks,
        IAppLogger? logger,
        int port,
        string version,
        DateTimeOffset startedAt)
    {
        var pairings = new RelayPairingSessions();
        var forms = new ProviderFormService(options.Providers);
        var sounds = new ManagedSoundStore(options.ConfigStore.SoundsDir);
        var usage = options.Usage ?? new LocalUsageService();
        var quota = options.Quota ?? new AgentNotify.Core.Quota.LiveQuotaService(
            accounts: () => config.QuotaAccounts.ToArray(), usage: usage);
        var quotaAccountsGate = new object();
        app.Lifetime.ApplicationStopping.Register(pairings.Dispose);

        // ---- overview ----------------------------------------------------------------------

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

        app.MapGet($"{BasePath}/api/quota/accounts", () => Results.Json(new
        {
            accounts = new[] { QuotaAccountDefinition.Default("codex"), QuotaAccountDefinition.Default("claude_code") }
                .Select(account => new { account.Id, account.Provider, account.Label, account.Directory, IsDefault = true })
                .Concat(config.QuotaAccounts.Select(account => new
                    { account.Id, account.Provider, account.Label, account.Directory, IsDefault = false })).ToArray()
        }, JsonOptions));

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
                        config.QuotaAccounts.Concat([QuotaAccountDefinition.Default("codex"),
                            QuotaAccountDefinition.Default("claude_code")]));
                    var previous = config.QuotaAccounts;
                    config.QuotaAccounts = [.. previous, account];
                    try { options.ConfigStore.Save(config); }
                    catch { config.QuotaAccounts = previous; throw; }
                }
                Notify(options, config, false, logger);
                return Results.Json(account, JsonOptions, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException error) { return Error(error.Message); }
        }));

        app.MapDelete($"{BasePath}/api/quota/accounts/{{id}}", (string id) =>
        {
            lock (quotaAccountsGate)
            {
                var previous = config.QuotaAccounts;
                var updated = previous.Where(account => account.Id != id).ToList();
                if (updated.Count == previous.Count) return Error("That additional account was not found.", StatusCodes.Status404NotFound);
                config.QuotaAccounts = updated;
                try { options.ConfigStore.Save(config); }
                catch { config.QuotaAccounts = previous; throw; }
            }
            Notify(options, config, false, logger);
            return Results.Json(new { deleted = id }, JsonOptions);
        });

        // ---- settings ----------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/settings", () => Results.Json(SettingsJson(config), JsonOptions));

        app.MapPut($"{BasePath}/api/settings", async (HttpContext http) =>
        {
            var body = await ReadAsync<SettingsBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                var originalPort = config.Port;
                ApplySettings(config, body);
                options.ConfigStore.Save(config);
                var restart = config.Port != originalPort;
                Notify(options, config, restart, logger);
                return Results.Json(new { settings = SettingsJson(config), restart_required = restart }, JsonOptions);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        });

        app.MapGet($"{BasePath}/api/types", () => Results.Json(TypesJson(config), JsonOptions));

        app.MapPut($"{BasePath}/api/types", async (HttpContext http) =>
        {
            var body = await ReadAsync<TypesBody>(http);
            if (body?.Custom is null) return Error("The request body is not valid JSON.");
            try
            {
                var definitions = ValidateTypes(body.Custom);
                var kept = definitions.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var removed in config.CustomNotificationTypes.Select(d => d.Id).Where(id => !kept.Contains(id)).ToList())
                    config.TypeSoundFiles.Remove(removed);
                config.CustomNotificationTypes = definitions;
                options.ConfigStore.Save(config);
                Notify(options, config, false, logger);
                return Results.Json(TypesJson(config), JsonOptions);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        });

        // ---- sounds ------------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/sounds", () =>
        {
            var builtIn = BuiltInTones.All.Select(t => new { file_name = t.FileName, display_name = t.DisplayName, available = sounds.Resolve(t.FileName) is not null });
            var imported = Directory.Exists(sounds.DirectoryPath)
                ? Directory.EnumerateFiles(sounds.DirectoryPath)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && !BuiltInTones.Contains(name) &&
                                   (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
            return Results.Json(new { built_in = builtIn, imported }, JsonOptions);
        });

        app.MapPost($"{BasePath}/api/sounds", async (HttpContext http) =>
        {
            var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaximumSoundUploadBytes;
            if (!http.Request.HasFormContentType) return Error("Upload a WAV or MP3 file.");

            IFormFile? file;
            try
            {
                file = (await http.Request.ReadFormAsync(http.RequestAborted)).Files.GetFile("file");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
            {
                return Error("Sound files must be at most 10 MB.");
            }
            if (file is null || file.Length == 0) return Error("Upload a WAV or MP3 file.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not ".wav" and not ".mp3") return Error("Only WAV and MP3 sound files are supported.");
            var baseName = Path.GetFileNameWithoutExtension(SafeFileName.Last(file.FileName));
            var staging = Path.Combine(Path.GetTempPath(), $"agentnotify-upload-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var temporary = Path.Combine(staging, (string.IsNullOrWhiteSpace(baseName) ? "sound" : baseName) + extension);
            try
            {
                await using (var target = File.Create(temporary))
                    await file.CopyToAsync(target, http.RequestAborted);
                return Results.Json(new { file_name = sounds.Import(temporary) }, JsonOptions);
            }
            catch (InvalidOperationException exception)
            {
                return Error(exception.Message);
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        });

        app.MapGet($"{BasePath}/api/sounds/{{fileName}}", (string fileName) =>
        {
            var path = sounds.Resolve(fileName);
            if (path is null) return Error("That sound is not available on this machine.", StatusCodes.Status404NotFound);
            var type = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg" : "audio/wav";
            return Results.File(path, type);
        });

        // ---- attention ---------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/notifications", async (HttpContext http, CancellationToken ct) =>
        {
            var attention = http.Request.Query["view"] != "recent";
            var items = await repository.QueryAsync(new NotificationQuery
            {
                Unresolved = attention ? true : null,
                Limit = int.TryParse(http.Request.Query["limit"], out var limit) ? Math.Clamp(limit, 1, 500) : 200
            }, ct);
            return Results.Json(items.Select(DtoMapper.ToDto), JsonOptions);
        });

        app.MapPost($"{BasePath}/api/notifications/{{id}}/{{action}}", async (string id, string action, CancellationToken ct) =>
        {
            var status = action switch
            {
                "resolve" => NotificationStatus.Resolved,
                "dismiss" => NotificationStatus.Dismissed,
                _ => (NotificationStatus?)null
            };
            if (status is null) return Error("Unknown action.", StatusCodes.Status404NotFound);
            var result = await service.UpdateStatusAsync(id, new UpdateNotificationRequest { Status = status.Value }, ct);
            if (result.NotFound) return Error("That notification no longer exists.", StatusCodes.Status404NotFound);
            if (result.Error is not null) return Error(result.Error);
            try { callbacks?.Updated?.Invoke(result.Value!); }
            catch (Exception exception) { logger?.Error("Notification UI callback failed", exception); }
            return Results.Json(DtoMapper.ToDto(result.Value!), JsonOptions);
        });

        // ---- questions ---------------------------------------------------------------------

        if (interactions is not null)
        {
            app.MapGet($"{BasePath}/api/interactions", async (HttpContext http, CancellationToken ct) =>
            {
                var pendingOnly = http.Request.Query["view"] != "recent";
                var items = await interactions.ListAsync(new InteractionQuery { PendingOnly = pendingOnly, Limit = 100 }, ct);
                return Results.Json(items.Select(ToPageDto), JsonOptions);
            });

            app.MapPost($"{BasePath}/api/interactions/{{id}}/respond", async (string id, HttpContext http) =>
            {
                var body = await ReadAsync<AnswerBody>(http);
                if (body is null) return Error("The request body is not valid JSON.");
                var current = await interactions.GetAsync(id, http.RequestAborted);
                if (current.NotFound) return Error("That question no longer exists.", StatusCodes.Status404NotFound);

                // The digest comes from the page, so an answer can only apply to the exact question
                // the person read. The nonce is filled in here; it never leaves the broker.
                var result = await interactions.RespondAsync(id, new RespondInteractionRequest
                {
                    ResponseId = Guid.NewGuid().ToString("N"),
                    RequestDigest = body.RequestDigest ?? "",
                    Nonce = current.Value!.Nonce,
                    ChoiceId = body.ChoiceId,
                    Text = body.Text,
                    Source = "web"
                }, http.RequestAborted);
                if (result.NotFound) return Error("That question no longer exists.", StatusCodes.Status404NotFound);
                if (result.Error is not null)
                    return Error(result.Error, result.Error == "interaction already answered" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
                return Results.Json(ToPageDto(result.Value!), JsonOptions);
            });

            app.MapPost($"{BasePath}/api/interactions/{{id}}/cancel", async (string id, CancellationToken ct) =>
            {
                var result = await interactions.CancelAsync(id, ct);
                return result.NotFound
                    ? Error("That question no longer exists.", StatusCodes.Status404NotFound)
                    : Results.Json(ToPageDto(result.Value!), JsonOptions);
            });
        }

        // ---- providers ---------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/provider-kinds", () => Results.Json(ProviderFormCatalog.All, JsonOptions));

        app.MapGet($"{BasePath}/api/providers", async (CancellationToken ct) =>
            Results.Json((await options.Providers.ListAsync(ct)).Select(ProviderJson), JsonOptions));

        // Typed explicitly: an expression-bodied (HttpContext) lambda binds to RequestDelegate, which
        // discards the returned IResult and answers 200 with an empty body.
        app.MapPost($"{BasePath}/api/providers", (Func<HttpContext, Task<IResult>>)(http => SaveProviderAsync(http, null)));

        app.MapPut($"{BasePath}/api/providers/{{id}}", (string id, HttpContext http) => SaveProviderAsync(http, id));

        app.MapDelete($"{BasePath}/api/providers/{{id}}", async (string id, CancellationToken ct) =>
        {
            await options.Providers.DeleteAsync(id, ct);
            return Results.Json(new { deleted = id }, JsonOptions);
        });

        app.MapPost($"{BasePath}/api/providers/{{id}}/test", async (string id, CancellationToken ct) =>
        {
            if ((await options.Providers.ListAsync(ct)).All(p => p.Id != id))
                return Error("That provider no longer exists.", StatusCodes.Status404NotFound);
            var result = await options.Dispatcher.TestProviderAsync(id, ct: ct);
            return Results.Json(new
            {
                succeeded = result.Succeeded,
                status_code = result.StatusCode,
                error_code = result.ErrorCode,
                message = result.Succeeded
                    ? $"Test delivered (provider status {result.StatusCode?.ToString() ?? "ok"})."
                    : result.ErrorCode switch
                    {
                        "no_devices_paired" => "Connected to the relay, but no phone is paired yet. Pair a phone from the relay console, then send a test.",
                        "relay_device_not_found" => "The selected Relay phone is no longer paired. Pair it again, then send a test.",
                        "relay_installation_identity_missing" => "This Relay profile predates installation identity. Connect it again.",
                        "provider_disabled" => "Enable the provider before sending a test.",
                        _ => $"Test failed: {result.ErrorCode ?? "unspecified"}."
                    }
            }, JsonOptions);
        });

        async Task<IResult> SaveProviderAsync(HttpContext http, string? id)
        {
            var body = await ReadAsync<ProviderBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            RelayPairingOutcome? pairing = null;
            if (!string.IsNullOrWhiteSpace(body.PairingId))
            {
                pairing = pairings.Take(body.PairingId);
                if (pairing is null)
                    return Error("That Relay connection is no longer waiting to be saved. Connect again.");
            }

            try
            {
                var saved = await forms.SaveAsync(id, body.Kind ?? "", new ProviderFormInput
                {
                    Name = body.Name ?? "",
                    Enabled = body.Enabled,
                    Values = body.Values ?? new Dictionary<string, string?>(),
                    Secrets = body.Secrets ?? new Dictionary<string, string?>(),
                    ClearSecrets = body.ClearSecrets ?? [],
                    Pairing = pairing
                }, http.RequestAborted);
                return Results.Json(ProviderJson(saved), JsonOptions);
            }
            catch (KeyNotFoundException)
            {
                return Error("That provider no longer exists.", StatusCodes.Status404NotFound);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                return Error(exception.Message);
            }
        }

        // ---- relay pairing -----------------------------------------------------------------

        app.MapPost($"{BasePath}/api/relay/pairings", async (HttpContext http) =>
        {
            var body = await ReadAsync<PairingBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            string? installId = null;
            if (!string.IsNullOrWhiteSpace(body.ProviderId))
            {
                var profile = (await options.Providers.ListAsync(http.RequestAborted)).FirstOrDefault(p => p.Id == body.ProviderId);
                installId = ProviderFormReader.ReadConfigString(profile, "install_id");
            }

            try
            {
                var snapshot = await pairings.StartAsync(body.RelayUrl ?? "", body.SenderName, body.AllowPrivateNetwork, installId, http.RequestAborted);
                return Results.Json(snapshot, JsonOptions);
            }
            catch (RelayPairingException exception)
            {
                return Error(RelayPairingSessions.Describe(exception), StatusCodes.Status502BadGateway);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        });

        app.MapGet($"{BasePath}/api/relay/pairings/{{id}}", (string id) =>
            pairings.Get(id) is { } snapshot
                ? Results.Json(snapshot, JsonOptions)
                : Error("That connection request is gone. Connect again.", StatusCodes.Status404NotFound));

        app.MapDelete($"{BasePath}/api/relay/pairings/{{id}}", (string id) =>
            Results.Json(new { cancelled = pairings.Cancel(id) }, JsonOptions));

        // ---- routes ------------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/routes", async (CancellationToken ct) =>
            Results.Json(await options.Routes.ListAsync(ct), JsonOptions));

        app.MapPost($"{BasePath}/api/routes", (Func<HttpContext, Task<IResult>>)(http => SaveRouteAsync(http, null)));
        app.MapPut($"{BasePath}/api/routes/{{id}}", (string id, HttpContext http) => SaveRouteAsync(http, id));

        app.MapDelete($"{BasePath}/api/routes/{{id}}", async (string id, CancellationToken ct) =>
        {
            await options.Routes.DeleteAsync(id, ct);
            return Results.Json(new { deleted = id }, JsonOptions);
        });

        async Task<IResult> SaveRouteAsync(HttpContext http, string? id)
        {
            var body = await ReadAsync<RouteBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            if (id is not null && (await options.Routes.ListAsync(http.RequestAborted)).All(r => r.Id != id))
                return Error("That route no longer exists.", StatusCodes.Status404NotFound);
            if (!Enum.TryParse<NotificationPriority>(body.MinimumPriority, ignoreCase: true, out var priority))
                return Error("Select a minimum priority.");
            try
            {
                var route = await options.Routes.SaveAsync(id, body.Name ?? "", body.ProviderId ?? "", body.Enabled,
                    priority, body.TypeId, body.Project, body.Agent, body.IncludeMessage, http.RequestAborted);
                return Results.Json(route, JsonOptions);
            }
            catch (ArgumentException exception)
            {
                return Error(exception.Message);
            }
        }

        app.MapGet($"{BasePath}/api/delivery", async (CancellationToken ct) =>
            Results.Json(DeliveryJson(await options.Dispatcher.GetDiagnosticsAsync(ct)), JsonOptions));

        // ---- agents ------------------------------------------------------------------------

        app.MapGet($"{BasePath}/api/agents", () => Results.Json(new
        {
            skills = AgentSkillCatalog.WithKnownLocations.Select(SkillJson),
            harnesses = HarnessCatalog.All.Select(target => new
            {
                id = target.Id,
                display_name = target.DisplayName,
                note = target.Note,
                command = $"agentnotify install-harness {target.Id}",
                ask_command = target.Id is "codex" or "claude" ? $"agentnotify install-harness {target.Id} --ask" : null
            })
        }, JsonOptions));

        app.MapPost($"{BasePath}/api/agents/skills/{{id}}", async (string id, HttpContext http) =>
        {
            var target = AgentSkillCatalog.Find(id);
            if (target is null || !target.HasDefaultLocation)
                return Error("Unknown agent.", StatusCodes.Status404NotFound);
            var body = await ReadAsync<SkillBody>(http);
            try
            {
                var root = AgentSkillCatalog.DefaultSkillsRoot(target);
                var result = SkillInstaller.Install(target.DisplayName, root, WebUiSkill.Files(target), body?.Force == true, dryRun: false);
                return Results.Json(new { success = result.Success, changed = result.Changed, message = result.Message, skill = SkillJson(target) },
                    statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict, options: JsonOptions);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Error(exception.Message);
            }
        });

        // ---- static front end --------------------------------------------------------------

        app.MapGet($"{BasePath}/{{**path}}", (string? path, HttpContext http) =>
        {
            path ??= "";
            if (path.StartsWith("api/", StringComparison.Ordinal))
                return Error("Not found.", StatusCodes.Status404NotFound);
            var asset = WebUiAssets.Find(path);
            if (asset is null && Path.HasExtension(path))
                return Results.NotFound();
            asset ??= WebUiAssets.Index;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(asset.Content, asset.ContentType);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, JsonOptions, statusCode: status);

    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(JsonOptions, http.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static void Notify(WebUiOptions options, AgentNotifyConfig config, bool restartRequired, IAppLogger? logger)
    {
        try { options.ConfigSaved?.Invoke(config, restartRequired); }
        catch (Exception exception) { logger?.Error("Web UI configuration callback failed", exception); }
    }

    private static string PlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        return "Linux";
    }

    private static object DeliveryJson(DeliveryDiagnosticSnapshot snapshot) => new
    {
        pending = snapshot.Pending,
        processing = snapshot.Processing,
        retry = snapshot.Retry,
        delivered = snapshot.Delivered,
        dead_letter = snapshot.DeadLetter,
        adapters = snapshot.RegisteredAdapters
    };

    private static object ProviderJson(ProviderProfile profile)
    {
        var relay = profile.Kind == "relay"
            ? new
            {
                connected = profile.SecretNames.Contains("installation_token", StringComparer.Ordinal),
                relay_name = ProviderFormReader.ReadConfigString(profile, "relay_name"),
                installation_id = ProviderFormReader.ReadConfigString(profile, "installation_id")
            }
            : null;
        return new
        {
            id = profile.Id,
            name = profile.Name,
            kind = profile.Kind,
            enabled = profile.Enabled,
            secret_names = profile.SecretNames,
            values = ProviderFormReader.ReadValues(profile),
            relay,
            created_at = profile.CreatedAt,
            updated_at = profile.UpdatedAt
        };
    }

    private static InteractionDto ToPageDto(Interaction interaction)
    {
        var dto = DtoMapper.ToDto(interaction);
        // The nonce authorizes Relay answers; the page answers through the broker instead.
        dto.Nonce = "";
        return dto;
    }

    private static object SkillJson(AgentSkillTarget target)
    {
        string? root = null;
        var state = "unavailable";
        try
        {
            root = AgentSkillCatalog.DefaultSkillsRoot(target);
            state = SkillInstaller.Inspect(root, WebUiSkill.Files(target)) switch
            {
                SkillInstallState.UpToDate => "up_to_date",
                SkillInstallState.Outdated => "outdated",
                _ => "not_installed"
            };
        }
        catch (InvalidOperationException)
        {
        }

        return new
        {
            id = target.Id,
            display_name = target.DisplayName,
            note = target.Note,
            destination = root is null ? null : SkillInstaller.SkillDirectory(root),
            state
        };
    }

    private static object SettingsJson(AgentNotifyConfig config) => new
    {
        port = config.Port,
        history_retention_days = config.HistoryRetentionDays,
        pause_notifications = config.PauseNotifications,
        do_not_disturb = config.DoNotDisturb,
        launch_at_startup = config.LaunchAtStartup,
        toast_location = config.ToastLocation,
        max_visible_toasts = config.MaxVisibleToasts,
        toast_durations = NotificationTypes.BuiltIns.ToDictionary(type => type, config.ToastDurationSeconds),
        sounds_enabled = config.SoundsEnabled,
        sound_volume = (int)Math.Round(config.SoundVolume * 100),
        play_critical_sounds_during_do_not_disturb = config.PlayCriticalSoundsDuringDoNotDisturb,
        default_sound_file = config.DefaultSoundFile,
        type_sound_files = config.TypeSoundFiles
    };

    private static void ApplySettings(AgentNotifyConfig config, SettingsBody body)
    {
        // Validate everything before touching the shared config, so a refusal changes nothing.
        static int Range(int? value, int current, int min, int max, string label) =>
            value is null ? current
            : value < min || value > max ? throw new ArgumentException($"{label} must be between {min} and {max}.")
            : value.Value;

        var port = Range(body.Port, config.Port, 1, 65535, "Port");
        var retention = Range(body.HistoryRetentionDays, config.HistoryRetentionDays, 0, 3650, "Retention");
        var visible = Range(body.MaxVisibleToasts, config.MaxVisibleToasts, 1, 20, "Maximum visible toasts");
        var volume = Range(body.SoundVolume, (int)Math.Round(config.SoundVolume * 100), 0, 100, "Sound volume");
        var location = body.ToastLocation ?? config.ToastLocation;
        if (location is not ("BottomRight" or "TopRight"))
            throw new ArgumentException("Screen corner must be BottomRight or TopRight.");

        var durations = new Dictionary<string, int>(config.ToastDurations, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, seconds) in body.ToastDurations ?? [])
        {
            var id = NotificationTypes.Normalize(type);
            if (id is null || !NotificationTypes.BuiltIns.Contains(id))
                throw new ArgumentException($"'{type}' is not a built-in notification type.");
            durations[id] = Range(seconds, 0, 0, 86400, $"{id.Replace('_', ' ')} duration");
        }

        string? defaultSound = config.DefaultSoundFile;
        if (body.DefaultSoundFile is not null)
            defaultSound = NormalizeSound(body.DefaultSoundFile);
        Dictionary<string, string>? typeSounds = null;
        if (body.TypeSoundFiles is not null)
        {
            typeSounds = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, file) in body.TypeSoundFiles)
            {
                var id = NotificationTypes.Normalize(type) ?? throw new ArgumentException($"'{type}' is not a valid notification type.");
                if (NormalizeSound(file) is { } sound) typeSounds[id] = sound;
            }
        }

        config.Port = port;
        config.HistoryRetentionDays = retention;
        config.MaxVisibleToasts = visible;
        config.SoundVolume = volume / 100d;
        config.ToastLocation = location;
        config.ToastDurations = durations;
        if (body.PauseNotifications is { } pause) config.PauseNotifications = pause;
        if (body.DoNotDisturb is { } dnd) config.DoNotDisturb = dnd;
        if (body.SoundsEnabled is { } soundsEnabled) config.SoundsEnabled = soundsEnabled;
        if (body.PlayCriticalSoundsDuringDoNotDisturb is { } critical) config.PlayCriticalSoundsDuringDoNotDisturb = critical;
        config.DefaultSoundFile = defaultSound;
        if (typeSounds is not null) config.TypeSoundFiles = typeSounds;
    }

    private static string? NormalizeSound(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var file = SafeFileName.Last(value.Trim());
        var extension = Path.GetExtension(file);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sounds must be WAV or MP3 files.");
        return file;
    }

    private static object TypesJson(AgentNotifyConfig config) => new
    {
        built_in = NotificationTypes.BuiltIns,
        custom = config.CustomNotificationTypes.Select(type => new
        {
            id = type.Id,
            display_name = type.DisplayName,
            accent_color = type.AccentColor,
            default_priority = type.DefaultPriority,
            duration_seconds = type.DurationSeconds,
            enabled = type.Enabled
        })
    };

    private static List<NotificationTypeDefinition> ValidateTypes(IReadOnlyList<TypeBody> types)
    {
        if (types.Count > 100) throw new ArgumentException("At most 100 custom types are supported.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<NotificationTypeDefinition>();
        foreach (var type in types)
        {
            var id = NotificationTypes.Normalize(type.Id);
            if (id is null || NotificationTypes.BuiltIns.Contains(id))
                throw new ArgumentException($"'{type.Id}' is not a usable custom type ID. Use lowercase letters, digits, and underscores, and avoid built-in names.");
            if (!seen.Add(id)) throw new ArgumentException($"The custom type ID '{id}' is used twice.");
            if (type.DurationSeconds is < 0 or > 86400) throw new ArgumentException("Custom lifetime must be 0–86400 seconds.");
            var color = (type.AccentColor ?? "").Trim();
            if (color.Length != 7 || color[0] != '#' || !color[1..].All(Uri.IsHexDigit))
                throw new ArgumentException("Accent must use #RRGGBB.");
            if (!Enum.TryParse<NotificationPriority>(type.DefaultPriority, ignoreCase: true, out var priority))
                priority = NotificationPriority.Normal;
            var name = (type.DisplayName ?? "").Trim();
            if (name.Length > 60) throw new ArgumentException("Display names must be at most 60 characters.");
            result.Add(new NotificationTypeDefinition
            {
                Id = id,
                DisplayName = name.Length == 0 ? id.Replace('_', ' ') : name,
                AccentColor = color.ToUpperInvariant(),
                DefaultPriority = priority,
                DurationSeconds = type.DurationSeconds,
                Enabled = type.Enabled
            });
        }

        return result;
    }

    // ---- request bodies --------------------------------------------------------------------

    private sealed class QuotaAccountBody
    {
        public string? Provider { get; set; }
        public string? Label { get; set; }
        public string? Directory { get; set; }
    }

    private sealed class SettingsBody
    {
        public int? Port { get; set; }
        public int? HistoryRetentionDays { get; set; }
        public bool? PauseNotifications { get; set; }
        public bool? DoNotDisturb { get; set; }
        public string? ToastLocation { get; set; }
        public int? MaxVisibleToasts { get; set; }
        public Dictionary<string, int>? ToastDurations { get; set; }
        public bool? SoundsEnabled { get; set; }
        public int? SoundVolume { get; set; }
        public bool? PlayCriticalSoundsDuringDoNotDisturb { get; set; }
        public string? DefaultSoundFile { get; set; }
        public Dictionary<string, string>? TypeSoundFiles { get; set; }
    }

    private sealed class TypesBody { public List<TypeBody>? Custom { get; set; } }

    private sealed class TypeBody
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? AccentColor { get; set; }
        public string? DefaultPriority { get; set; }
        public int DurationSeconds { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class AnswerBody
    {
        public string? RequestDigest { get; set; }
        public string? ChoiceId { get; set; }
        public string? Text { get; set; }
    }

    private sealed class ProviderBody
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public bool Enabled { get; set; }
        public Dictionary<string, string?>? Values { get; set; }
        public Dictionary<string, string?>? Secrets { get; set; }
        public List<string>? ClearSecrets { get; set; }
        public string? PairingId { get; set; }
    }

    private sealed class PairingBody
    {
        public string? RelayUrl { get; set; }
        public string? SenderName { get; set; }
        public bool AllowPrivateNetwork { get; set; }
        public string? ProviderId { get; set; }
    }

    private sealed class RouteBody
    {
        public string? Name { get; set; }
        public string? ProviderId { get; set; }
        public bool Enabled { get; set; }
        public string? MinimumPriority { get; set; }
        public string? TypeId { get; set; }
        public string? Project { get; set; }
        public string? Agent { get; set; }
        public bool IncludeMessage { get; set; } = true;
    }

    private sealed class SkillBody { public bool Force { get; set; } }
}

/// <summary>The agent skill as this build carries it, for installs started from the web UI.</summary>
internal static class WebUiSkill
{
    private static readonly Lazy<string> Skill = new(() => Read("AgentNotify.Api.Resources.SKILL.md"));
    private static readonly Lazy<string> OpenAiMetadata = new(() => Read("AgentNotify.Api.Resources.openai.yaml"));

    public static IReadOnlyList<SkillInstaller.SkillFile> Files(AgentSkillTarget target)
    {
        var files = new List<SkillInstaller.SkillFile> { new("SKILL.md", Skill.Value) };
        if (target.Id == AgentSkillCatalog.Codex.Id)
            files.Add(new(Path.Combine("agents", "openai.yaml"), OpenAiMetadata.Value));
        return files;
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
