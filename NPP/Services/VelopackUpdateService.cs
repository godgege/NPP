using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace Netch.App.Services;

public enum AppUpdateStage
{
    Unsupported,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
    PreparingRestart,
}

/// <summary>
/// Owns the complete Velopack update session for the lifetime of the application.
/// The UpdateManager and UpdateInfo discovered by a check are deliberately retained
/// so that a later download uses the same stable/prerelease source snapshot.
/// </summary>
public sealed class VelopackUpdateService : ObservableObject, IDisposable
{
    private const string RepositoryUrl = "https://github.com/godgege/netch";

    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _fallbackVersion;

    private UpdateManager? _updateManager;
    private UpdateInfo? _updateInfo;
    private VelopackAsset? _readyAsset;
    private CancellationTokenSource? _downloadCancellation;
    private AppUpdateStage _stage;
    private double _downloadProgress;
    private string? _statusOverride;
    private bool _failedWhileDownloading;
    private bool _disposed;

    public VelopackUpdateService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _fallbackVersion = ReadEntryAssemblyVersion();

        try
        {
            var manager = CreateManager(includePrerelease: false);
            _updateManager = manager;
            Log.Information(
                "Velopack update service initialized: installed={IsInstalled}, portable={IsPortable}, version={CurrentVersion}",
                manager.IsInstalled,
                manager.IsPortable,
                manager.CurrentVersion?.ToString() ?? _fallbackVersion);

            if (!manager.IsInstalled)
            {
                _stage = AppUpdateStage.Unsupported;
                return;
            }

            var pendingUpdate = manager.UpdatePendingRestart;
            if (pendingUpdate is not null)
            {
                _readyAsset = pendingUpdate;
                _stage = AppUpdateStage.ReadyToRestart;
                return;
            }

            _stage = AppUpdateStage.Idle;
        }
        catch (NotInstalledException)
        {
            _stage = AppUpdateStage.Unsupported;
        }
        catch (Exception exception)
        {
            // Constructing the service must never prevent the application from starting.
            Log.Warning(exception, "Failed to initialize the Velopack update service");
            _statusOverride = "更新服务暂时不可用";
            _stage = AppUpdateStage.Failed;
        }
    }

    public AppUpdateStage Stage
    {
        get => _stage;
        private set
        {
            if (SetProperty(ref _stage, value))
            {
                RaiseDerivedPropertiesChanged();
            }
        }
    }

    public string CurrentVersionText
    {
        get
        {
            var version = _updateManager?.CurrentVersion?.ToString() ?? _fallbackVersion;
            return $"当前版本 {version}";
        }
    }

    public string StatusText => _statusOverride ?? Stage switch
    {
        AppUpdateStage.Unsupported => "当前运行目录未由 Velopack 管理，无法自动更新",
        AppUpdateStage.Idle => "可随时检查新版本",
        AppUpdateStage.Checking => "正在检查更新…",
        AppUpdateStage.UpToDate => "当前已是最新版本",
        AppUpdateStage.Available => $"发现新版本 {TargetVersionText}",
        AppUpdateStage.Downloading => $"正在下载 {TargetVersionText}…",
        AppUpdateStage.ReadyToRestart => $"版本 {TargetVersionText} 已就绪，重启后完成更新",
        AppUpdateStage.Failed => "更新失败，请稍后重试",
        AppUpdateStage.PreparingRestart => "正在准备重启更新…",
        _ => string.Empty,
    };

    public string ActionText => Stage switch
    {
        AppUpdateStage.Unsupported => "不可用",
        AppUpdateStage.Idle => "检查更新",
        AppUpdateStage.Checking => "检查中…",
        AppUpdateStage.UpToDate => "重新检查",
        AppUpdateStage.Available => "下载更新",
        AppUpdateStage.Downloading => "下载中…",
        AppUpdateStage.ReadyToRestart => "重启更新",
        AppUpdateStage.Failed when _failedWhileDownloading => "重新下载",
        AppUpdateStage.Failed => "重试",
        AppUpdateStage.PreparingRestart => "准备重启…",
        _ => "检查更新",
    };

    public bool IsActionEnabled => Stage is
        AppUpdateStage.Idle or
        AppUpdateStage.UpToDate or
        AppUpdateStage.Available or
        AppUpdateStage.ReadyToRestart or
        AppUpdateStage.Failed;

    public bool IsActionVisible => Stage != AppUpdateStage.Downloading;

    public Visibility ActionVisibility => IsActionVisible ? Visibility.Visible : Visibility.Collapsed;

    public bool IsProgressVisible => Stage == AppUpdateStage.Downloading;

    public Visibility ProgressVisibility => IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisibility => Stage == AppUpdateStage.Downloading
        ? Visibility.Visible
        : Visibility.Collapsed;

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, Math.Clamp(value, 0, 100));
    }

    public bool IsBusy => Stage is
        AppUpdateStage.Checking or
        AppUpdateStage.Downloading or
        AppUpdateStage.PreparingRestart;

    public bool CanRetryDownload => Stage == AppUpdateStage.Failed && _failedWhileDownloading &&
                                    _updateManager is not null && _updateInfo is not null;

    public async Task<bool> CheckForUpdatesAsync(bool includePrerelease, bool isAutomatic = false)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync().ConfigureAwait(true);

        try
        {
            SetUiState(AppUpdateStage.Checking);

            var manager = CreateManager(includePrerelease);
            if (!manager.IsInstalled)
            {
                _updateManager = manager;
                _updateInfo = null;
                _readyAsset = null;
                SetUiState(AppUpdateStage.Unsupported);
                return false;
            }

            _updateManager = manager;
            OnPropertyChanged(nameof(CurrentVersionText));

            var pendingUpdate = manager.UpdatePendingRestart;
            if (pendingUpdate is not null)
            {
                _updateInfo = null;
                _readyAsset = pendingUpdate;
                SetUiState(AppUpdateStage.ReadyToRestart);
                return true;
            }

            var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
            _updateInfo = updateInfo;
            _readyAsset = null;

            if (updateInfo is null)
            {
                SetUiState(AppUpdateStage.UpToDate);
                return false;
            }

            SetUiState(AppUpdateStage.Available);
            return true;
        }
        catch (NotInstalledException)
        {
            _updateInfo = null;
            _readyAsset = null;
            SetUiState(AppUpdateStage.Unsupported);
            return false;
        }
        catch (Exception exception)
        {
            _updateInfo = null;
            _readyAsset = null;

            if (isAutomatic)
            {
                Log.Warning(exception, "The automatic Velopack update check failed");
                SetUiState(AppUpdateStage.Idle);
            }
            else
            {
                Log.Error(exception, "The Velopack update check failed");
                SetUiState(AppUpdateStage.Failed, "检查更新失败，请稍后重试");
            }

            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync().ConfigureAwait(true);

        try
        {
            if (_updateManager is null || _updateInfo is null ||
                Stage is not (AppUpdateStage.Available or AppUpdateStage.Failed))
            {
                throw new InvalidOperationException("No checked update is available to download.");
            }

            _downloadCancellation?.Dispose();
            _downloadCancellation = new CancellationTokenSource();

            SetDownloadProgress(0);
            SetUiState(AppUpdateStage.Downloading);

            await _updateManager.DownloadUpdatesAsync(
                _updateInfo,
                SetDownloadProgress,
                _downloadCancellation.Token).ConfigureAwait(true);

            _readyAsset = _updateManager.UpdatePendingRestart ?? _updateInfo.TargetFullRelease;
            SetDownloadProgress(100);
            SetUiState(AppUpdateStage.ReadyToRestart);
        }
        catch (OperationCanceledException)
        {
            Log.Information("The Velopack update download was cancelled");
            SetDownloadProgress(0);
            SetUiState(AppUpdateStage.Available, "下载已取消，可稍后继续");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "The Velopack update download failed");
            SetUiState(AppUpdateStage.Failed, "下载更新失败，请稍后重试", failedWhileDownloading: true);
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            _operationGate.Release();
        }
    }

    public void CancelDownload()
    {
        if (Stage == AppUpdateStage.Downloading)
        {
            _downloadCancellation?.Cancel();
        }
    }

    public void BeginPreparingRestart()
    {
        ThrowIfDisposed();

        if (Stage != AppUpdateStage.ReadyToRestart || _updateManager is null || _readyAsset is null)
        {
            throw new InvalidOperationException("The update is not ready to restart.");
        }

        SetUiState(AppUpdateStage.PreparingRestart);
    }

    public void RestoreReadyToRestart(string statusText)
    {
        ThrowIfDisposed();

        if (_updateManager is null || _readyAsset is null)
        {
            SetUiState(AppUpdateStage.Failed, statusText);
            return;
        }

        SetUiState(AppUpdateStage.ReadyToRestart, statusText);
    }

    public void ApplyUpdateAndRestart()
    {
        ThrowIfDisposed();

        if (Stage is not (AppUpdateStage.ReadyToRestart or AppUpdateStage.PreparingRestart) ||
            _updateManager is null || _readyAsset is null)
        {
            throw new InvalidOperationException("The update is not ready to apply.");
        }

        try
        {
            // Velopack exits the process immediately. The caller must persist state and
            // stop proxy resources before entering this method.
            _updateManager.ApplyUpdatesAndRestart(_readyAsset, Array.Empty<string>());
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to apply the Velopack update");
            SetUiState(AppUpdateStage.ReadyToRestart, "应用更新失败，未退出程序，可重试");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _downloadCancellation?.Cancel();
        _downloadCancellation?.Dispose();
        _operationGate.Dispose();
    }

    private string TargetVersionText =>
        (_readyAsset ?? _updateInfo?.TargetFullRelease)?.Version?.ToString() ?? "新版本";

    private static UpdateManager CreateManager(bool includePrerelease)
    {
        var source = new GithubSource(RepositoryUrl, accessToken: null, includePrerelease);
        return new UpdateManager(source);
    }

    private void SetUiState(
        AppUpdateStage stage,
        string? statusOverride = null,
        bool failedWhileDownloading = false)
    {
        void Apply()
        {
            _statusOverride = statusOverride;
            _failedWhileDownloading = failedWhileDownloading;

            if (Stage == stage)
            {
                RaiseDerivedPropertiesChanged();
            }
            else
            {
                Stage = stage;
            }
        }

        RunOnDispatcher(Apply);
    }

    private void SetDownloadProgress(int progress)
    {
        RunOnDispatcher(() => DownloadProgress = progress);
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => action()))
        {
            Log.Debug("The UI dispatcher was unavailable while updating Velopack state");
        }
    }

    private void RaiseDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsActionEnabled));
        OnPropertyChanged(nameof(IsActionVisible));
        OnPropertyChanged(nameof(ActionVisibility));
        OnPropertyChanged(nameof(IsProgressVisible));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(CancelVisibility));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanRetryDownload));
    }

    private static string ReadEntryAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informationalVersion = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly?.GetName().Version?.ToString(3) ?? "未知";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
