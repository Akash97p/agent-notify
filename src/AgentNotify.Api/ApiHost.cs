using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentNotify.Api.Auth;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
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
        ApiCallbacks? callbacks = null)
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
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

        app.Use(async (context, next) =>
        {
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

            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments($"{RootPath}/notifications"))
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

        return app;
    }

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
