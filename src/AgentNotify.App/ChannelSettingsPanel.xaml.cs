using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentNotify.Contracts;
using AgentNotify.Core.Delivery;

namespace AgentNotify.App;

public partial class ChannelSettingsPanel : System.Windows.Controls.UserControl
{
    private ProviderProfileService _profiles = null!;
    private DeliveryRouteService _routes = null!;
    private DeliveryDispatcher _dispatcher = null!;
    private bool _initialized;

    public ChannelSettingsPanel()
    {
        InitializeComponent();
        RoutePriorityBox.ItemsSource = Enum.GetNames<NotificationPriority>();
        RoutePriorityBox.SelectedItem = nameof(NotificationPriority.Normal);
    }

    public void Initialize(
        ProviderProfileService profiles,
        DeliveryRouteService routes,
        DeliveryDispatcher dispatcher)
    {
        _profiles = profiles;
        _routes = routes;
        _dispatcher = dispatcher;
        _initialized = true;
        _ = RunAsync(() => ReloadAsync());
    }

    private async Task ReloadAsync(string? providerId = null, string? routeId = null)
    {
        if (!_initialized)
            return;
        var providers = await _profiles.ListAsync();
        ProviderList.ItemsSource = providers;
        RouteProviderBox.ItemsSource = providers;
        if (providerId is not null)
            ProviderList.SelectedItem = providers.FirstOrDefault(profile => profile.Id == providerId);

        var routes = await _routes.ListAsync();
        RouteList.ItemsSource = routes;
        if (routeId is not null)
            RouteList.SelectedItem = routes.FirstOrDefault(route => route.Id == routeId);
        await RefreshDiagnosticsAsync();
    }

    private void Provider_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderProfile profile)
            return;
        ProviderNameBox.Text = profile.Name;
        ProviderEnabledBox.IsChecked = profile.Enabled;
        EndpointBox.Clear();
        AuthorizationBox.Clear();
        HmacBox.Clear();
        ClearAuthorizationBox.IsChecked = false;
        ClearHmacBox.IsChecked = false;
        AllowPrivateBox.IsChecked = ReadAllowPrivate(profile.ConfigJson);
        StoredSecretsText.Text = profile.SecretNames.Count == 0
            ? "No encrypted values stored."
            : "Stored encrypted fields: " + string.Join(", ", profile.SecretNames);
    }

    private void NewProvider_Click(object sender, RoutedEventArgs e)
    {
        ProviderList.SelectedItem = null;
        ProviderNameBox.Text = "Webhook";
        ProviderEnabledBox.IsChecked = false;
        EndpointBox.Clear();
        AuthorizationBox.Clear();
        HmacBox.Clear();
        ClearAuthorizationBox.IsChecked = false;
        ClearHmacBox.IsChecked = false;
        AllowPrivateBox.IsChecked = false;
        StoredSecretsText.Text = "Enter an HTTPS endpoint. New providers start disabled.";
        ProviderNameBox.Focus();
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var profile = await SaveProviderAsync();
            await ReloadAsync(providerId: profile.Id);
            SetStatus("Provider saved.", success: true);
        });
    }

    private async Task<ProviderProfile> SaveProviderAsync()
    {
        var existing = ProviderList.SelectedItem as ProviderProfile;
        var hasEndpoint = existing?.SecretNames.Contains("endpoint_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(EndpointBox.Password) && !hasEndpoint)
            throw new ArgumentException("Enter the webhook HTTPS endpoint.");

        var keepAuthorization =
            !string.IsNullOrEmpty(AuthorizationBox.Password) ||
            existing?.SecretNames.Contains("authorization", StringComparer.Ordinal) == true &&
            ClearAuthorizationBox.IsChecked != true;
        var keepHmac =
            !string.IsNullOrEmpty(HmacBox.Password) ||
            existing?.SecretNames.Contains("hmac_secret", StringComparer.Ordinal) == true &&
            ClearHmacBox.IsChecked != true;
        var config = JsonSerializer.Serialize(new
        {
            urlSecretName = "endpoint_url",
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            secretHeaders = keepAuthorization
                ? new Dictionary<string, string> { ["Authorization"] = "authorization" }
                : null,
            signature = keepHmac ? new { secretName = "hmac_secret" } : null
        }, Json.Options);

        var initialSecrets = existing is null
            ? BuildEnteredSecrets()
            : null;
        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "webhook",
            ProviderEnabledBox.IsChecked == true,
            config,
            initialSecrets);
        if (existing is not null)
        {
            var remove = new List<string>();
            if (ClearAuthorizationBox.IsChecked == true) remove.Add("authorization");
            if (ClearHmacBox.IsChecked == true) remove.Add("hmac_secret");
            await _profiles.UpdateSecretsAsync(saved.Id, BuildEnteredSecrets(), remove);
        }
        EndpointBox.Clear();
        AuthorizationBox.Clear();
        HmacBox.Clear();
        return saved;
    }

    private Dictionary<string, string> BuildEnteredSecrets()
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(EndpointBox.Password))
            secrets["endpoint_url"] = EndpointBox.Password.Trim();
        if (!string.IsNullOrEmpty(AuthorizationBox.Password))
            secrets["authorization"] = AuthorizationBox.Password;
        if (!string.IsNullOrEmpty(HmacBox.Password))
            secrets["hmac_secret"] = HmacBox.Password;
        return secrets;
    }

    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var profile = await SaveProviderAsync();
            var result = await _dispatcher.TestProviderAsync(profile.Id);
            await ReloadAsync(providerId: profile.Id);
            SetStatus(
                result.Succeeded
                    ? $"Test delivered (HTTP {result.StatusCode})."
                    : $"Test failed: {result.ErrorCode ?? "unspecified"}.",
                result.Succeeded);
        });
    }

    private async void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderProfile profile)
            return;
        if (System.Windows.MessageBox.Show(
                $"Delete provider '{profile.Name}' and its routes/delivery history?",
                "Delete provider",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _profiles.DeleteAsync(profile.Id);
            NewProvider_Click(sender, e);
            await ReloadAsync();
            SetStatus("Provider deleted.", success: true);
        });
    }

    private void Route_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RouteList.SelectedItem is not DeliveryRoute route)
            return;
        RouteNameBox.Text = route.Name;
        RouteProviderBox.SelectedItem = RouteProviderBox.Items.Cast<ProviderProfile>()
            .FirstOrDefault(profile => profile.Id == route.ProviderId);
        RouteEnabledBox.IsChecked = route.Enabled;
        RoutePriorityBox.SelectedItem = route.MinimumPriority.ToString();
        RouteTypeBox.Text = route.TypeId ?? "";
        RouteProjectBox.Text = route.Project ?? "";
        RouteAgentBox.Text = route.Agent ?? "";
        IncludeMessageBox.IsChecked = route.IncludeMessage;
    }

    private void NewRoute_Click(object sender, RoutedEventArgs e)
    {
        RouteList.SelectedItem = null;
        RouteNameBox.Text = "Webhook route";
        RouteProviderBox.SelectedIndex = RouteProviderBox.Items.Count > 0 ? 0 : -1;
        RouteEnabledBox.IsChecked = false;
        RoutePriorityBox.SelectedItem = nameof(NotificationPriority.Normal);
        RouteTypeBox.Clear();
        RouteProjectBox.Clear();
        RouteAgentBox.Clear();
        IncludeMessageBox.IsChecked = false;
    }

    private async void SaveRoute_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (RouteProviderBox.SelectedItem is not ProviderProfile provider)
                throw new ArgumentException("Select a provider.");
            _ = Enum.TryParse<NotificationPriority>(
                RoutePriorityBox.SelectedItem?.ToString(),
                out var priority);
            var route = await _routes.SaveAsync(
                (RouteList.SelectedItem as DeliveryRoute)?.Id,
                RouteNameBox.Text,
                provider.Id,
                RouteEnabledBox.IsChecked == true,
                priority,
                RouteTypeBox.Text,
                RouteProjectBox.Text,
                RouteAgentBox.Text,
                IncludeMessageBox.IsChecked == true);
            await ReloadAsync(routeId: route.Id);
            SetStatus("Route saved. New matching notifications will be queued.", success: true);
        });
    }

    private async void DeleteRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is not DeliveryRoute route)
            return;
        if (System.Windows.MessageBox.Show(
                $"Delete route '{route.Name}' and its delivery history?",
                "Delete route",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _routes.DeleteAsync(route.Id);
            NewRoute_Click(sender, e);
            await ReloadAsync();
            SetStatus("Route deleted.", success: true);
        });
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(RefreshDiagnosticsAsync);

    private async Task RefreshDiagnosticsAsync()
    {
        var snapshot = await _dispatcher.GetDiagnosticsAsync();
        DiagnosticsText.Text =
            $"Pending: {snapshot.Pending}   Processing: {snapshot.Processing}   " +
            $"Retry: {snapshot.Retry}   Delivered: {snapshot.Delivered}   " +
            $"Dead-letter: {snapshot.DeadLetter}\n\n" +
            "Registered adapters: " + string.Join(", ", snapshot.RegisteredAdapters);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            IsEnabled = false;
            SetStatus("Working…", success: true);
            await operation();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, success: false);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void SetStatus(string message, bool success)
    {
        ChannelStatusText.Text = message;
        ChannelStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                success ? "#86EFAC" : "#FCA5A5"));
    }

    private static bool ReadAllowPrivate(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.TryGetProperty("allowPrivateNetwork", out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
