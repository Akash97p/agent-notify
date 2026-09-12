using System.Net.Http.Headers;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Logging;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Continuously pulls mobile interaction answers for enabled Relay profiles.
/// Network work runs on a worker task; cursors move only after a complete batch.
/// </summary>
public sealed class InteractionResponsePoller : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultMaximumRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ProviderProfileService _profiles;
    private readonly RelayCursorStore _cursors;
    private readonly InteractionResponseSync _sync;
    private readonly IAppLogger? _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maximumRetryDelay;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<double> _jitter;
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient? _ownedRelayClient;
    private readonly HttpClient? _ownedBrokerClient;
    private Task? _runTask;

    public InteractionResponsePoller(
        ProviderProfileService profiles,
        RelayCursorStore cursors,
        string brokerBaseUrl,
        string brokerToken,
        IAppLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(brokerToken))
            throw new ArgumentException("Broker token is required.", nameof(brokerToken));

        _profiles = profiles;
        _cursors = cursors;
        _logger = logger;
        _pollInterval = DefaultPollInterval;
        _maximumRetryDelay = DefaultMaximumRetryDelay;
        _requestTimeout = DefaultRequestTimeout;
        _jitter = Random.Shared.NextDouble;

        _ownedRelayClient = RelayHttpTransport.CreateClient();
        _ownedBrokerClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _ownedBrokerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", brokerToken);
        _sync = new InteractionResponseSync(_ownedRelayClient, _ownedBrokerClient, brokerBaseUrl);
    }

    internal InteractionResponsePoller(
        ProviderProfileService profiles,
        RelayCursorStore cursors,
        InteractionResponseSync sync,
        IAppLogger? logger = null,
        TimeSpan? pollInterval = null,
        TimeSpan? maximumRetryDelay = null,
        TimeSpan? requestTimeout = null,
        Func<double>? jitter = null)
    {
        _profiles = profiles;
        _cursors = cursors;
        _sync = sync;
        _logger = logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _maximumRetryDelay = maximumRetryDelay ?? DefaultMaximumRetryDelay;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _jitter = jitter ?? Random.Shared.NextDouble;

        if (_pollInterval <= TimeSpan.Zero || _maximumRetryDelay < _pollInterval ||
            _requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public void Start()
    {
        if (_runTask is not null)
            throw new InvalidOperationException("Interaction response poller is already running.");
        _runTask = Task.Run(() => RunAsync(_stop.Token));
    }

    public async Task StopAsync()
    {
        if (_runTask is null)
            return;
        _stop.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    internal async Task<bool> PollProvidersOnceAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ProviderProfile> profiles;
        try
        {
            profiles = await _profiles.ListAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        var succeeded = true;
        foreach (var profile in profiles.Where(profile => profile.Enabled && profile.Kind == "relay"))
        {
            RelayPollTarget? target;
            try
            {
                target = await RelayPollTarget.FromProfileAsync(_profiles, profile, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Profile parsing/decryption errors may contain credentials or URLs.
                succeeded = false;
                continue;
            }

            if (target is null)
                continue;

            try
            {
                var since = await _cursors.GetAsync(profile.Id, ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_requestTimeout);
                var outcome = await _sync.PollOnceAsync(target, since, timeout.Token).ConfigureAwait(false);
                if (!outcome.Succeeded)
                {
                    succeeded = false;
                    continue;
                }

                if (!string.Equals(outcome.NextCursor, since, StringComparison.Ordinal))
                    await _cursors.SetAsync(profile.Id, outcome.NextCursor, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Keep the old cursor and let the bounded worker retry. Exception text
                // can contain a Relay URL, bearer token, or interaction content.
                succeeded = false;
            }
        }

        return succeeded;
    }

    internal static TimeSpan RetryDelay(
        int consecutiveFailures,
        double jitter,
        TimeSpan? pollInterval = null,
        TimeSpan? maximumRetryDelay = null)
    {
        var minimum = pollInterval ?? DefaultPollInterval;
        var maximum = maximumRetryDelay ?? DefaultMaximumRetryDelay;
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 20);
        var rawMilliseconds = Math.Min(
            maximum.TotalMilliseconds,
            minimum.TotalMilliseconds * Math.Pow(2, exponent));
        var factor = 0.8 + Math.Clamp(jitter, 0, 1) * 0.4;
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, rawMilliseconds * factor));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _ownedBrokerClient?.Dispose();
        _ownedRelayClient?.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        var failureReported = false;
        while (!ct.IsCancellationRequested)
        {
            bool succeeded;
            try
            {
                succeeded = await PollProvidersOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                succeeded = false;
            }

            if (succeeded)
            {
                consecutiveFailures = 0;
                if (failureReported)
                {
                    _logger?.Info("Relay interaction response polling recovered.");
                    failureReported = false;
                }
            }
            else
            {
                consecutiveFailures++;
                if (!failureReported)
                {
                    _logger?.Warn("Relay interaction response polling failed; cursor retained for retry.");
                    failureReported = true;
                }
            }

            var delay = RetryDelay(consecutiveFailures, _jitter(), _pollInterval, _maximumRetryDelay);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
