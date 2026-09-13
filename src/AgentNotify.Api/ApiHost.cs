using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentNotify.Api.Auth;
using AgentNotify.Protocol;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;

namespace AgentNotify.Api;

public sealed class ApiCallbacks
{
    /// <summary>
    /// Optional local-only persistence hook invoked after notification storage and before the
    /// response. Failures are isolated; implementations must never perform network I/O.
    /// </summary>
    public Func<Notification, CancellationToken, Task>? PersistOutbound { get; set; }
    public Action<Notification>? Created { get; set; }
    public Action<Notification>? Updated { get; set; }
    /// <summary>
    /// Optional hook invoked after an interaction is durably opened. Used to publish
    /// the question to Relay-enabled routes. Failures are isolated like
    /// <see cref="PersistOutbound"/>.
    /// </summary>
    public Func<Interaction, CancellationToken, Task>? InteractionCreated { get; set; }
}

/// <summary>
/// Builds the loopback-only ASP.NET Core Minimal API host that is embedded in the
/// AgentNotify WPF process. All /v1 routes require a local bearer token.
/// </summary>
public static class ApiHost
{
    public const string RootPath = "/v1";

    public static WebApplication Build(
        AgentNotifyConfig config,
        INotificationRepository repository,
        NotificationService service,
        IAppLogger? logger = null,
        string? url = null,
        ApiCallbacks? callbacks = null,
        InteractionService? interactions = null,
        InteractionRelayPublisher? relayPublisher = null,
        WebUi.WebUiOptions? webUi = null)
    {
        // Do not inherit the caller's command line or content root. In WSL-driven
        // Windows test/build processes the working directory is a UNC path, and
        // host configuration/file watchers can block indefinitely while probing it.
        // This API has no content files, so the local temporary directory is a safe,
        // fast host root both in tests and in the installed desktop process.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(ApiHost).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath()
        });

        builder.Logging.ClearProviders();
        if (logger is not null)
            builder.Logging.AddProvider(new LogProvider(logger));

        var baseUrl = url ?? $"http://127.0.0.1:{config.Port}";
        builder.WebHost.UseUrls(baseUrl);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = config.MaxRequestBodyBytes;
        });

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.Converters.Clear();
            options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        });

        var app = builder.Build();
        var token = config.AuthToken;
        var limiter = new RateLimiter(config.RateLimitPerSecond, TimeSpan.FromSeconds(1));
        var startedAt = DateTimeOffset.UtcNow;
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.1";

        var listenPort = new Uri(baseUrl).Port;

        app.Use(async (context, next) =>
        {
            if (webUi is not null && context.Request.Path.StartsWithSegments(WebUi.WebUiEndpoints.BasePath))
            {
                if (await WebUi.WebUiEndpoints.Guard(context))
                    await next();
                return;
            }

            if (context.Request.Path.StartsWithSegments(RootPath))
            {
                var header = context.Request.Headers.Authorization.ToString();
                if (!TokenAuth.IsAuthorized(header, token))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }, cancellationToken: context.RequestAborted);
                    return;
                }
            }

            if (HttpMethods.IsPost(context.Request.Method) &&
                (context.Request.Path.StartsWithSegments($"{RootPath}/notifications") ||
                  context.Request.Path.StartsWithSegments($"{RootPath}/interactions") ||
                  context.Request.Path.Equals($"{RootPath}/events")))
            {
                var key = token;
                if (!limiter.TryAcquire(key))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.RetryAfter = "1";
                    await context.Response.WriteAsJsonAsync(new { error = "rate limit exceeded" }, cancellationToken: context.RequestAborted);
                    return;
                }
            }

            await next();
        });

        // Minimal, unauthenticated liveness probe (no data leaked).
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        app.MapGet($"{RootPath}/health", async (CancellationToken ct) =>
            Results.Json(new HealthResponse
            {
                Status = "ok",
                Version = version,
                Pid = Environment.ProcessId,
                UptimeSeconds = (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                ActiveCount = await repository.CountActiveAsync(ct),
                ApiVersion = config.ApiVersion,
                ServerTimeUtc = DateTimeOffset.UtcNow
            }));

        app.MapPost($"{RootPath}/notifications", async (HttpContext http) =>
        {
            CreateNotificationRequest request;
            try
            {
                request = (await http.Request.ReadFromJsonAsync<CreateNotificationRequest>(Json.Options, http.RequestAborted))!;
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await service.CreateAsync(request, http.RequestAborted);
            if (result.Error is not null)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);

            var notification = result.Value!;
            if (result.WasCreated)
                await InvokePersistenceCallbackAsync(
                    callbacks?.PersistOutbound,
                    notification,
                    logger);
            InvokeCallback(
                result.WasCreated ? callbacks?.Created : callbacks?.Updated,
                notification,
                logger);

            return Results.Json(DtoMapper.ToDto(notification), statusCode: StatusCodes.Status201Created);
        });

        app.MapPost($"{RootPath}/events", async (HttpContext http) =>
        {
            ArcEvent? arcEvent;
            try
            {
                arcEvent = await http.Request.ReadFromJsonAsync<ArcEvent>(Json.Options, http.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!ArcEventMapper.TryMap(arcEvent, out var mapping, out var mappingError))
                return Results.Json(new { error = mappingError }, statusCode: StatusCodes.Status400BadRequest);

            if (mapping!.Operation == ArcOperation.Resolve)
            {
                var resolution = await service.ResolveByKeyAsync(mapping.LocalKey, http.RequestAborted);
                if (resolution.NotFound)
                    return Results.Json(new { error = "ARC request not found" }, statusCode: StatusCodes.Status404NotFound);
                if (resolution.Error is not null)
                    return Results.Json(new { error = resolution.Error }, statusCode: StatusCodes.Status400BadRequest);

                InvokeCallback(callbacks?.Updated, resolution.Value!, logger);
                return Results.Json(DtoMapper.ToDto(resolution.Value!), statusCode: StatusCodes.Status200OK);
            }

            // An unkeyed request.created event is immutable. Its derived event-identity key makes
            // delivery retries return the original local projection across resolved history.
            if (mapping.UsesEventIdentityKey)
            {
                var existing = await repository.FindByKeyAsync(mapping.LocalKey, http.RequestAborted);
                if (existing is not null)
                    return Results.Json(DtoMapper.ToDto(existing), statusCode: StatusCodes.Status200OK);
            }

            var result = mapping.Operation == ArcOperation.Update
                ? await service.UpdateActiveByKeyAsync(mapping.Request!, http.RequestAborted)
                : await service.CreateAsync(mapping.Request!, http.RequestAborted);
            if (result.NotFound)
                return Results.Json(new { error = "active ARC request not found" }, statusCode: StatusCodes.Status404NotFound);
            if (result.Error is not null)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);

            var notification = result.Value!;
            if (result.WasCreated)
                await InvokePersistenceCallbackAsync(
                    callbacks?.PersistOutbound,
                    notification,
                    logger);
            InvokeCallback(
                result.WasCreated ? callbacks?.Created : callbacks?.Updated,
                notification,
                logger);

            return Results.Json(
                DtoMapper.ToDto(notification),
                statusCode: result.WasCreated ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        app.MapGet($"{RootPath}/notifications", async (HttpContext http, CancellationToken ct) =>
        {
            var query = new NotificationQuery
            {
                Unresolved = ParseBool(http.Request.Query["unresolved"]),
                Type = NotificationTypes.Normalize(http.Request.Query["type"].ToString()),
                Status = TryParseEnum<NotificationStatus>(http.Request.Query["status"].ToString()),
                Project = http.Request.Query["project"].ToString(),
                Agent = http.Request.Query["agent"].ToString(),
                Limit = int.TryParse(http.Request.Query["limit"], out var limit) ? Math.Clamp(limit, 1, 500) : 100
            };

            var items = await repository.QueryAsync(query, ct);
            return Results.Json(items.Select(DtoMapper.ToDto));
        });

        app.MapGet($"{RootPath}/notifications/{{id}}", async (string id, CancellationToken ct) =>
        {
            var item = await repository.GetByIdAsync(id, ct);
            return item is null
                ? Results.NotFound()
                : Results.Json(DtoMapper.ToDto(item));
        });

        app.MapPatch($"{RootPath}/notifications/{{id}}", async (string id, HttpContext http) =>
        {
            UpdateNotificationRequest request;
            try
            {
                request = (await http.Request.ReadFromJsonAsync<UpdateNotificationRequest>(Json.Options, http.RequestAborted))!;
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await service.UpdateStatusAsync(id, request, http.RequestAborted);
            if (result.NotFound)
                return Results.Json(new { error = "notification not found" }, statusCode: StatusCodes.Status404NotFound);
            if (result.Error is not null)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);

            InvokeCallback(callbacks?.Updated, result.Value!, logger);
            return Results.Json(DtoMapper.ToDto(result.Value!));
        });

        app.MapPost($"{RootPath}/notifications/{{id}}/dismiss", async (string id, CancellationToken ct) =>
        {
            var result = await service.UpdateStatusAsync(id, new UpdateNotificationRequest { Status = NotificationStatus.Dismissed }, ct);
            if (result.NotFound)
                return Results.Json(new { error = "notification not found" }, statusCode: StatusCodes.Status404NotFound);
            if (result.Error is not null)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);

            InvokeCallback(callbacks?.Updated, result.Value!, logger);
            return Results.Json(DtoMapper.ToDto(result.Value!));
        });

        if (interactions is not null)
            MapInteractions(app, interactions, callbacks, relayPublisher, logger);

        if (webUi is not null)
            WebUi.WebUiEndpoints.Map(app, webUi, config, repository, service, interactions, callbacks, logger, listenPort, version, startedAt);

        return app;
    }

    /// <summary>
    /// Waiting questions/permissions. Every route needs the loopback bearer token
    /// like the rest of <c>/v1</c>; responses additionally bind to the request digest.
    /// </summary>
    private static void MapInteractions(
        WebApplication app,
        InteractionService interactions,
        ApiCallbacks? callbacks,
        InteractionRelayPublisher? relayPublisher,
        IAppLogger? logger)
    {
        app.MapPost($"{RootPath}/interactions/request", async (HttpContext http) =>
        {
            CreateInteractionRequest request;
            try
            {
                request = (await http.Request.ReadFromJsonAsync<CreateInteractionRequest>(Json.Options, http.RequestAborted))!;
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await interactions.RequestAsync(request, http.RequestAborted);
            if (result.Error is not null)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status400BadRequest);

            if (result.WasCreated)
                await InvokeCallbackAsync(callbacks?.InteractionCreated, result.Value!, logger);

            return Results.Json(
                DtoMapper.ToDto(result.Value!),
                statusCode: result.WasCreated ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        app.MapGet($"{RootPath}/interactions", async (HttpContext http, CancellationToken ct) =>
        {
            var query = new InteractionQuery
            {
                Status = TryParseEnum<InteractionStatus>(http.Request.Query["status"].ToString()),
                PendingOnly = ParseBool(http.Request.Query["pending"]),
                Agent = NullIfBlank(http.Request.Query["agent"].ToString()),
                Project = NullIfBlank(http.Request.Query["project"].ToString()),
                SessionId = NullIfBlank(http.Request.Query["session"].ToString()),
                Limit = int.TryParse(http.Request.Query["limit"], out var limit) ? Math.Clamp(limit, 1, 500) : 100
            };

            var items = await interactions.ListAsync(query, ct);
            return Results.Json(items.Select(DtoMapper.ToDto));
        });

        app.MapGet($"{RootPath}/interactions/{{id}}", async (string id, CancellationToken ct) =>
        {
            var result = await interactions.GetAsync(id, ct);
            return result.NotFound
                ? Results.Json(new { error = "interaction not found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(DtoMapper.ToDto(result.Value!));
        });

        app.MapGet($"{RootPath}/interactions/{{id}}/wait", async (string id, HttpContext http, CancellationToken ct) =>
        {
            var timeoutSeconds = int.TryParse(http.Request.Query["timeout"], out var seconds)
                ? Math.Clamp(seconds, 1, 300)
                : 60;
            var result = await interactions.GetAsync(id, http.RequestAborted);
            if (result.NotFound)
                return Results.Json(new { error = "interaction not found" }, statusCode: StatusCodes.Status404NotFound);

            var settled = result.Value!.Status != InteractionStatus.Pending
                ? result.Value
                : await interactions.WaitAsync(id, TimeSpan.FromSeconds(timeoutSeconds), http.RequestAborted);
            return settled is null
                ? Results.Json(new { error = "interaction not found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(DtoMapper.ToDto(settled));
        });

        app.MapPost($"{RootPath}/interactions/{{id}}/respond", async (string id, HttpContext http) =>
        {
            RespondInteractionRequest request;
            try
            {
                request = (await http.Request.ReadFromJsonAsync<RespondInteractionRequest>(Json.Options, http.RequestAborted))!;
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await interactions.RespondAsync(id, request, http.RequestAborted);
            if (result.NotFound)
                return Results.Json(new { error = "interaction not found" }, statusCode: StatusCodes.Status404NotFound);
            if (result.Error is not null)
            {
                var conflict = result.Error is "interaction already answered";
                return Results.Json(new { error = result.Error },
                    statusCode: conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
            }

            return Results.Json(DtoMapper.ToDto(result.Value!));
        });

        app.MapPost($"{RootPath}/interactions/{{id}}/cancel", async (string id, CancellationToken ct) =>
        {
            var result = await interactions.CancelAsync(id, ct);
            return result.NotFound
                ? Results.Json(new { error = "interaction not found" }, statusCode: StatusCodes.Status404NotFound)
                : Results.Json(DtoMapper.ToDto(result.Value!));
        });

        app.MapPost($"{RootPath}/interactions/{{id}}/publish", async (string id, CancellationToken ct) =>
        {
            if (relayPublisher is null)
                return Results.Json(new { error = "interaction relay publishing is not enabled on this broker" },
                    statusCode: StatusCodes.Status400BadRequest);
            var result = await interactions.GetAsync(id, ct);
            if (result.NotFound)
                return Results.Json(new { error = "interaction not found" }, statusCode: StatusCodes.Status404NotFound);
            var published = await relayPublisher.PublishAsync(DtoMapper.ToDto(result.Value!), ct);
            return Results.Json(new { id, published });
        });
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool? ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : null;

    private static T? TryParseEnum<T>(string? value) where T : struct, Enum
    {
        var normalized = value?.Replace('-', '_').Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse<T>(normalized, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static void InvokeCallback(Action<Notification>? callback, Notification notification, IAppLogger? logger)
    {
        if (callback is null)
            return;
        try { callback(notification); }
        catch (Exception ex) { logger?.Error("Notification UI callback failed", ex); }
    }

    private static async Task InvokePersistenceCallbackAsync(
        Func<Notification, CancellationToken, Task>? callback,
        Notification notification,
        IAppLogger? logger)
    {
        if (callback is null)
            return;
        try
        {
            // The notification is already committed. Do not let client disconnect cancellation
            // create a route/outbox gap, and never let queueing failure roll back local success.
            await callback(notification, CancellationToken.None);
        }
        catch (Exception)
        {
            logger?.Warn("Could not persist outbound delivery work; local notification remains available.");
        }
    }

    private static async Task InvokeCallbackAsync(
        Func<Interaction, CancellationToken, Task>? callback,
        Interaction value,
        IAppLogger? logger)
    {
        if (callback is null)
            return;
        try
        {
            // The interaction is already committed. Do not let client disconnect
            // cancellation skip Relay publication, and never let publish failure
            // roll back the open interaction.
            await callback(value, CancellationToken.None);
        }
        catch (Exception)
        {
            logger?.Warn("Could not publish the interaction to Relay; the local interaction remains available.");
        }
    }

    private sealed class LogProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly IAppLogger _logger;
        public LogProvider(IAppLogger logger) => _logger = logger;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(_logger);

        public void Dispose() { }

        private sealed class Logger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly IAppLogger _inner;
            public Logger(IAppLogger inner) => _inner = inner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                {
                    var message = formatter(state, exception);
                    if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                        _inner.Error(message, exception);
                    else
                        _inner.Warn(message);
                }
            }
        }
    }
}
