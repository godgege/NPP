using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Netch.App.Models;
using Netch.App.Services;
using Netch.Enums;
using Netch.Models;
using Netch.Utils;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Netch.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const string AppSourcePrefix = "app|";
    private const string DirectorySourcePrefix = "directory|";
    private readonly NetchAppContext _appContext;
    private readonly LiteModeManager _liteModeManager;
    private readonly Configuration _configuration;
    private const string Socks5Protocol = "SOCKS5";
    private List<InstalledApp> _allApps = new();
    private CancellationTokenSource? _saveCts;
    private readonly object _operationSync = new();
    private readonly object _initializationSync = new();
    private readonly HashSet<string> _explicitlyRemovedSourceKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _backgroundOperations = [];
    private Task _activeOperation = Task.CompletedTask;
    private Task? _initializationTask;
    private bool _isShuttingDown;
    private bool _shellInitialized;
    private bool _isInitializing;
    private bool _homeInitializationSucceeded;
    private bool _hasScannedDirectory;
    private bool _directoryScanFailed;
    private CancellationTokenSource? _directoryScanCts;
    private CancellationTokenSource? _applicationScanCts;

    [ObservableProperty]
    private bool _isHomeLoading = true;

    [ObservableProperty]
    private string _homeLoadError = string.Empty;

    [ObservableProperty]
    private string _proxyHost = string.Empty;

    [ObservableProperty]
    private string _proxyPort = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _useLocalhostOverride;

    [ObservableProperty]
    private bool _useDirectorySource;

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    [ObservableProperty]
    private bool _scanSubdirectories = true;

    [ObservableProperty]
    private bool _isDirectoryScanning;

    private bool _isApplicationScanning;

    private string _applicationSelectionStatusText = "选择应用来源";

    public bool IsApplicationScanning
    {
        get => _isApplicationScanning;
        private set
        {
            if (!SetProperty(ref _isApplicationScanning, value))
                return;

            OnApplicationScanningStateChanged();
        }
    }

    public string ApplicationSelectionStatusText
    {
        get => _applicationSelectionStatusText;
        private set => SetProperty(ref _applicationSelectionStatusText, value);
    }

    public string SelectedProxyProtocol { get; set; } = Socks5Protocol;

    public IReadOnlyList<string> ProxyProtocols { get; } = [Socks5Protocol];

    public State CurrentState => _liteModeManager.CurrentState;

    public string StatusText => _liteModeManager.StatusText;

    public string RunStateLabel => CurrentState switch
    {
        State.Waiting or State.Stopped => "当前已停止",
        State.Started => "代理运行中",
        State.Starting => "正在启动",
        State.Stopping => "正在停止",
        _ => "状态未知"
    };

    public bool IsHomeLoaded => !IsHomeLoading && _homeInitializationSucceeded;

    public Visibility HomeLoadingVisibility => IsHomeLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HomeErrorVisibility => !IsHomeLoading && !_homeInitializationSucceeded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanRetryHomeLoad => !IsHomeLoading && !_homeInitializationSucceeded;

    public bool CanEditConfiguration => IsHomeLoaded &&
                                        _homeInitializationSucceeded &&
                                        !_isShuttingDown &&
                                        !IsApplicationScanning &&
                                        CurrentState is State.Waiting or State.Stopped;

    private bool CanCommitConfiguration => IsHomeLoaded &&
                                           _homeInitializationSucceeded &&
                                           !_isShuttingDown &&
                                           CurrentState is State.Waiting or State.Stopped;

    public bool IsHostEditable => CanEditConfiguration && !UseLocalhostOverride;

    public bool UseAppListSource
    {
        get => !UseDirectorySource;
        set
        {
            if (value)
                UseDirectorySource = false;
        }
    }

    public bool UseDirectorySearchSource
    {
        get => UseDirectorySource;
        set
        {
            if (value)
                UseDirectorySource = true;
        }
    }

    public Visibility AppListSourceVisibility => UseDirectorySource ? Visibility.Collapsed : Visibility.Visible;

    public Visibility DirectorySourceVisibility => UseDirectorySource ? Visibility.Visible : Visibility.Collapsed;

    public bool CanScanDirectory => CanEditConfiguration &&
                                    !IsDirectoryScanning &&
                                    !string.IsNullOrWhiteSpace(DirectoryPath);

    public string DirectoryScanButtonText => IsDirectoryScanning ? "扫描中" : "扫描";

    public Visibility DirectoryEmptyStateVisibility => DirectoryProcesses.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DirectoryProcessListVisibility => DirectoryProcesses.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string DirectoryEmptyTitle => _directoryScanFailed
        ? "目录扫描失败"
        : _hasScannedDirectory
            ? "当前目录没有可执行程序"
            : string.IsNullOrWhiteSpace(DirectoryPath)
                ? "选择目录后开始扫描"
                : "点击扫描查找可执行程序";

    public string DirectoryEmptyDescription => _directoryScanFailed
        ? "无法读取此目录，请检查路径或访问权限"
        : _hasScannedDirectory
            ? "可以更换目录，或调整扫描范围后重试"
            : "扫描到的可执行程序会显示在这里，勾选后加入代理规则";

    public ObservableCollection<InstalledApp> FilteredApps { get; } = new();

    public BulkObservableCollection<ProcessEntry> DirectoryProcesses { get; } = new();

    public ObservableCollection<ProcessGroup> ProcessGroups { get; } = new();

    public string SelectedAppsTitle => $"当前已选择 {ProcessGroups.Count} 个应用";

    public string AppsSearchPlaceholder => $"搜索 {_allApps.Count} 个应用";

    public bool CanStartStop => IsHomeLoaded &&
                                _homeInitializationSucceeded &&
                                !_isShuttingDown &&
                                !IsApplicationScanning &&
                                _liteModeManager.CanStartStop;

    public string ControlButtonText => _liteModeManager.ControlButtonText;

    public MainViewModel(NetchAppContext appContext, LiteModeManager liteModeManager, Configuration configuration)
    {
        _appContext = appContext;
        _liteModeManager = liteModeManager;
        _configuration = configuration;
        _liteModeManager.PropertyChanged += LiteModeManager_PropertyChanged;
    }

    public AdvancedProxySettings GetAdvancedProxySettings()
    {
        var config = _appContext.Settings.Redirector;
        var dnsHost = TryNormalizeDnsEndpoint(config.DNSHost, out var normalizedDnsHost)
            ? normalizedDnsHost
            : new RedirectorConfig().DNSHost;

        return new AdvancedProxySettings(
            config.FilterUDP,
            config.FilterParent,
            config.BypassLan,
            dnsHost);
    }

    public static AdvancedProxySettings GetDefaultAdvancedProxySettings()
    {
        var defaults = new RedirectorConfig();
        return new AdvancedProxySettings(
            defaults.FilterUDP,
            defaults.FilterParent,
            defaults.BypassLan,
            defaults.DNSHost);
    }

    public async Task SaveAdvancedProxySettingsAsync(AdvancedProxySettings settings)
    {
        if (!CanCommitConfiguration)
            throw new InvalidOperationException("代理运行或页面加载期间不能修改高级设置。");

        if (!TryNormalizeDnsEndpoint(settings.DnsHost, out var normalizedDnsHost))
            throw new ArgumentException("DNS 服务器格式无效，请输入 IP:端口，例如 1.1.1.1:53。IPv6 请使用 [地址]:端口。");

        var config = _appContext.Settings.Redirector;
        var previousUdp = config.FilterUDP;
        var previousParent = config.FilterParent;
        var previousBypassLan = config.BypassLan;
        var previousDnsHost = config.DNSHost;

        config.FilterUDP = settings.EnableUdp;
        config.FilterParent = settings.IncludeChildProcesses;
        config.BypassLan = settings.BypassLan;
        config.DNSHost = normalizedDnsHost;

        try
        {
            await _configuration.SaveOrThrowAsync();
            Serilog.Log.Information(
                "Advanced proxy settings saved: UDP {EnableUdp}, child processes {IncludeChildProcesses}, bypass LAN {BypassLan}, DNS {DnsHost}",
                settings.EnableUdp,
                settings.IncludeChildProcesses,
                settings.BypassLan,
                normalizedDnsHost);
        }
        catch
        {
            config.FilterUDP = previousUdp;
            config.FilterParent = previousParent;
            config.BypassLan = previousBypassLan;
            config.DNSHost = previousDnsHost;
            throw;
        }
    }

    private static bool TryNormalizeDnsEndpoint(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        IPEndPoint endpoint;
        if (IPAddress.TryParse(candidate, out var address))
        {
            endpoint = new IPEndPoint(address, 53);
        }
        else
        {
            if (!IPEndPoint.TryParse(candidate, out var parsedEndpoint) || parsedEndpoint == null)
                return false;

            endpoint = parsedEndpoint;
            if (endpoint.Port == 0)
                endpoint.Port = 53;
        }

        if (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.Address.ScopeId != 0)
            return false;

        normalized = endpoint.ToString();
        return true;
    }

    public void InitializeShell()
    {
        if (_shellInitialized)
            return;

        _shellInitialized = true;
        _isInitializing = true;
        IsHomeLoading = true;

        var settings = _appContext.Settings;
        SelectedProxyProtocol = Socks5Protocol;
        UseLocalhostOverride = settings.UseLocalhostOverride;
        ProxyHost = UseLocalhostOverride
            ? "127.0.0.1"
            : string.IsNullOrWhiteSpace(settings.LiteProxyHost) ? "127.0.0.1" : settings.LiteProxyHost.Trim();
        ProxyPort = string.IsNullOrWhiteSpace(settings.LiteProxyPort) ? "10808" : settings.LiteProxyPort.Trim();

        _allApps = [];
        FilteredApps.Clear();
        _hasScannedDirectory = false;
        _directoryScanFailed = false;
        DirectoryProcesses.Clear();
        ProcessGroups.Clear();
        _explicitlyRemovedSourceKeys.Clear();
        NotifyDirectoryResultsChanged();
    }

    public Task InitializeAsync()
    {
        InitializeShell();
        lock (_initializationSync)
            return _initializationTask ??= InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            var snapshot = CaptureInitializationSnapshot();
            var apps = await AppDiscoveryService.DiscoverInstalledAppsAsync();
            var result = await Task.Run(() => BuildInitializationResult(apps, snapshot));

            _allApps = result.Apps;
            FilteredApps.Clear();
            DirectoryProcesses.Clear();
            ProcessGroups.Clear();
            foreach (var group in result.ProcessGroups)
                ProcessGroups.Add(group);

            ApplyFilter();
            _homeInitializationSucceeded = true;
            HomeLoadError = string.Empty;
            OnPropertyChanged(nameof(SelectedAppsTitle));
            OnPropertyChanged(nameof(AppsSearchPlaceholder));
            NotifyDirectoryResultsChanged();
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to initialize the home page");
            _homeInitializationSucceeded = false;
            _allApps = [];
            FilteredApps.Clear();
            ProcessGroups.Clear();
            HomeLoadError = "应用列表加载失败，请检查系统权限后重试";
            _liteModeManager.StatusText = HomeLoadError;
            OnPropertyChanged(nameof(SelectedAppsTitle));
            OnPropertyChanged(nameof(AppsSearchPlaceholder));
        }
        finally
        {
            _isInitializing = false;
            IsHomeLoading = false;
        }
    }

    private HomeInitializationSnapshot CaptureInitializationSnapshot()
    {
        var settings = _appContext.Settings;
        var savedPaths = new HashSet<string>(
            settings.SelectedAppPaths ?? [],
            StringComparer.OrdinalIgnoreCase);
        var savedGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceKey, processPaths) in settings.LiteProcessGroups ?? [])
            savedGroups[sourceKey] = [.. processPaths];

        return new HomeInitializationSnapshot(savedPaths, savedGroups);
    }

    private static HomeInitializationResult BuildInitializationResult(
        List<InstalledApp> apps,
        HomeInitializationSnapshot snapshot)
    {
        var groups = new List<ProcessGroup>();
        foreach (var app in apps)
        {
            if (!snapshot.SavedPaths.Contains(app.InstallPath))
                continue;

            app.IsSelected = true;
            var sourceKey = CreateAppSourceKey(app.InstallPath);
            var group = new ProcessGroup { GroupName = app.Name, SourceKey = sourceKey };
            var persistedPaths = FindSavedPaths(snapshot.SavedGroups, sourceKey);
            if (persistedPaths != null)
            {
                RestorePathsInto(persistedPaths, group);
            }
            else
            {
                RestoreDiscoveredPathsInto(app.InstallPath, group);
            }

            if (group.Processes.Count > 0)
                groups.Add(group);
            else
                app.IsSelected = false;
        }

        foreach (var (sourceKey, processPaths) in snapshot.SavedGroups)
        {
            if (!sourceKey.StartsWith(DirectorySourcePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var directory = sourceKey[DirectorySourcePrefix.Length..];
            var group = new ProcessGroup
            {
                GroupName = GetDirectoryGroupName(directory),
                SourceKey = sourceKey
            };
            RestorePathsInto(processPaths, group);
            if (group.Processes.Count > 0)
                groups.Add(group);
        }

        return new HomeInitializationResult(apps, groups);
    }

    private static void RestoreDiscoveredPathsInto(string installPath, ProcessGroup group)
    {
        try
        {
            foreach (var exe in EnumerateExesSafe(installPath, recursive: true))
                group.Processes.Add(new ProcessEntry { FullPath = exe });
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
        }
    }

    public void Dispose()
    {
        _liteModeManager.PropertyChanged -= LiteModeManager_PropertyChanged;
        RequestCancellation(_applicationScanCts);
        RequestCancellation(_directoryScanCts);
        RequestCancellation(_saveCts);
    }

    public async Task SaveSettingsAsync()
    {
        await InitializeAsync();
        if (_homeInitializationSucceeded)
            await SaveSelectionAsync(throwOnFailure: true);
    }

    [RelayCommand(CanExecute = nameof(CanRetryHomeLoad))]
    private async Task RetryHomeLoadAsync()
    {
        lock (_initializationSync)
        {
            if (IsHomeLoading || _homeInitializationSucceeded)
                return;

            _initializationTask = null;
        }

        HomeLoadError = string.Empty;
        IsHomeLoading = true;
        await InitializeAsync();
    }

    public void BeginShutdown()
    {
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;
        RequestCancellation(_applicationScanCts);
        RequestCancellation(_directoryScanCts);
        RequestCancellation(_saveCts);
        OnPropertyChanged(nameof(CanStartStop));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(IsHostEditable));
        OnPropertyChanged(nameof(CanScanDirectory));
        StartStopCommand.NotifyCanExecuteChanged();
        ScanDirectoryCommand.NotifyCanExecuteChanged();
    }

    public void ResumeAfterFailedShutdown()
    {
        if (!_isShuttingDown)
            return;

        _isShuttingDown = false;
        OnPropertyChanged(nameof(CanStartStop));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(IsHostEditable));
        OnPropertyChanged(nameof(CanScanDirectory));
        StartStopCommand.NotifyCanExecuteChanged();
        ScanDirectoryCommand.NotifyCanExecuteChanged();
    }

    // The operation is captured on the UI context and is only awaited asynchronously
    // during shutdown; no caller blocks synchronously on this task.
#pragma warning disable VSTHRD003
    public Task WaitForActiveOperationAsync()
    {
        lock (_operationSync)
            return Task.WhenAll([_activeOperation, .. _backgroundOperations]);
    }
#pragma warning restore VSTHRD003

    partial void OnIsHomeLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsHomeLoaded));
        OnPropertyChanged(nameof(HomeLoadingVisibility));
        OnPropertyChanged(nameof(HomeErrorVisibility));
        OnPropertyChanged(nameof(CanRetryHomeLoad));
        OnPropertyChanged(nameof(CanStartStop));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(IsHostEditable));
        OnPropertyChanged(nameof(CanScanDirectory));
        StartStopCommand.NotifyCanExecuteChanged();
        ScanDirectoryCommand.NotifyCanExecuteChanged();
        RetryHomeLoadCommand.NotifyCanExecuteChanged();
    }

    partial void OnHomeLoadErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HomeErrorVisibility));
    }

    partial void OnIsDirectoryScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScanDirectory));
        OnPropertyChanged(nameof(DirectoryScanButtonText));
        ScanDirectoryCommand.NotifyCanExecuteChanged();
    }

    private void OnApplicationScanningStateChanged()
    {
        OnPropertyChanged(nameof(CanStartStop));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(IsHostEditable));
        OnPropertyChanged(nameof(CanScanDirectory));
        StartStopCommand.NotifyCanExecuteChanged();
        ScanDirectoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnProxyHostChanged(string value)
    {
        if (!_isInitializing)
            _ = SaveSelectionWithDebounceAsync();
    }

    partial void OnProxyPortChanged(string value)
    {
        if (!_isInitializing)
            _ = SaveSelectionWithDebounceAsync();
    }

    public void ClearSelection()
    {
        RequestCancellation(_applicationScanCts);
        foreach (var app in _allApps)
            app.IsSelected = false;
        foreach (var process in DirectoryProcesses)
            process.IsSelected = false;

        ProcessGroups.Clear();
        var settings = _appContext.Settings;
        _explicitlyRemovedSourceKeys.UnionWith(settings.LiteProcessGroups.Keys);
        settings.SelectedAppPaths.Clear();
        settings.LiteProcessGroups.Clear();
        OnPropertyChanged(nameof(SelectedAppsTitle));
        _ = SaveSelectionAsync();
        ApplyFilter();
    }

    partial void OnUseLocalhostOverrideChanged(bool value)
    {
        if (value)
        {
            ProxyHost = "127.0.0.1";
        }

        OnPropertyChanged(nameof(IsHostEditable));
        if (!_isInitializing)
            _ = SaveSelectionWithDebounceAsync();
    }

    partial void OnUseDirectorySourceChanged(bool value)
    {
        OnPropertyChanged(nameof(UseAppListSource));
        OnPropertyChanged(nameof(UseDirectorySearchSource));
        OnPropertyChanged(nameof(AppListSourceVisibility));
        OnPropertyChanged(nameof(DirectorySourceVisibility));
    }

    partial void OnDirectoryPathChanged(string value)
    {
        RequestCancellation(_directoryScanCts);
        _hasScannedDirectory = false;
        _directoryScanFailed = false;
        DirectoryProcesses.Clear();
        NotifyDirectoryResultsChanged();
        OnPropertyChanged(nameof(CanScanDirectory));
        ScanDirectoryCommand.NotifyCanExecuteChanged();
    }

    partial void OnScanSubdirectoriesChanged(bool value)
    {
        if (_hasScannedDirectory && CanEditConfiguration && !string.IsNullOrWhiteSpace(DirectoryPath))
            _ = ScanDirectoryAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void LiteModeManager_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiteModeManager.CurrentState))
        {
            OnPropertyChanged(nameof(CurrentState));
            OnPropertyChanged(nameof(RunStateLabel));
            OnPropertyChanged(nameof(CanStartStop));
            OnPropertyChanged(nameof(ControlButtonText));
            OnPropertyChanged(nameof(CanEditConfiguration));
            OnPropertyChanged(nameof(IsHostEditable));
            OnPropertyChanged(nameof(CanScanDirectory));
            StartStopCommand.NotifyCanExecuteChanged();
            ScanDirectoryCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(LiteModeManager.StatusText))
        {
            OnPropertyChanged(nameof(StatusText));
        }
        else if (e.PropertyName == nameof(LiteModeManager.CanStartStop))
        {
            OnPropertyChanged(nameof(CanStartStop));
            StartStopCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(LiteModeManager.ControlButtonText))
        {
            OnPropertyChanged(nameof(ControlButtonText));
        }
        else if (e.PropertyName == nameof(LiteModeManager.CurrentProcessNames))
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void ApplyFilter()
    {
        FilteredApps.Clear();
        var query = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allApps
            : _allApps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var app in filtered
                     .OrderByDescending(a => a.IsSelected)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            FilteredApps.Add(app);
    }

    public async Task OnAppSelectionChangedAsync(InstalledApp app)
    {
        if (!CanEditConfiguration)
        {
            // The row toggles its model before raising Click. Restore that state
            // when configuration is locked so UI and persisted rules cannot diverge.
            app.IsSelected = !app.IsSelected;
            ApplicationSelectionStatusText = IsApplicationScanning
                ? "请等待当前应用扫描完成"
                : "请先停止代理，再修改应用规则";
            _liteModeManager.StatusText = ApplicationSelectionStatusText;
            ApplyFilter();
            return;
        }

        var sourceKey = CreateAppSourceKey(app.InstallPath);
        if (app.IsSelected)
        {
            await TrackBackgroundOperationAsync(ScanSelectedApplicationAsync(app, sourceKey));
            return;
        }

        _explicitlyRemovedSourceKeys.Add(sourceKey);
        var group = ProcessGroups.FirstOrDefault(candidate =>
            candidate.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
        if (group != null)
        {
            ProcessGroups.Remove(group);
            OnPropertyChanged(nameof(SelectedAppsTitle));
        }

        ApplicationSelectionStatusText = $"已移除 {app.Name}";
        _liteModeManager.StatusText = ApplicationSelectionStatusText;
        await SaveSelectionAsync();
        ApplyFilter();
    }

    private async Task ScanSelectedApplicationAsync(InstalledApp app, string sourceKey)
    {
        var previousCts = _applicationScanCts;
        var cts = new CancellationTokenSource();
        _applicationScanCts = cts;
        RequestCancellation(previousCts);
        RequestCancellation(_saveCts);

        IsApplicationScanning = true;
        ApplicationSelectionStatusText = $"正在扫描 {app.Name}…";
        _liteModeManager.StatusText = ApplicationSelectionStatusText;

        try
        {
            var entries = await EnumerateProcessEntriesSnapshotAsync(
                app.InstallPath,
                recursive: true,
                selectedPaths: null,
                cts.Token);
            if (!IsCurrentApplicationScan(cts))
                return;

            if (!app.IsSelected || !CanCommitConfiguration)
            {
                RollBackApplicationSelection(app, sourceKey, $"已取消添加 {app.Name}");
                await SaveSelectionAsync();
                return;
            }

            var existingGroup = ProcessGroups.FirstOrDefault(candidate =>
                candidate.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
            if (existingGroup != null)
                ProcessGroups.Remove(existingGroup);

            if (entries.Count == 0)
            {
                RollBackApplicationSelection(app, sourceKey, $"{app.Name} 的目录中没有可用的 exe");
                await SaveSelectionAsync();
                return;
            }

            var group = new ProcessGroup
            {
                GroupName = app.Name,
                SourceKey = sourceKey
            };
            group.Processes.AddRange(entries);
            ProcessGroups.Add(group);
            _explicitlyRemovedSourceKeys.Remove(sourceKey);
            OnPropertyChanged(nameof(SelectedAppsTitle));
            ApplicationSelectionStatusText = $"已添加 {app.Name} · {entries.Count} 个进程";
            _liteModeManager.StatusText = ApplicationSelectionStatusText;
            await SaveSelectionAsync();
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentApplicationScan(cts))
            {
                RollBackApplicationSelection(app, sourceKey, $"已取消添加 {app.Name}");
                await SaveSelectionAsync();
            }
        }
        catch (Exception e)
        {
            if (IsCurrentApplicationScan(cts))
            {
                Serilog.Log.Error(e, "Failed to scan application directory {ApplicationPath}", app.InstallPath);
                RollBackApplicationSelection(app, sourceKey, $"扫描 {app.Name} 失败");
                await SaveSelectionAsync();
            }
        }
        finally
        {
            if (ReferenceEquals(cts, _applicationScanCts))
            {
                _applicationScanCts = null;
                IsApplicationScanning = false;
                ApplyFilter();
            }

            cts.Dispose();
        }
    }

    private bool IsCurrentApplicationScan(CancellationTokenSource cts)
    {
        return !cts.IsCancellationRequested &&
               ReferenceEquals(cts, _applicationScanCts);
    }

    private void RollBackApplicationSelection(InstalledApp app, string sourceKey, string status)
    {
        app.IsSelected = false;
        _explicitlyRemovedSourceKeys.Add(sourceKey);
        var existingGroup = ProcessGroups.FirstOrDefault(candidate =>
            candidate.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
        if (existingGroup != null)
            ProcessGroups.Remove(existingGroup);

        OnPropertyChanged(nameof(SelectedAppsTitle));
        ApplicationSelectionStatusText = status;
        _liteModeManager.StatusText = status;
    }

    public void OnDirectoryProcessSelectionChanged(ProcessEntry process)
    {
        if (!CanEditConfiguration)
        {
            _liteModeManager.StatusText = "请先停止代理，再修改进程规则";
            return;
        }

        var sourceKey = CreateDirectorySourceKey(DirectoryPath);
        if (process.IsSelected)
        {
            _explicitlyRemovedSourceKeys.Remove(sourceKey);
            AddDirectoryProcess(process);
        }
        else
        {
            RemoveDirectoryProcess(process.FullPath);
            if (ProcessGroups.All(group => !group.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)))
                _explicitlyRemovedSourceKeys.Add(sourceKey);
        }

        OnPropertyChanged(nameof(SelectedAppsTitle));
        _ = SaveSelectionAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private async Task StartStopAsync()
    {
        if (CurrentState is State.Waiting or State.Stopped)
            await StartAsync();
        else if (CurrentState == State.Started)
            await StopAsync();
    }

    private Task<bool> StartAsync()
    {
        var operation = StartCoreAsync();
        SetActiveOperation(operation);
        return AwaitTrackedOperationAsync(operation);
    }

    private async Task<bool> StartCoreAsync()
    {
        try
        {
            await SaveSelectionAsync(throwOnFailure: true);
        }
        catch
        {
            return false;
        }

        if (_isShuttingDown)
            return false;

        var processes = ProcessGroups.SelectMany(g => g.Processes).ToList();
        var started = await _liteModeManager.StartAsync(ProxyHost, ProxyPort, processes);
        if (started && _appContext.Settings.MinimizeWhenStarted)
            App.MainWindow.MinimizeToTray();

        return started;
    }

    private Task StopAsync()
    {
        var operation = _liteModeManager.StopAsync();
        SetActiveOperation(operation);
        return AwaitTrackedOperationAsync(operation);
    }

    private void SetActiveOperation(Task operation)
    {
        lock (_operationSync)
            _activeOperation = operation;
    }

    private Task TrackBackgroundOperationAsync(Task operation)
    {
        lock (_operationSync)
            _backgroundOperations.Add(operation);

        return AwaitBackgroundOperationAsync(operation);
    }

    private async Task AwaitBackgroundOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            lock (_operationSync)
                _backgroundOperations.Remove(operation);
        }
    }

#pragma warning disable VSTHRD003
    private async Task AwaitTrackedOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            ClearActiveOperation(operation);
        }
    }

    private async Task<T> AwaitTrackedOperationAsync<T>(Task<T> operation)
    {
        try
        {
            return await operation;
        }
        finally
        {
            ClearActiveOperation(operation);
        }
    }
#pragma warning restore VSTHRD003

    private void ClearActiveOperation(Task operation)
    {
        lock (_operationSync)
        {
            if (ReferenceEquals(_activeOperation, operation))
                _activeOperation = Task.CompletedTask;
        }
    }

    public async Task<bool> StartIfConfiguredAsync()
    {
        await InitializeAsync();
        if (!_homeInitializationSucceeded || !_appContext.Settings.StartWhenOpened)
            return false;

        return await StartAsync();
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null)
            return;

        DirectoryPath = folder.Path;
    }

    [RelayCommand(CanExecute = nameof(CanScanDirectory))]
    private Task ScanDirectoryAsync()
    {
        return TrackBackgroundOperationAsync(ScanDirectoryCoreAsync());
    }

    private async Task ScanDirectoryCoreAsync()
    {
        var previousCts = _directoryScanCts;
        var cts = new CancellationTokenSource();
        _directoryScanCts = cts;
        IsDirectoryScanning = true;
        RequestCancellation(previousCts);

        var selectedPaths = ProcessGroups
            .SelectMany(g => g.Processes)
            .Select(p => p.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _hasScannedDirectory = true;
        _directoryScanFailed = false;

        try
        {
            if (!Directory.Exists(DirectoryPath))
                throw new DirectoryNotFoundException($"目录不存在: {DirectoryPath}");

            var scanPath = DirectoryPath;
            var recursive = ScanSubdirectories;
            _liteModeManager.StatusText = $"正在扫描: {scanPath}";
            var entries = await EnumerateProcessEntriesSnapshotAsync(
                scanPath,
                recursive,
                selectedPaths,
                cts.Token);
            if (!IsCurrentDirectoryScan(cts))
                return;

            DirectoryProcesses.ReplaceAll(entries);

            _liteModeManager.StatusText = DirectoryProcesses.Count == 0
                ? $"未找到可执行文件: {scanPath}"
                : $"找到 {DirectoryProcesses.Count} 个可执行文件";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (IsSkippableFileSystemException(e))
        {
            if (!IsCurrentDirectoryScan(cts))
                return;

            DirectoryProcesses.Clear();
            _directoryScanFailed = true;
            _liteModeManager.StatusText = $"目录扫描失败: {e.Message}";
        }
        finally
        {
            if (ReferenceEquals(cts, _directoryScanCts))
            {
                _directoryScanCts = null;
                IsDirectoryScanning = false;
                NotifyDirectoryResultsChanged();
            }

            cts.Dispose();
        }
    }

    private bool IsCurrentDirectoryScan(CancellationTokenSource cts)
    {
        return !cts.IsCancellationRequested &&
               ReferenceEquals(cts, _directoryScanCts);
    }

    private void NotifyDirectoryResultsChanged()
    {
        OnPropertyChanged(nameof(DirectoryEmptyStateVisibility));
        OnPropertyChanged(nameof(DirectoryProcessListVisibility));
        OnPropertyChanged(nameof(DirectoryEmptyTitle));
        OnPropertyChanged(nameof(DirectoryEmptyDescription));
    }

    [RelayCommand]
    private async Task AddToGroupAsync(ProcessGroup group)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null)
            return;

        IReadOnlyList<ProcessEntry> entries;
        try
        {
            entries = await EnumerateProcessEntriesSnapshotAsync(
                folder.Path,
                recursive: true,
                selectedPaths: null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            _liteModeManager.StatusText = $"无法读取目录: {e.Message}";
            return;
        }

        group.Processes.AddRange(entries.Where(entry => group.Processes.All(process =>
            !process.FullPath.Equals(entry.FullPath, StringComparison.OrdinalIgnoreCase))));
        _explicitlyRemovedSourceKeys.Remove(group.SourceKey);
        await SaveSelectionAsync();
    }

    [RelayCommand]
    private async Task AddExeToGroupAsync(ProcessGroup group)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");

        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        var path = file.Path;
        if (group.Processes.All(p => !p.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            group.Processes.Add(new ProcessEntry { FullPath = path });

        _explicitlyRemovedSourceKeys.Remove(group.SourceKey);
        await SaveSelectionAsync();
    }

    [RelayCommand]
    private void RemoveGroup(ProcessGroup group)
    {
        _explicitlyRemovedSourceKeys.Add(group.SourceKey);
        ProcessGroups.Remove(group);
        OnPropertyChanged(nameof(SelectedAppsTitle));
        var app = group.SourceKey.StartsWith(AppSourcePrefix, StringComparison.OrdinalIgnoreCase)
            ? _allApps.FirstOrDefault(candidate =>
                CreateAppSourceKey(candidate.InstallPath).Equals(
                    group.SourceKey,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        if (app != null)
            app.IsSelected = false;

        foreach (var process in DirectoryProcesses)
        {
            if (group.Processes.Any(p => p.FullPath.Equals(process.FullPath, StringComparison.OrdinalIgnoreCase)))
                process.IsSelected = false;
        }

        _ = SaveSelectionAsync();
        ApplyFilter();
    }

    private void AddDirectoryProcess(ProcessEntry process)
    {
        var sourceKey = CreateDirectorySourceKey(DirectoryPath);
        var groupName = GetDirectoryGroupName(DirectoryPath);
        var group = ProcessGroups.FirstOrDefault(g => g.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase));
        if (group == null)
        {
            group = new ProcessGroup { GroupName = groupName, SourceKey = sourceKey };
            ProcessGroups.Add(group);
        }

        if (group.Processes.All(p => !p.FullPath.Equals(process.FullPath, StringComparison.OrdinalIgnoreCase)))
            group.Processes.Add(new ProcessEntry { FullPath = process.FullPath });
    }

    private void RemoveDirectoryProcess(string fullPath)
    {
        foreach (var group in ProcessGroups.ToArray())
        {
            var existing = group.Processes.FirstOrDefault(p => p.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                continue;

            group.Processes.Remove(existing);
            if (group.Processes.Count == 0)
                ProcessGroups.Remove(group);

            break;
        }
    }

    private static string GetDirectoryGroupName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "目录查找" : $"目录: {name}";
    }

    private static IEnumerable<string> EnumerateExesSafe(
        string root,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSafeDirectoryAttributes(root, out var rootAttributes) ||
            rootAttributes.HasFlag(FileAttributes.ReparsePoint))
            yield break;

        var dirs = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dirs.Enqueue(root);

        while (dirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = dirs.Dequeue();
            var normalizedDirectory = NormalizePath(dir);
            if (!visited.Add(normalizedDirectory) ||
                !TryGetSafeDirectoryAttributes(dir, out var attributes) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            foreach (var file in MaterializeDirectoryEntriesSafely(
                         () => Directory.EnumerateFiles(dir, "*.exe"),
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return file;
            }

            if (!recursive)
                continue;

            foreach (var subDirectory in MaterializeDirectoryEntriesSafely(
                         () => Directory.EnumerateDirectories(dir),
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGetSafeDirectoryAttributes(subDirectory, out var subDirectoryAttributes) &&
                    !subDirectoryAttributes.HasFlag(FileAttributes.ReparsePoint))
                    dirs.Enqueue(subDirectory);
            }
        }
    }

    private static List<string> MaterializeDirectoryEntriesSafely(
        Func<IEnumerable<string>> entryFactory,
        CancellationToken cancellationToken)
    {
        var entries = new List<string>();
        try
        {
            using var enumerator = entryFactory().GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext())
                    break;

                entries.Add(enumerator.Current);
            }
        }
        catch (Exception e) when (IsSkippableFileSystemException(e))
        {
            // A directory can disappear or become inaccessible between discovery
            // and MoveNext. Keep the entries already read and continue elsewhere.
        }

        return entries;
    }

    private static bool TryGetSafeDirectoryAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory);
        }
        catch (Exception e) when (IsSkippableFileSystemException(e))
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsSkippableFileSystemException(Exception exception)
    {
        return exception is UnauthorizedAccessException or
            DirectoryNotFoundException or
            IOException or
            SecurityException;
    }

    private async Task<bool> SaveSelectionAsync(bool throwOnFailure = false)
    {
        var settings = _appContext.Settings;
        settings.LiteProxyHost = ProxyHost;
        settings.LiteProxyPort = ProxyPort;
        settings.UseLocalhostOverride = UseLocalhostOverride;

        // Discovery can be partial when a registry hive, package directory, or
        // drive is temporarily unavailable. Only replace paths for applications
        // that were actually discovered; keep unresolved saved rules intact.
        var discoveredPaths = _allApps
            .Select(app => app.InstallPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.SelectedAppPaths = settings.SelectedAppPaths
            .Where(path => !discoveredPaths.Contains(path))
            .Concat(_allApps.Where(app => app.IsSelected).Select(app => app.InstallPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var persistedGroups = new Dictionary<string, List<string>>(
            settings.LiteProcessGroups,
            StringComparer.OrdinalIgnoreCase);
        foreach (var sourceKey in _explicitlyRemovedSourceKeys)
            persistedGroups.Remove(sourceKey);

        var discoveredAppSourceKeys = _allApps
            .Select(app => CreateAppSourceKey(app.InstallPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceKey in discoveredAppSourceKeys)
        {
            if (ProcessGroups.All(group => !group.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)))
                persistedGroups.Remove(sourceKey);
        }

        foreach (var group in ProcessGroups.Where(group => !string.IsNullOrWhiteSpace(group.SourceKey)))
        {
            var selectedPaths = group.Processes
                .Where(process => process.IsSelected && File.Exists(process.FullPath))
                .Select(process => process.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (persistedGroups.TryGetValue(group.SourceKey, out var previouslySavedPaths))
            {
                selectedPaths = selectedPaths
                    .Concat(previouslySavedPaths.Where(path => !File.Exists(path)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (selectedPaths.Count == 0)
                persistedGroups.Remove(group.SourceKey);
            else
                persistedGroups[group.SourceKey] = selectedPaths;
        }

        settings.LiteProcessGroups = persistedGroups;

        try
        {
            await _configuration.SaveOrThrowAsync();
            return true;
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Failed to save NPP proxy configuration");
            var status = $"保存设置失败：{exception.GetBaseException().Message}";
            ApplicationSelectionStatusText = status;
            _liteModeManager.StatusText = status;
            if (throwOnFailure)
                throw;

            return false;
        }
    }

    private async Task SaveSelectionWithDebounceAsync()
    {
        RequestCancellation(_saveCts);
        var cts = new CancellationTokenSource();
        _saveCts = cts;

        try
        {
            await Task.Delay(500, cts.Token);
            await SaveSelectionAsync();
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(cts, _saveCts))
                _saveCts = null;

            cts.Dispose();
        }
    }

    private static Task<List<ProcessEntry>> EnumerateProcessEntriesSnapshotAsync(
        string root,
        bool recursive,
        IReadOnlySet<string>? selectedPaths,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in EnumerateExesSafe(root, recursive, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(path);
            }

            return paths
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(path => new ProcessEntry
                {
                    FullPath = path,
                    IsSelected = selectedPaths?.Contains(path) ?? true
                })
                .ToList();
        }, cancellationToken);
    }

    private static void RequestCancellation(CancellationTokenSource? source)
    {
        if (source == null || source.IsCancellationRequested)
            return;

#pragma warning disable VSTHRD103
        source.Cancel();
#pragma warning restore VSTHRD103
    }

    private static string CreateAppSourceKey(string installPath) => AppSourcePrefix + NormalizePath(installPath);

    private static string CreateDirectorySourceKey(string directoryPath) => DirectorySourcePrefix + NormalizePath(directoryPath);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static List<string>? FindSavedPaths(Dictionary<string, List<string>> groups, string sourceKey)
    {
        return groups.FirstOrDefault(pair => pair.Key.Equals(sourceKey, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static void RestorePathsInto(IEnumerable<string> paths, ProcessGroup group)
    {
        foreach (var path in paths
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            group.Processes.Add(new ProcessEntry { FullPath = path });
        }
    }

    private sealed record HomeInitializationSnapshot(
        HashSet<string> SavedPaths,
        Dictionary<string, List<string>> SavedGroups);

    private sealed record HomeInitializationResult(
        List<InstalledApp> Apps,
        List<ProcessGroup> ProcessGroups);

    public sealed record AdvancedProxySettings(
        bool EnableUdp,
        bool IncludeChildProcesses,
        bool BypassLan,
        string DnsHost);
}
