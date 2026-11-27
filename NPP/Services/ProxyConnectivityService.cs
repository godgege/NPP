using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace Netch.App.Services;

public enum ConnectionHealthState { Unknown, Checking, Connected, Failed }

public sealed class ConnectionHealth : ObservableObject
{
    public ConnectionHealthState State { get; private set; }
    public string Message { get; private set; } = "未检测";
    public bool CanCheck => State != ConnectionHealthState.Checking;
    public string ButtonText => CanCheck ? "检测连通" : "检测中…";
    public string Tooltip => $"{Message}\n点击“检测连通”可重新检测当前 SOCKS5 入口。";
    public Brush IndicatorBrush => new SolidColorBrush(State switch
    {
        ConnectionHealthState.Connected => ColorHelper.FromArgb(255, 30, 166, 114),
        ConnectionHealthState.Failed => ColorHelper.FromArgb(255, 218, 65, 74),
        ConnectionHealthState.Checking => ColorHelper.FromArgb(255, 224, 151, 42),
        _ => ColorHelper.FromArgb(255, 139, 151, 168)
    });

    internal void Set(ConnectionHealthState state, string message)
    {
        State = state;
        Message = message;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(IndicatorBrush));
    }

    internal void Apply(ProxyEndpoint endpoint, ConnectivityResult result) => Set(
        result.Connected ? ConnectionHealthState.Connected : ConnectionHealthState.Failed,
        $"{endpoint.Display} · {result.Message}");
}

/// <summary>Checks the SOCKS5 endpoint configured on the home page.</summary>
public sealed class ProxyConnectivityService : IDisposable
{
    private const int StartupCheckSeconds = 20;
    private CancellationTokenSource? _activeCheck;
    private ProxyEndpoint? _endpoint;
    private bool _disposed;

    public ConnectionHealth Home { get; } = new();

    public void SetHomeEndpoint(string host, string port)
    {
        ProxyEndpoint.TryCreate(host, port, out var endpoint);
        if (_disposed || _endpoint == endpoint)
            return;
        _endpoint = endpoint;
        CancelActiveCheck();
        Home.Set(ConnectionHealthState.Unknown, "地址或端口已变更，请检测连通");
    }

    public async Task CheckHomeAsync(string host, string port, bool startup = false)
    {
        if (_disposed)
            return;
        SetHomeEndpoint(host, port);
        if (_activeCheck is not null)
            return;
        if (_endpoint is not { } endpoint)
        {
            Home.Set(ConnectionHealthState.Failed, "请输入有效的 SOCKS5 地址和端口");
            return;
        }

        using var check = new CancellationTokenSource();
        _activeCheck = check;
        Home.Set(ConnectionHealthState.Checking, $"正在检测 {endpoint.Display}…");
        try
        {
            var result = startup
                ? await Socks5ConnectivityProbe.CheckWithRetriesAsync(endpoint, TimeSpan.FromSeconds(StartupCheckSeconds), check.Token)
                : await Socks5ConnectivityProbe.CheckAsync(endpoint, check.Token);
            if (_activeCheck != check)
                return;
            _activeCheck = null;
            Home.Apply(endpoint, result);
            Log.Information("SOCKS5 connectivity check {Endpoint}: {Connected}", endpoint.Display, result.Connected);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log.Warning(exception, "SOCKS5 connectivity check failed");
            if (_activeCheck == check)
            {
                _activeCheck = null;
                Home.Set(ConnectionHealthState.Failed, "检测失败，请检查代理配置后重试");
            }
        }
    }

    private void CancelActiveCheck()
    {
        var check = _activeCheck;
        _activeCheck = null;
#pragma warning disable VSTHRD103
        check?.Cancel();
#pragma warning restore VSTHRD103
    }

    public void Dispose()
    {
        _disposed = true;
        CancelActiveCheck();
    }
}
