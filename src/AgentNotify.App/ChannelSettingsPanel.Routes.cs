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
    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var profile = await SaveProviderAsync();
            var result = await _dispatcher.TestProviderAsync(profile.Id);
            await ReloadAsync(providerId: profile.Id);
            SetStatus(
                result.Succeeded
                    ? $"Test delivered (provider status {result.StatusCode?.ToString() ?? "ok"})."
                    : result.ErrorCode switch
                    {
                        "no_devices_paired" => "Connected to the relay, but no phone is paired yet. Pair a phone from the relay console, then send a test.",
                        "relay_device_not_found" => "The selected Relay phone is no longer paired. Pair it again or remove the pinned device setting, then send a test.",
                        "relay_installation_identity_missing" => "This Relay profile predates installation identity. Reconnect it with Connect so the relay accepts its envelopes.",
                        _ => $"Test failed: {result.ErrorCode ?? "unspecified"}."
                    },
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
            StartNewProvider();
            await ReloadAsync();
            SetStatus("Provider deleted.", success: true);
        });
    }

    private void Route_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RouteList.SelectedItem is not DeliveryRoute route)
        {
            ResetRouteForm();
            return;
        }
        RouteEditorHeader.Text = $"Editing “{route.Name}”";
        RouteBackToNewButton.Visibility = Visibility.Visible;
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

    private void NewRoute_Click(object sender, RoutedEventArgs e) => StartNewRoute();

    private void RouteList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        e.Handled = true;
        StartNewRoute();
    }

    private void StartNewRoute()
    {
        try
        {
            if (RouteList.SelectedItem is null)
                ResetRouteForm();
            else
                RouteList.SelectedItem = null;
            SetStatus("Editing a new route. Fill in the fields, then press Save route.", success: true);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not start a new route: {exception.Message}", success: false);
        }
    }

    private void ResetRouteForm()
    {
        RouteEditorHeader.Text = "New route";
        RouteBackToNewButton.Visibility = Visibility.Collapsed;
        RouteNameBox.Text = "Delivery route";
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
            StartNewRoute();
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
                success ? "#4ADE80" : "#F87171"));
    }

    private static bool ReadAllowPrivate(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (document.RootElement.TryGetProperty("allowPrivateNetwork", out var value) && value.ValueKind == JsonValueKind.True)
                return true;
            if (document.RootElement.TryGetProperty("allow_private_network", out var snake) && snake.ValueKind == JsonValueKind.True)
                return true;
            if (document.RootElement.TryGetProperty("AllowPrivateNetwork", out var pascal) && pascal.ValueKind == JsonValueKind.True)
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
