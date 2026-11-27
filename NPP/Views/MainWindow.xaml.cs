using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Netch;
using Netch.App.Helpers;
using Netch.App.Services;
using Netch.App.ViewModels;
using Netch.Enums;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Netch.App.Views;

public sealed partial class MainWindow : Window
{
    private const string AppDisplayName = "NPP";
    private static readonly string AppIconPath = Path.Combine(
        System.AppContext.BaseDirectory,
        "Assets",
        "NPP.ico");
    private const int ShowWindowCommandHide = 0;
    private const int ShowWindowCommandShow = 5;

    private TaskbarIcon? _trayIcon;
    private System.Drawing.Icon? _trayRunningIconTemplate;
    private System.Drawing.Icon? _trayStoppedIconTemplate;
    private bool? _trayShowsRunning;
    private readonly LiteModeManager _liteModeManager;
    private readonly MainViewModel _mainViewModel;
    private readonly VelopackUpdateService _updateService;
    private readonly EmbeddedV2rayNService _embeddedClient;
    private readonly ProxyConnectivityService _connectivity;
    private readonly Task _homeInitializationTask;
    private readonly HashSet<Type> _loadedPageTypes = [];
    private bool _forceExit;
    private bool _exitInProgress;
    private bool _mainViewModelDisposed;
    private InputNonClientPointerSource? _nonClientInputSource;
    private int _navigationVersion;

    public MainWindow()
    {
        InitializeComponent();
        ApplyApplicationIcon();
        _liteModeManager = App.Services.GetRequiredService<LiteModeManager>();
        _mainViewModel = App.Services.GetRequiredService<MainViewModel>();
        _updateService = App.Services.GetRequiredService<VelopackUpdateService>();
        _embeddedClient = App.Services.GetRequiredService<EmbeddedV2rayNService>();
        _connectivity = App.Services.GetRequiredService<ProxyConnectivityService>();
        _mainViewModel.InitializeShell();
        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        Title = AppDisplayName;
        ConfigureTitleBar();
        ConfigureInitialWindowSize();
        this.AppWindow.Changed += OnAppWindowChanged;
        if (ContentFrame.Navigate(typeof(MainPage), null, new SuppressNavigationTransitionInfo()))
            _loadedPageTypes.Add(typeof(MainPage));
        _homeInitializationTask = _mainViewModel.InitializeAsync();

        SetupTrayIcon();
        this.AppWindow.Closing += OnClosing;
    }

    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(13, 0, 0, 0);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(20, 0, 0, 0);
        UpdateTitleBarInsets(titleBar);
    }

    private void UpdateTitleBarInsets(AppWindowTitleBar titleBar)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ??
                    Math.Max(1d, GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d);
        RightInsetSpace.Width = Math.Max(132, titleBar.RightInset / scale);
    }

    private void ConfigureInitialWindowSize()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var scale = Math.Max(1d, GetDpiForWindow(windowHandle) / 96d);
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;

        // Preserve v2rayN's default 1200 x 800 content size, plus the NPP tabs.
        var preferredWidth = (int)Math.Round(1200 * scale);
        var preferredHeight = (int)Math.Round(848 * scale);
        var width = Math.Min(preferredWidth, (int)Math.Round(workArea.Width * 0.94));
        var height = Math.Min(preferredHeight, (int)Math.Round(workArea.Height * 0.94));

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));

        UpdateMinimumWindowSize(AppWindow, scale, workArea);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            SetRegionsForCustomTitleBar();
            var scale = Math.Max(1d, GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d);
            var workArea = DisplayArea.GetFromWindowId(sender.Id, DisplayAreaFallback.Nearest).WorkArea;
            UpdateMinimumWindowSize(sender, scale, workArea);
        }
        _embeddedClient.SetVisible(sender.IsVisible &&
            (sender.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Minimized) &&
            ContentFrame.CurrentSourcePageType == typeof(V2rayNPage));
    }

    private static void UpdateMinimumWindowSize(AppWindow window, double scale, RectInt32 workArea)
    {
        if (window.Presenter is not OverlappedPresenter presenter)
            return;

        presenter.PreferredMinimumWidth = Math.Min(
            (int)Math.Round(860 * scale),
            (int)Math.Round(workArea.Width * 0.94));
        presenter.PreferredMinimumHeight = Math.Min(
            (int)Math.Round(560 * scale),
            (int)Math.Round(workArea.Height * 0.94));
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        SetRegionsForCustomTitleBar();
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetRegionsForCustomTitleBar();
    }

    private void SetRegionsForCustomTitleBar()
    {
        if (AppTitleBar.XamlRoot == null)
            return;

        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        UpdateTitleBarInsets(AppWindow.TitleBar);

        var interactiveElements = new FrameworkElement[]
        {
            HomeNavButton,
            V2rayNNavButton,
            LogNavButton,
            SettingsNavButton
        };
        var regions = interactiveElements
            .Where(element => element.Visibility == Visibility.Visible &&
                              element.ActualWidth > 0 && element.ActualHeight > 0)
            .Select(element =>
            {
                var bounds = element.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale));
            })
            .ToArray();

        _nonClientInputSource ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClientInputSource.SetRegionRects(NonClientRegionKind.Passthrough, regions);
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentState))
            UpdateTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _trayRunningIconTemplate = TrayIconFactory.Create(proxyRunning: true);
        _trayStoppedIconTemplate = TrayIconFactory.Create(proxyRunning: false);

        _trayIcon = new TaskbarIcon
        {
            ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true
        };
        UpdateTrayIcon();

        var menu = new MenuFlyout();

        var showItem = new MenuFlyoutItem
        {
            Text = "显示",
            Command = new RelayCommand(ShowWindow)
        };
        menu.Items.Add(showItem);

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出",
            Command = new AsyncRelayCommand(RequestExitAsync)
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;
        _trayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon == null)
            return;

        var state = _mainViewModel.CurrentState;
        var isRunning = state == State.Started;
        if (_trayShowsRunning != isRunning)
        {
            // H.NotifyIcon owns the assigned icon and disposes it on replacement.
            var template = isRunning ? _trayRunningIconTemplate : _trayStoppedIconTemplate;
            _trayIcon.Icon = (System.Drawing.Icon?)template?.Clone();
            _trayShowsRunning = isRunning;
        }

        _trayIcon.ToolTipText = $"{AppDisplayName} · {state switch
        {
            State.Started => "代理已开启",
            State.Starting => "代理启动中",
            State.Stopping => "代理停止中",
            _ => "代理未开启"
        }}";
    }

    private void ApplyApplicationIcon()
    {
        if (File.Exists(AppIconPath))
        {
            AppWindow.SetIcon(AppIconPath);
            return;
        }

        Serilog.Log.Warning("NPP icon file was not found at {IconPath}", AppIconPath);
    }

    private void ShowWindow()
    {
        if (!_exitInProgress)
            _ = _connectivity.CheckHomeAsync(_mainViewModel.ProxyHost, _mainViewModel.ProxyPort);

        SetWindowVisibility(WindowNative.GetWindowHandle(this), ShowWindowCommandShow);
        this.Activate();
        var presenter = this.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter != null && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            presenter.Restore();
        _embeddedClient.SetVisible(ContentFrame.CurrentSourcePageType == typeof(V2rayNPage));
    }

    public void ActivateFromSecondaryInstance()
    {
        ShowWindow();
    }

    public void MinimizeToTray()
    {
        _embeddedClient.SetVisible(false);
        SetWindowVisibility(WindowNative.GetWindowHandle(this), ShowWindowCommandHide);
    }

    public async Task RunStartupActionsAsync()
    {
        var settings = NetchAppContext.Current?.Settings;
        if (settings == null)
            return;

        if (settings.CheckUpdateWhenOpened)
            _ = CheckForUpdatesOnStartupAsync(settings.CheckBetaUpdate);

        _ = _connectivity.CheckHomeAsync(_mainViewModel.ProxyHost, _mainViewModel.ProxyPort, startup: true);
        // 内置节点和应用分流独立启动。分流规则可以在节点的 SOCKS5 端口就绪之前安装。
        var nodeStartup = settings.StartEmbeddedClientWhenOpened
            ? StartEmbeddedClientAsync()
            : Task.CompletedTask;
        await _homeInitializationTask;
        var shouldAutoStartProxy = settings.StartWhenOpened;
        var started = await _mainViewModel.StartIfConfiguredAsync();
        if (shouldAutoStartProxy && !started && (Program.IsStartupLaunch || settings.MinimizeWhenStarted))
            ShowNotification(_liteModeManager.StatusText, isInfo: false);

        await nodeStartup;
    }

    private async Task StartEmbeddedClientAsync()
    {
        try
        {
            await _embeddedClient.EnsureStartedAsync(WindowNative.GetWindowHandle(this));
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The bundled v2rayN client could not be started");
            ShowNotification("内置 v2rayN 启动失败，可在“代理节点”页重试", isInfo: false);
        }
    }

    public void ShowNotification(string text, bool isInfo = true)
    {
        Serilog.Log.Information("UI notification: {Text}", text);
        _trayIcon?.ShowNotification(
            AppDisplayName,
            text,
            isInfo ? NotificationIcon.Info : NotificationIcon.Error);
    }

    private void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceExit)
        {
            DisposeTrayIcon();
            return;
        }

        var settings = NetchAppContext.Current?.Settings;
        if (settings != null && !settings.ExitWhenClosed)
        {
            args.Cancel = true;
            MinimizeToTray();
        }
        else
        {
            args.Cancel = true;
            _ = RequestExitAsync();
        }
    }

    private async Task RequestExitAsync()
    {
        if (_exitInProgress)
            return;

        _exitInProgress = true;
        _mainViewModel.BeginShutdown();
        try
        {
            await _mainViewModel.WaitForActiveOperationAsync();

            if (_liteModeManager.CurrentState is State.Started or State.Starting or State.Stopping)
                await _liteModeManager.StopAsync();

            await _mainViewModel.SaveSettingsAsync();

            await _embeddedClient.StopAsync();

            _forceExit = true;
            DisposeTrayIcon();
            Close();
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed while shutting down NPP");
            try { await _embeddedClient.StopAsync(); }
            catch (Exception clientError) { Serilog.Log.Warning(clientError, "Failed to stop bundled v2rayN"); }
            try
            {
                await _mainViewModel.SaveSettingsAsync();
            }
            catch (Exception saveException)
            {
                Serilog.Log.Error(saveException, "Failed to save NPP settings while shutting down");
            }

            _forceExit = true;
            DisposeTrayIcon();
            Close();
        }
    }

    public async Task ApplyUpdateAndRestartAsync()
    {
        if (_exitInProgress || _updateService.Stage != AppUpdateStage.ReadyToRestart)
            return;

        _exitInProgress = true;
        var restartEmbeddedClient = false;
        try
        {
            _updateService.BeginPreparingRestart();
            _mainViewModel.BeginShutdown();
            await _mainViewModel.WaitForActiveOperationAsync();

            // Release redirector resources before applying the update.
            if (_liteModeManager.CurrentState is State.Started or State.Starting or State.Stopping)
                await _liteModeManager.StopForUpdateAsync();

            await _mainViewModel.SaveSettingsAsync();
            restartEmbeddedClient = _embeddedClient.IsReady || _embeddedClient.IsStarting;
            await _embeddedClient.StopAsync();
            _updateService.ApplyUpdateAndRestart();

            // ApplyUpdatesAndRestart normally terminates this process. If the
            // platform unexpectedly returns, still complete a clean shutdown.
            _forceExit = true;
            DisposeTrayIcon();
            Close();
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed to apply the pending NPP update");
            _updateService.RestoreReadyToRestart($"更新未应用：{e.GetBaseException().Message}");
            _forceExit = false;
            _exitInProgress = false;
            _mainViewModel.ResumeAfterFailedShutdown();
            try { await _embeddedClient.ResumeAfterFailedUpdateAsync(WindowNative.GetWindowHandle(this), restartEmbeddedClient); }
            catch (Exception clientError) { Serilog.Log.Warning(clientError, "Failed to resume bundled v2rayN after update failure"); }
            ShowNotification("更新未应用，NPP 已恢复运行，可稍后重试", isInfo: false);
        }
    }

    private async Task CheckForUpdatesOnStartupAsync(bool includePrerelease)
    {
        var updateFound = await _updateService.CheckForUpdatesAsync(includePrerelease, isAutomatic: true);
        if (updateFound)
            DispatcherQueue.TryEnqueue(() =>
                ShowNotification("发现 NPP 新版本，可在设置中下载更新"));
    }

    private void DisposeTrayIcon()
    {
        if (!_mainViewModelDisposed)
        {
            _connectivity.Dispose();
            _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
            _mainViewModel.Dispose();
            _mainViewModelDisposed = true;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayRunningIconTemplate?.Dispose();
        _trayRunningIconTemplate = null;
        _trayStoppedIconTemplate?.Dispose();
        _trayStoppedIconTemplate = null;
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowVisibility(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    private async void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button item)
            return;

        var tag = item.Tag?.ToString();
        var pageType = tag switch
        {
            "MainPage" => typeof(MainPage),
            "V2rayNPage" => typeof(V2rayNPage),
            "LogPage" => typeof(LogPage),
            "SettingsPage" => typeof(SettingsPage),
            _ => null
        };

        if (pageType == null)
            return;

        var pageTitle = item.Content?.ToString();

        var navigationVersion = ++_navigationVersion;
        SetSelectedNav(item);

        if (ContentFrame.CurrentSourcePageType == pageType)
        {
            PageLoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var isFirstVisit = !_loadedPageTypes.Contains(pageType);
        if (isFirstVisit)
        {
            PageLoadingText.Text = $"正在打开{pageTitle}…";
            PageLoadingOverlay.Visibility = Visibility.Visible;

            // Return control to the compositor for one frame so the selected
            // tab and loading state appear before a page creates its XAML tree.
            await Task.Delay(16);
            if (navigationVersion != _navigationVersion)
                return;
        }

        try
        {
            if (ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo()))
            {
                _loadedPageTypes.Add(pageType);
                ContentFrame.BackStack.Clear();
            }
            else
            {
                RestoreSelectedNavFromCurrentPage();
                Serilog.Log.Warning("Navigation to {PageType} was declined", pageType.Name);
            }
        }
        catch (Exception exception)
        {
            RestoreSelectedNavFromCurrentPage();
            Serilog.Log.Error(exception, "Failed to navigate to {PageType}", pageType.Name);
            ShowNotification($"页面打开失败：{pageTitle}", isInfo: false);
        }
        finally
        {
            if (navigationVersion == _navigationVersion)
                PageLoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void RestoreSelectedNavFromCurrentPage()
    {
        var currentButton = ContentFrame.CurrentSourcePageType switch
        {
            var type when type == typeof(MainPage) => HomeNavButton,
            var type when type == typeof(V2rayNPage) => V2rayNNavButton,
            var type when type == typeof(LogPage) => LogNavButton,
            var type when type == typeof(SettingsPage) => SettingsNavButton,
            _ => null
        };

        if (currentButton != null)
            SetSelectedNav(currentButton);
    }

    private void SetSelectedNav(Button selected)
    {
        foreach (var button in new[] { HomeNavButton, V2rayNNavButton, LogNavButton, SettingsNavButton })
        {
            var isSelected = ReferenceEquals(button, selected);
            button.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                isSelected
                    ? ColorHelper.FromArgb(255, 23, 32, 51)
                    : ColorHelper.FromArgb(255, 116, 128, 148));
            button.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                ColorHelper.FromArgb(255, 101, 86, 217));
            button.BorderThickness = isSelected
                ? new Thickness(0, 0, 0, 2)
                : new Thickness(0);
        }
    }
}
