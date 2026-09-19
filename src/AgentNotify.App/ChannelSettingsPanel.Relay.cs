using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentNotify.Protocol;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core;

namespace AgentNotify.App;

public partial class ChannelSettingsPanel : System.Windows.Controls.UserControl
{
    private async void RelayConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_relayPairingCts is not null)
            return;

        Uri baseUri;
        const bool allowPrivate = false;
        try
        {
            baseUri = RelayChannelAdapter.ValidateRelayUrl(RelayChannelAdapter.HostedBaseUrl, allowPrivate);
        }
        catch (ArgumentException exception)
        {
            SetRelayStatus(exception.Message, "ErrorBrush");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _relayPairingCts = cancellation;
        _pendingRelayInstallationToken = null;
        _pendingRelayInstallationId = null;
        _pendingRelayName = null;
        RelayConnectButton.IsEnabled = false;
        RelayCancelButton.Visibility = Visibility.Collapsed;
        RelayCodePanel.Visibility = Visibility.Collapsed;
        RelayConnectedText.Visibility = Visibility.Collapsed;
        SetRelayStatus("Checking the Relay server…", "MutedTextBrush");

        try
        {
            using var client = new RelayPairingClient(allowPrivateNetwork: allowPrivate);
            await client.DiscoverAsync(baseUri, cancellation.Token);
            if (!ReferenceEquals(_relayPairingCts, cancellation))
                return;
            SetRelayStatus("Starting a secure connection request…", "MutedTextBrush");

            var senderName = string.IsNullOrWhiteSpace(RelaySenderNameBox.Text)
                ? Environment.MachineName
                : RelaySenderNameBox.Text.Trim();
            // Reuse this computer's identity if it has one; otherwise mint it
            // now and let SaveRelayProviderAsync write it down. Not derived from
            // the machine name or any hardware id on purpose — it is scoped to
            // this provider profile, so two profiles pointing at two relays stay
            // separate, and nothing about the machine leaks into it.
            _pendingRelayInstallId = ReadRelayConfigValue(ProviderList.SelectedItem as ProviderProfile, "install_id")
                ?? _pendingRelayInstallId
                ?? Guid.NewGuid().ToString("N");
            var pairing = await client.BeginAsync(
                baseUri,
                senderName,
                CurrentRelayPlatform(),
                CurrentRelayClientVersion(),
                cancellation.Token,
                _pendingRelayInstallId);

            if (!ReferenceEquals(_relayPairingCts, cancellation))
                return;
            RelayPairingClient.EnsureSameOrigin(baseUri, pairing.VerificationUriComplete);
            RelayUserCodeText.Text = pairing.UserCode;
            RelayCodeHintText.Text = "Approve this code in your browser to connect this computer.";
            RelayCodePanel.Visibility = Visibility.Visible;
            RelayCancelButton.Visibility = Visibility.Visible;
            SetRelayStatus("Waiting for approval…", "MutedTextBrush");

            try
            {
                Process.Start(new ProcessStartInfo(pairing.VerificationUriComplete.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                RelayCodeHintText.Text =
                    RelayPairingPresentation.ManualApprovalText(pairing);
            }

            var poll = await client.WaitForApprovalAsync(
                baseUri,
                pairing,
                progress => Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_relayPairingCts, cancellation))
                        return;
                    var recovering = progress.ConsecutiveNetworkFailures > 0
                        ? $"Connection interrupted ({progress.ConsecutiveNetworkFailures}/5) — retrying"
                        : "Waiting for approval";
                    SetRelayStatus(
                        $"{recovering} — {RelayPairingPresentation.FormatRemaining(progress.Remaining)} left",
                        progress.Remaining < TimeSpan.FromMinutes(1) ? "WarningBrush" : "MutedTextBrush");
                }).Task,
                cancellation.Token);

            if (!ReferenceEquals(_relayPairingCts, cancellation))
                return;
            if (poll.Status == "denied")
            {
                SetRelayStatus("Rejected in the browser.", "ErrorBrush");
                RelayCodePanel.Visibility = Visibility.Collapsed;
                return;
            }
            if (poll.Status == "expired")
            {
                SetRelayStatus("Request expired — try Connect again.", "ErrorBrush");
                RelayCodePanel.Visibility = Visibility.Collapsed;
                return;
            }
            if (poll.Status != "approved" || poll.InstallationToken is null || poll.InstallationId is null)
                throw new RelayPairingException("poll_failed", "The relay returned an invalid pairing result.");

            _pendingRelayInstallationToken = poll.InstallationToken;
            _pendingRelayInstallationId = poll.InstallationId;
            _pendingRelayName = poll.RelayName;
            RelayCodePanel.Visibility = Visibility.Collapsed;
            RelayCancelButton.Visibility = Visibility.Collapsed;
            SetRelayStatus("Verifying the new connection…", "MutedTextBrush");

            try
            {
                var installation = await client.VerifyAsync(
                    baseUri,
                    poll.InstallationToken,
                    cancellation.Token);
                if (!ReferenceEquals(_relayPairingCts, cancellation))
                    return;
                var displayName = installation.DisplayName ?? poll.RelayName ?? installation.InstallationId;
                // "Reconnected" is worth saying: it is the difference between
                // this computer being on the relay's list once and being on it
                // twice, which is what the operator is trying to avoid.
                RelayConnectedText.Text = poll.Reconnected
                    ? $"Reconnected as {displayName}"
                    : $"Connected as {displayName}";
                RelayConnectedText.Visibility = Visibility.Visible;
                RelayConnectButton.Content = "Reconnect";
                ClearRelayTokenBox.IsChecked = false;
                SetRelayStatus("Connected. Press Save provider to finish.", "MutedTextBrush");
                try
                {
                    var devices = await client.GetDevicesAsync(
                        baseUri,
                        poll.InstallationToken,
                        cancellation.Token);
                    if (devices.ActiveDeviceCount == 0)
                        SetRelayStatus(
                            "Connected to the relay, but no phone is paired yet. Pair a phone from the relay console, then send a test. Press Save provider to finish.",
                            "WarningBrush");
                }
                catch (RelayPairingException exception) when (exception.Code == "device_discovery_failed")
                {
                    SetRelayStatus(
                        "Connected. AgentNotify could not check paired phones; press Save provider, then try again.",
                        "WarningBrush");
                }
            }
            catch (RelayPairingException)
            {
                _pendingRelayInstallationToken = null;
                _pendingRelayInstallationId = null;
                _pendingRelayName = null;
                RelayConnectedText.Visibility = Visibility.Collapsed;
                SetRelayStatus(
                    "Paired, but the relay did not accept the credential. Try Connect again.",
                    "ErrorBrush");
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_relayPairingCts, cancellation))
                SetRelayStatus("Connection cancelled.", "MutedTextBrush");
        }
        catch (RelayPairingException exception)
        {
            if (ReferenceEquals(_relayPairingCts, cancellation))
            {
                RelayCodePanel.Visibility = Visibility.Collapsed;
                RelayConnectedText.Visibility = Visibility.Collapsed;
                SetRelayStatus(RelayPairingErrorMessage(exception), "ErrorBrush");
            }
        }
        catch (ArgumentException exception)
        {
            if (ReferenceEquals(_relayPairingCts, cancellation))
                SetRelayStatus(exception.Message, "ErrorBrush");
        }
        finally
        {
            if (ReferenceEquals(_relayPairingCts, cancellation))
            {
                _relayPairingCts = null;
                RelayCancelButton.Visibility = Visibility.Collapsed;
                RelayConnectButton.IsEnabled = true;
                RelayConnectButton.Content = HasRelayCredentialInEditor() ? "Reconnect" : "Connect";
            }
            cancellation.Dispose();
        }
    }

    private void RelayCancel_Click(object sender, RoutedEventArgs e) => _relayPairingCts?.Cancel();

    private void ChannelSettingsPanel_Unloaded(object sender, RoutedEventArgs e) =>
        StopRelayPairing();

    public void StopRelayPairing() => CancelRelayPairing(clearPending: true);

    private void CancelRelayPairing(bool clearPending)
    {
        var cancellation = _relayPairingCts;
        _relayPairingCts = null;
        cancellation?.Cancel();
        if (clearPending)
        {
            _pendingRelayInstallationToken = null;
            _pendingRelayInstallationId = null;
            _pendingRelayName = null;
        }
        RelayCancelButton.Visibility = Visibility.Collapsed;
        RelayConnectButton.IsEnabled = true;
        RelayCodePanel.Visibility = Visibility.Collapsed;
    }

    private void ResetRelayConnectionPresentation(bool hasStoredCredential)
    {
        RelayCodePanel.Visibility = Visibility.Collapsed;
        RelayCancelButton.Visibility = Visibility.Collapsed;
        RelayConnectButton.IsEnabled = true;
        RelayConnectButton.Content = hasStoredCredential ? "Reconnect" : "Connect";
        RelayConnectedText.Text = hasStoredCredential ? "Connected — credential stored" : "";
        RelayConnectedText.Visibility = hasStoredCredential ? Visibility.Visible : Visibility.Collapsed;
        SetRelayStatus(hasStoredCredential ? "Connected" : "Not connected", "MutedTextBrush");
    }

    private bool HasRelayCredentialInEditor() =>
        _pendingRelayInstallationToken is not null ||
        ProviderList.SelectedItem is ProviderProfile profile &&
        profile.Kind == "relay" &&
        profile.SecretNames.Contains("installation_token", StringComparer.Ordinal) &&
        ClearRelayTokenBox.IsChecked != true;

    private void SetRelayStatus(string message, string brushKey)
    {
        RelayStatusText.Text = message;
        if (TryFindResource(brushKey) is System.Windows.Media.Brush brush)
            RelayStatusText.Foreground = brush;
    }

    private static string RelayPairingErrorMessage(RelayPairingException exception)
    {
        return exception.Code switch
        {
            "not_relay" => "That URL is not an AgentNotify Relay.",
            "discovery_failed" => "Could not reach the relay. Check the URL and try again.",
            "rate_limited" when exception.RetryAfterMilliseconds is int milliseconds =>
                $"The relay is rate-limiting pairing requests. Try again in {Math.Max(1, (int)Math.Ceiling(milliseconds / 1000d))} seconds.",
            "rate_limited" => "The relay is rate-limiting pairing requests. Try again shortly.",
            "unexpected_verification_uri" => "The relay returned an unexpected verification URL.",
            "network_error" => "Could not reach the relay after several attempts. Check your connection and try again.",
            "consumed" => "The pairing credential was already collected. Try Connect again.",
            _ => exception.Message
        };
    }

    private static string CurrentRelayPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    private static string CurrentRelayClientVersion()
    {
        var assembly = typeof(ChannelSettingsPanel).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                      assembly.GetName().Version?.ToString(3) ??
                      "0.0.0";
        return version.Length <= 32 ? version : version[..32];
    }

    private void LoadRelayConfiguration(ProviderProfile profile)
    {
        CancelRelayPairing(clearPending: true);
        RelayInstallationTokenBox.Clear();
        ClearRelayTokenBox.IsChecked = false;
        if (profile.Kind != "relay")
        {
            ResetRelayConnectionPresentation(hasStoredCredential: false);
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            RelaySenderNameBox.Text = GetJsonString(root, "sender_name") != "" ? GetJsonString(root, "sender_name") : GetJsonString(root, "senderName");
        }
        catch (JsonException)
        {
            RelaySenderNameBox.Clear();
        }
        ResetRelayConnectionPresentation(
            profile.SecretNames.Contains("installation_token", StringComparer.Ordinal));
    }

}
