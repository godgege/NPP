using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netch.App;
using Netch.App.Services;
using Netch.Utils;

namespace Netch.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly NetchAppContext _appContext;
    private readonly Configuration _configuration;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _isLoading;
    private bool _isRollingBack;
    private bool _lastPersistedRunAtStartup;
    private int _saveVersion;

    public VelopackUpdateService UpdateService { get; }

    public SettingsViewModel(
        NetchAppContext appContext,
        Configuration configuration,
        StartupRegistrationService startupRegistration,
        VelopackUpdateService updateService)
    {
        _appContext = appContext;
        _configuration = configuration;
        _startupRegistration = startupRegistration;
        UpdateService = updateService;
        _isLoading = true;
        try
        {
            LoadSettings();
        }
        finally
        {
            _isLoading = false;
        }

        _lastPersistedRunAtStartup = _appContext.Settings.RunAtStartup;
    }

    // Startup behavior
    [ObservableProperty] private bool _exitWhenClosed;
    [ObservableProperty] private bool _stopWhenExited;
    [ObservableProperty] private bool _startWhenOpened;
    [ObservableProperty] private bool _minimizeWhenStarted;
    [ObservableProperty] private bool _runAtStartup;
    [ObservableProperty] private bool _checkUpdateWhenOpened;
    [ObservableProperty] private bool _checkBetaUpdate;

    private void LoadSettings()
    {
        var s = _appContext.Settings;
        ExitWhenClosed = s.ExitWhenClosed;
        StopWhenExited = s.StopWhenExited;
        StartWhenOpened = s.StartWhenOpened;
        MinimizeWhenStarted = s.MinimizeWhenStarted;
        RunAtStartup = s.RunAtStartup;
        CheckUpdateWhenOpened = s.CheckUpdateWhenOpened;
        CheckBetaUpdate = s.CheckBetaUpdate;
    }

    partial void OnExitWhenClosedChanged(bool value) => ApplySetting(() =>
        _appContext.Settings.ExitWhenClosed = value);

    partial void OnStopWhenExitedChanged(bool value) => ApplySetting(() =>
        _appContext.Settings.StopWhenExited = value);

    partial void OnStartWhenOpenedChanged(bool value) => ApplySetting(() =>
        _appContext.Settings.StartWhenOpened = value);

    partial void OnMinimizeWhenStartedChanged(bool value) => ApplySetting(() =>
        _appContext.Settings.MinimizeWhenStarted = value);

    partial void OnCheckUpdateWhenOpenedChanged(bool value) => ApplySetting(() =>
        _appContext.Settings.CheckUpdateWhenOpened = value);

    partial void OnCheckBetaUpdateChanged(bool value) => ApplySetting(() =>
        _appContext.Settings.CheckBetaUpdate = value);

    partial void OnRunAtStartupChanged(bool value)
    {
        if (_isLoading || _isRollingBack)
            return;

        var previousValue = _appContext.Settings.RunAtStartup;
        try
        {
            _startupRegistration.Apply(value);
            _appContext.Settings.RunAtStartup = value;
            QueuePersist();
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to update the Windows startup setting");
            try
            {
                _startupRegistration.Apply(previousValue);
            }
            catch (Exception rollbackException)
            {
                Serilog.Log.Error(rollbackException, "Failed to restore the Windows startup setting");
            }

            _isRollingBack = true;
            RunAtStartup = previousValue;
            _isRollingBack = false;
            App.MainWindow.ShowNotification($"开机自启动设置失败：{e.Message}", isInfo: false);
        }
    }

    [RelayCommand]
    private async Task RunUpdateActionAsync()
    {
        switch (UpdateService.Stage)
        {
            case AppUpdateStage.Available:
                await UpdateService.DownloadUpdateAsync();
                break;
            case AppUpdateStage.Failed when UpdateService.CanRetryDownload:
                await UpdateService.DownloadUpdateAsync();
                break;
            case AppUpdateStage.ReadyToRestart:
                await App.MainWindow.ApplyUpdateAndRestartAsync();
                break;
            case AppUpdateStage.Checking:
            case AppUpdateStage.Downloading:
            case AppUpdateStage.PreparingRestart:
                break;
            default:
                await UpdateService.CheckForUpdatesAsync(CheckBetaUpdate);
                break;
        }
    }

    [RelayCommand]
    private void CancelUpdate() => UpdateService.CancelDownload();

    private void ApplySetting(Action apply)
    {
        if (_isLoading || _isRollingBack)
            return;

        apply();
        QueuePersist();
    }

    private void QueuePersist()
    {
        var version = Interlocked.Increment(ref _saveVersion);
        _ = PersistLatestAsync(version);
    }

    private async Task PersistLatestAsync(int version)
    {
        // Coalesce a quick sequence of clicks while keeping the in-memory
        // setting effective synchronously on every toggle.
        await Task.Delay(120);
        if (version != Volatile.Read(ref _saveVersion))
            return;

        await _saveGate.WaitAsync();
        try
        {
            if (version == Volatile.Read(ref _saveVersion))
            {
                await _configuration.SaveOrThrowAsync();
                _lastPersistedRunAtStartup = _appContext.Settings.RunAtStartup;
            }
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to automatically save settings");
            if (version == Volatile.Read(ref _saveVersion))
            {
                var persistedValue = _lastPersistedRunAtStartup;
                try
                {
                    _startupRegistration.Apply(persistedValue);
                }
                catch (Exception rollbackException)
                {
                    Serilog.Log.Error(rollbackException, "Failed to restore startup registration after a save failure");
                }

                _isRollingBack = true;
                RunAtStartup = persistedValue;
                _appContext.Settings.RunAtStartup = persistedValue;
                _isRollingBack = false;
                App.MainWindow.ShowNotification($"设置自动保存失败：{e.Message}", isInfo: false);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
