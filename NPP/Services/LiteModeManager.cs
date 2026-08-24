using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Netch.App.Models;
using Netch.Controllers;
using Netch.Enums;
using Netch.Models.Modes.ProcessMode;
using Netch.Servers;
using Netch.Utils;
using Serilog;

namespace Netch.App.Services;

public partial class LiteModeManager : ObservableObject
{
    private const int ProxyProbeTimeoutMilliseconds = 2500;
    private const ulong TrafficLogStepBytes = 1024 * 1024;
    private static readonly byte[] Socks5NoAuthenticationGreeting = [0x05, 0x01, 0x00];

    private readonly MainController _mainController;
    private readonly NetchAppContext _appContext;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private IReadOnlyList<string> _currentProcessNames = Array.Empty<string>();
    private CancellationTokenSource? _trafficMonitorCts;
    private Task? _trafficMonitorTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartStop))]
    [NotifyPropertyChangedFor(nameof(ControlButtonText))]
    private State _currentState = State.Waiting;

    [ObservableProperty]
    private string _statusText = "等待操作";

    public bool CanStartStop => CurrentState is State.Waiting or State.Stopped or State.Started;

    public string ControlButtonText => CurrentState switch
    {
        State.Waiting or State.Stopped => "开始运行",
        State.Started => "停止运行",
        State.Starting => "正在启动...",
        State.Stopping => "正在停止...",
        _ => string.Empty
    };

    public IReadOnlyList<string> CurrentProcessNames => _currentProcessNames;

    public LiteModeManager(MainController mainController, NetchAppContext appContext)
    {
        _mainController = mainController;
        _appContext = appContext;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task<bool> StartAsync(string proxyHost, string proxyPort, IReadOnlyCollection<ProcessEntry> processes)
    {
        await _operationLock.WaitAsync();
        try
        {
            if (CurrentState == State.Started)
                return true;

            proxyHost = proxyHost.Trim();
            proxyPort = proxyPort.Trim();
            if (string.IsNullOrWhiteSpace(proxyHost))
            {
                StatusText = "请输入代理地址";
                return false;
            }

            if (!ushort.TryParse(proxyPort, out var port) || port == 0)
            {
                StatusText = "请输入有效的端口（1-65535）";
                return false;
            }

            var selectedProcesses = processes
                .Where(p => p.IsSelected)
                .Where(p => !string.IsNullOrWhiteSpace(p.FileName))
                .Where(p => File.Exists(p.FullPath))
                .GroupBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToArray();

            if (selectedProcesses.Length == 0)
            {
                StatusText = "没有可用的进程规则，请重新选择应用";
                return false;
            }

            // Capture a stable configuration snapshot before the asynchronous
            // SOCKS5 probe. Advanced settings only take effect on the next start.
            var redirectorConfig = _appContext.Settings.Redirector;
            var enableUdp = redirectorConfig.FilterUDP;
            var includeChildProcesses = redirectorConfig.FilterParent;
            var bypassLan = redirectorConfig.BypassLan;
            var dnsHost = NormalizeDnsEndpointOrDefault(redirectorConfig.DNSHost);

            CurrentState = State.Starting;
            StatusText = "正在测试代理连接...";
            var probe = await ProbeSocks5Async(proxyHost, port);
            if (!probe.HostResolved)
            {
                Log.Warning("Cannot resolve host {Host}", proxyHost);
                StatusText = $"无法解析地址或解析超时: {proxyHost}";
                CurrentState = State.Stopped;
                return false;
            }

            if (probe.Address == null)
            {
                Log.Warning("Proxy {Host}:{Port} did not complete a no-authentication SOCKS5 handshake", proxyHost, port);
                StatusText = $"SOCKS5 代理不可用或需要认证: {proxyHost}:{port}";
                CurrentState = State.Stopped;
                return false;
            }

            Log.Information("SOCKS5 proxy {Host}:{Port} handshake succeeded via {Address} in {Delay}ms",
                proxyHost, port, probe.Address, probe.ElapsedMilliseconds);

            _currentProcessNames = selectedProcesses
                .Select(p => p.FileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            OnPropertyChanged(nameof(CurrentProcessNames));

            var server = new Socks5Server(proxyHost, port);
            var mode = new Redirector
            {
                FilterTCP = true,
                FilterUDP = enableUdp,
                // The native redirector cannot safely scope DNS by process when
                // UDP filtering is disabled, so DNS interception follows UDP.
                FilterDNS = enableUdp,
                FilterParent = includeChildProcesses,
                DNSHost = dnsHost,
                DNSProxy = true,
                FilterIntranet = !bypassLan,
                FilterLoopback = false,
                FilterICMP = false,
                // In the native redirector this flag also controls whether UDP
                // endpoints that do not match a process rule are released from
                // filtering. It must stay enabled for strict process isolation;
                // matched endpoints still proxy TCP, UDP and DNS normally.
                HandleOnlyDNS = true
            };

            Log.Information(
                "Redirector options: UDP {EnableUdp}, child processes {IncludeChildProcesses}, bypass LAN {BypassLan}, DNS {DnsHost}",
                enableUdp,
                includeChildProcesses,
                bypassLan,
                dnsHost);

            var handleRules = selectedProcesses
                .SelectMany(CreateHandleRules)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var rule in handleRules)
                mode.Handle.Add(rule);

            Log.Information("Selected process paths ({Count}): {List}",
                selectedProcesses.Length, string.Join("; ", selectedProcesses.Select(p => p.FullPath)));
            Log.Information("Redirector handle rules ({Count}): {Rules}",
                handleRules.Length, string.Join("; ", handleRules));
            Log.Information("Starting redirector -> {Host}:{Port}, processes: {List}",
                proxyHost, port, string.Join(", ", selectedProcesses.Select(p => p.FileName)));

            StatusText = "启动中...";
            await _mainController.StartAsync(server, mode);
            CurrentState = State.Started;
            StatusText = "运行中 - 暂无转发流量";
            StartTrafficMonitor();
            Log.Information("Redirector started successfully");
            return true;
        }
        catch (Exception ex)
        {
            await StopTrafficMonitorAsync();
            Log.Error(ex, "Redirector start failed");
            if (_mainController.IsStopped)
            {
                _currentProcessNames = Array.Empty<string>();
                OnPropertyChanged(nameof(CurrentProcessNames));
                StatusText = $"启动失败: {GetFailureMessage(ex)}";
                CurrentState = State.Stopped;
            }
            else
            {
                StatusText = $"启动失败且未完全停止: {GetFailureMessage(ex)}";
                CurrentState = State.Started;
            }

            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<bool> StopAsync()
    {
        return StopCoreAsync(throwOnFailure: false);
    }

    public async Task StopForUpdateAsync()
    {
        await StopCoreAsync(throwOnFailure: true);
    }

    private async Task<bool> StopCoreAsync(bool throwOnFailure)
    {
        await _operationLock.WaitAsync();
        try
        {
            CurrentState = State.Stopping;
            StatusText = "停止中...";
            await StopTrafficMonitorAsync();
            if (throwOnFailure)
                await _mainController.StopForUpdateAsync();
            else
                await _mainController.StopAsync();

            _currentProcessNames = Array.Empty<string>();
            OnPropertyChanged(nameof(CurrentProcessNames));
            CurrentState = State.Stopped;
            StatusText = "已停止";
            Log.Information("Redirector stopped");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Redirector stop failed");
            StatusText = $"停止失败: {GetFailureMessage(ex)}";
            CurrentState = State.Started;
            if (throwOnFailure)
                throw;

            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static IEnumerable<string> CreateHandleRules(ProcessEntry process)
    {
        var fullPath = process.FullPath.Trim();
        if (!string.IsNullOrWhiteSpace(fullPath))
            yield return "^" + fullPath.ToRegexString() + "$";
    }

    private void StartTrafficMonitor()
    {
        var cts = new CancellationTokenSource();
        _trafficMonitorCts = cts;
        _trafficMonitorTask = MonitorTrafficAsync(cts.Token);
    }

    private async Task StopTrafficMonitorAsync()
    {
        var cts = _trafficMonitorCts;
        var task = _trafficMonitorTask;
        if (cts == null)
            return;

        try
        {
            await cts.CancelAsync();
            if (task != null)
                await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Traffic reporting must never prevent the redirector itself from
            // releasing its driver, rules and update-locked files.
            Log.Warning(ex, "Traffic monitor failed while stopping");
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(cts, _trafficMonitorCts))
            {
                _trafficMonitorCts = null;
                _trafficMonitorTask = null;
            }
        }
    }

    private static string GetFailureMessage(Exception exception)
    {
        return exception.GetBaseException().Message;
    }

    private static string NormalizeDnsEndpointOrDefault(string? value)
    {
        var fallback = $"{Constants.DefaultPrimaryDNS}:53";
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var candidate = value.Trim();
        IPEndPoint endpoint;
        if (IPAddress.TryParse(candidate, out var address))
        {
            endpoint = new IPEndPoint(address, 53);
        }
        else
        {
            if (!IPEndPoint.TryParse(candidate, out var parsedEndpoint) || parsedEndpoint == null)
            {
                Log.Warning("Invalid DNS endpoint {DnsHost}; using {Fallback}", value, fallback);
                return fallback;
            }

            endpoint = parsedEndpoint;
            if (endpoint.Port == 0)
                endpoint.Port = 53;
        }

        if (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.Address.ScopeId != 0)
        {
            Log.Warning("Scoped IPv6 DNS endpoint {DnsHost} is unsupported; using {Fallback}", value, fallback);
            return fallback;
        }

        return endpoint.ToString();
    }

    private async Task MonitorTrafficAsync(CancellationToken token)
    {
        ulong lastLoggedTotal = 0;

        while (true)
        {
            await Task.Delay(1000, token);

            var upload = Netch.Interops.Redirector.GetUploadBytes();
            var download = Netch.Interops.Redirector.GetDownloadBytes();
            var total = upload + download;

            UpdateStatusText(total == 0
                ? "运行中 - 暂无转发流量"
                : $"运行中 - 上传 {FormatBytes(upload)} / 下载 {FormatBytes(download)}");

            if (total > 0 && (lastLoggedTotal == 0 || total - lastLoggedTotal >= TrafficLogStepBytes))
            {
                Log.Information("Redirector traffic: UP {Upload}, DOWN {Download}",
                    FormatBytes(upload), FormatBytes(download));
                lastLoggedTotal = total;
            }
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (double)bytes;
        var index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }
    private void UpdateStatusText(string value)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            StatusText = value;
            return;
        }

        _dispatcherQueue.TryEnqueue(() => StatusText = value);
    }

    private static async Task<(bool HostResolved, IPAddress? Address, long ElapsedMilliseconds)> ProbeSocks5Async(
        string host,
        ushort port)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ProxyProbeTimeoutMilliseconds));
        var token = timeoutCts.Token;

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var ip))
        {
            addresses = [ip];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                return (false, null, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        addresses = addresses
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Distinct()
            .ToArray();
        if (addresses.Length == 0)
            return (false, null, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        var pending = addresses
            .Select(address => ProbeSocks5EndpointAsync(address, port, token))
            .ToList();

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var successfulAddress = await completed;
            if (successfulAddress == null)
                continue;

            await timeoutCts.CancelAsync();
            if (pending.Count > 0)
                await Task.WhenAll(pending);

            return (true, successfulAddress, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return (true, null, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<IPAddress?> ProbeSocks5EndpointAsync(
        IPAddress address,
        ushort port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, cancellationToken);

            var stream = client.GetStream();
            await stream.WriteAsync(Socks5NoAuthenticationGreeting, cancellationToken);

            var response = new byte[2];
            await stream.ReadExactlyAsync(response, cancellationToken);
            if (response[0] == 0x05 && response[1] == 0x00)
                return address;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
        }

        return null;
    }
}
