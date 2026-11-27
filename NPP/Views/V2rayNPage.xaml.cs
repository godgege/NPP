using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Netch.App.Services;
using WinRT.Interop;

namespace Netch.App.Views;

public sealed partial class V2rayNPage : Page
{
    private readonly EmbeddedV2rayNService _client;
    private bool _active;

    public V2rayNPage()
    {
        InitializeComponent();
        _client = App.Services.GetRequiredService<EmbeddedV2rayNService>();
    }

    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        _active = true;
        _client.StateChanged += Client_StateChanged;
        UpdateState();
        if (NetchAppContext.Current?.Settings.StartEmbeddedClientWhenOpened == true)
            _ = StartClientAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        _active = false;
        _client.StateChanged -= Client_StateChanged;
        _client.SetVisible(false);
    }

    private async Task StartClientAsync()
    {
        try { await _client.EnsureStartedAsync(WindowNative.GetWindowHandle(App.MainWindow)); }
        catch { /* The service exposes a logged, actionable error in this page. */ }
        if (_active)
            UpdateState();
    }

    private void Client_StateChanged(object? sender, EventArgs args) => UpdateState();

    private void UpdateState()
    {
        ClientStatus.Text = _client.StatusText;
        StartupProgress.IsActive = _client.IsStarting;
        StartupProgress.Visibility = _client.IsStarting ? Visibility.Visible : Visibility.Collapsed;
        StartClientButton.Visibility = !_client.IsReady && !_client.IsStarting ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = _client.IsReady ? Visibility.Collapsed : Visibility.Visible;
        UpdateBounds();
        _client.SetVisible(_active && _client.IsReady);
    }

    private void StartClientButton_Click(object sender, RoutedEventArgs args) => _ = StartClientAsync();
    private void ClientSlot_LayoutUpdated(object? sender, object args) => UpdateBounds();

    private void UpdateBounds()
    {
        if (!_active || ClientSlot.XamlRoot is null || ClientSlot.ActualWidth <= 0 || ClientSlot.ActualHeight <= 0)
            return;
        var bounds = ClientSlot.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, ClientSlot.ActualWidth, ClientSlot.ActualHeight));
        var scale = ClientSlot.XamlRoot.RasterizationScale;
        _client.SetBounds((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));
    }
}
