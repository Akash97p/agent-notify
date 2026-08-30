using System.Net;
using System.Net.Sockets;

namespace AgentNotify.Core.Delivery.Channels;

/// <summary>
/// Shared hardened HTTP transport for Relay delivery and pairing requests.
/// Every request must opt in through <see cref="MarkValidated"/> after URL validation.
/// </summary>
internal static class RelayHttpTransport
{
    private static readonly HttpRequestOptionsKey<bool> ValidatedRequest =
        new("AgentNotify.Relay.ValidatedEndpoint");
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork =
        new("AgentNotify.Relay.AllowPrivateNetwork");

    internal static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static void MarkValidated(HttpRequestMessage request, bool allowPrivate)
    {
        request.Options.Set(ValidatedRequest, true);
        request.Options.Set(AllowPrivateNetwork, allowPrivate);
    }

    internal static bool IsLocalhost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        var trimmed = host.TrimStart('[').TrimEnd(']');
        return trimmed.Equals("127.0.0.1", StringComparison.Ordinal) ||
               trimmed.Equals("::1", StringComparison.Ordinal) ||
               trimmed.Equals("0:0:0:0:0:0:0:1", StringComparison.Ordinal);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(ValidatedRequest, out var validated) || !validated)
            throw new HttpRequestException("Relay transport refused an unvalidated destination.");

        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(
            AllowPrivateNetwork,
            out var configured) && configured;
        var host = context.DnsEndPoint.Host;
        var effectiveAllowPrivate = allowPrivate ||
                                    (IsLocalhost(host) && context.DnsEndPoint.Port != 443);

        var addresses = await Dns.GetHostAddressesAsync(
            host,
            AddressFamily.Unspecified,
            cancellationToken).ConfigureAwait(false);
        var allowed = addresses
            .Where(address => WebhookChannelAdapter.IsAddressAllowed(address, effectiveAllowPrivate))
            .ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Relay server resolved to a disallowed address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
