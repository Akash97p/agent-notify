using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// Runs Relay device-grant pairings on behalf of the web UI.
/// </summary>
/// <remarks>
/// The browser starts a pairing and polls its state; the broker does the waiting. The installation
/// token the Relay issues on approval stays here and is handed to <see cref="ProviderFormService"/>
/// only when the profile is saved with this pairing's id — it never reaches the page.
/// </remarks>
public sealed class RelayPairingSessions : IDisposable
{
    private static readonly TimeSpan RetainFinished = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Pairing> _pairings = new(StringComparer.Ordinal);
    private readonly Func<bool, RelayPairingClient> _clientFactory;

    public RelayPairingSessions(Func<bool, RelayPairingClient>? clientFactory = null) =>
        _clientFactory = clientFactory ?? (allowPrivate => new RelayPairingClient(allowPrivateNetwork: allowPrivate));

    public sealed record Snapshot(
        string Id,
        string Status,
        string Message,
        string? UserCode,
        string? VerificationUri,
        string? VerificationUriComplete,
        int? RemainingSeconds,
        string? ConnectedAs,
        bool Reconnected,
        int? ActiveDeviceCount);

    /// <summary>
    /// Discovers the hosted Relay and requests a pairing code. Only one pairing runs at a time.
    /// The endpoint is not a client input: Relay is hosted-only.
    /// </summary>
    /// <exception cref="ArgumentException">The sender name is invalid.</exception>
    /// <exception cref="RelayPairingException">The Relay could not be reached or refused.</exception>
    public async Task<Snapshot> StartAsync(
        string? senderName,
        string? existingInstallId,
        CancellationToken ct)
    {
        Sweep();
        foreach (var running in _pairings.Values.Where(p => !p.IsFinished))
            running.Cancel();

        const bool allowPrivateNetwork = false;
        var baseUri = RelayChannelAdapter.ValidateRelayUrl(RelayChannelAdapter.HostedBaseUrl, allowPrivateNetwork);
        var client = _clientFactory(allowPrivateNetwork);
        try
        {
            await client.DiscoverAsync(baseUri, ct);
            var installId = string.IsNullOrWhiteSpace(existingInstallId) ? Guid.NewGuid().ToString("N") : existingInstallId;
            var request = await client.BeginAsync(
                baseUri,
                string.IsNullOrWhiteSpace(senderName) ? Environment.MachineName : senderName.Trim(),
                Platform(),
                ClientVersion(),
                ct,
                installId);
            RelayPairingClient.EnsureSameOrigin(baseUri, request.VerificationUriComplete);

            var pairing = new Pairing(NewId(), baseUri, request, installId, client);
            _pairings[pairing.Id] = pairing;
            pairing.Run();
            return pairing.Snapshot();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public Snapshot? Get(string id) => _pairings.TryGetValue(id, out var pairing) ? pairing.Snapshot() : null;

    public bool Cancel(string id)
    {
        if (!_pairings.TryGetValue(id, out var pairing)) return false;
        pairing.Cancel();
        return true;
    }

    /// <summary>The approved credential for <paramref name="id"/>, consumed so it can be saved only once.</summary>
    public RelayPairingOutcome? Take(string id)
    {
        if (!_pairings.TryGetValue(id, out var pairing)) return null;
        var outcome = pairing.TakeOutcome();
        if (outcome is not null) _pairings.TryRemove(id, out _);
        return outcome;
    }

    public void Dispose()
    {
        foreach (var pairing in _pairings.Values) pairing.Cancel();
        _pairings.Clear();
    }

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - RetainFinished;
        foreach (var (id, pairing) in _pairings)
            if (pairing.IsFinished && pairing.FinishedAt < cutoff)
                _pairings.TryRemove(id, out _);
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string Platform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    private static string ClientVersion()
    {
        var assembly = typeof(RelayPairingSessions).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        return version.Length <= 32 ? version : version[..32];
    }

    internal static string Describe(RelayPairingException exception) => exception.Code switch
    {
        "not_relay" => "That URL is not an AgentNotify Relay.",
        "discovery_failed" => "Could not reach the relay. Check the URL and try again.",
        "rate_limited" when exception.RetryAfterMilliseconds is int ms =>
            $"The relay is rate-limiting pairing requests. Try again in {Math.Max(1, (int)Math.Ceiling(ms / 1000d))} seconds.",
        "rate_limited" => "The relay is rate-limiting pairing requests. Try again shortly.",
        "unexpected_verification_uri" => "The relay returned an unexpected verification URL.",
        "network_error" => "Could not reach the relay after several attempts. Check your connection and try again.",
        "consumed" => "The pairing credential was already collected. Connect again.",
        _ => exception.Message
    };

    private sealed class Pairing
    {
        private readonly object _gate = new();
        private readonly Uri _baseUri;
        private readonly RelayPairingRequest _request;
        private readonly string _installId;
        private readonly RelayPairingClient _client;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly DateTimeOffset _expiresAt;

        private string _status = "waiting";
        private string _message = "Waiting for approval.";
        private RelayPairingOutcome? _outcome;
        private string? _connectedAs;
        private bool _reconnected;
        private int? _activeDevices;

        public Pairing(string id, Uri baseUri, RelayPairingRequest request, string installId, RelayPairingClient client)
        {
            Id = id;
            _baseUri = baseUri;
            _request = request;
            _installId = installId;
            _client = client;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(request.ExpiresIn, 30, 1800));
        }

        public string Id { get; }
        public DateTimeOffset FinishedAt { get; private set; }
        public bool IsFinished { get { lock (_gate) return _status is not "waiting" and not "verifying"; } }

        public void Run() => _ = Task.Run(RunAsync);

        public void Cancel()
        {
            lock (_gate)
            {
                if (_status is "waiting" or "verifying")
                    Finish("cancelled", "Connection cancelled.");
            }
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        public RelayPairingOutcome? TakeOutcome()
        {
            lock (_gate)
            {
                var outcome = _outcome;
                _outcome = null;
                return outcome;
            }
        }

        public Snapshot Snapshot()
        {
            lock (_gate)
            {
                var waiting = _status == "waiting";
                return new Snapshot(
                    Id,
                    _status,
                    _message,
                    waiting ? _request.UserCode : null,
                    waiting ? _request.VerificationUri.AbsoluteUri : null,
                    waiting ? _request.VerificationUriComplete.AbsoluteUri : null,
                    waiting ? Math.Max(0, (int)(_expiresAt - DateTimeOffset.UtcNow).TotalSeconds) : null,
                    _connectedAs,
                    _reconnected,
                    _activeDevices);
            }
        }

        private async Task RunAsync()
        {
            var ct = _cancellation.Token;
            try
            {
                var poll = await _client.WaitForApprovalAsync(_baseUri, _request, progress =>
                {
                    lock (_gate)
                    {
                        if (_status == "waiting")
                            _message = progress.ConsecutiveNetworkFailures > 0
                                ? $"Connection interrupted ({progress.ConsecutiveNetworkFailures}/5), retrying."
                                : "Waiting for approval.";
                    }
                    return Task.CompletedTask;
                }, ct);

                if (poll.Status == "denied") { Set("denied", "Rejected in the browser."); return; }
                if (poll.Status == "expired") { Set("expired", "The request expired. Connect again."); return; }
                if (poll.Status != "approved" || poll.InstallationToken is null || poll.InstallationId is null)
                    throw new RelayPairingException("poll_failed", "The relay returned an invalid pairing result.");

                lock (_gate) { _status = "verifying"; _message = "Verifying the new connection."; }
                var installation = await _client.VerifyAsync(_baseUri, poll.InstallationToken, ct);
                int? devices = null;
                try
                {
                    devices = (await _client.GetDevicesAsync(_baseUri, poll.InstallationToken, ct)).ActiveDeviceCount;
                }
                catch (RelayPairingException)
                {
                }

                lock (_gate)
                {
                    _outcome = new RelayPairingOutcome(poll.InstallationToken, poll.InstallationId, poll.RelayName, _installId);
                    _connectedAs = installation.DisplayName ?? poll.RelayName ?? installation.InstallationId;
                    _reconnected = poll.Reconnected;
                    _activeDevices = devices;
                    Finish("approved", devices == 0
                        ? "Connected, but no phone is paired yet. Save the provider, pair a phone from the relay console, then send a test."
                        : "Connected. Save the provider to finish.");
                }
            }
            catch (OperationCanceledException)
            {
                Set("cancelled", "Connection cancelled.");
            }
            catch (RelayPairingException exception)
            {
                Set("failed", Describe(exception));
            }
            catch (Exception exception) when (exception is ArgumentException or HttpRequestException)
            {
                Set("failed", exception.Message);
            }
            finally
            {
                _client.Dispose();
            }
        }

        private void Set(string status, string message)
        {
            lock (_gate)
            {
                if (_status is "waiting" or "verifying") Finish(status, message);
            }
        }

        private void Finish(string status, string message)
        {
            _status = status;
            _message = message;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }
}
