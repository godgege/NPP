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
    private System.Drawing.Icon? _trayDrawingIcon;
    private readonly LiteModeManager _liteModeManager;
    private readonly MainViewModel _mainViewModel;
    private readonly VelopackUpdateService _updateService;
    private readonly Task _homeInitializationTask;
    private readonly HashSet<Type> _loadedPageTypes = [];
    private bool _forceExit;
    private bool _exitInProgress;
    private bool _mainViewModelDisposed;
    private bool _hasTitleBarStateSnapshot;
    private State _lastTitleBarState;
    private InputNonClientPointerSource? _nonClientInputSource;
    private int _navigationVersion;

    public MainWindow()
    {
        InitializeComponent();
        ApplyApplicationIcon();
        _liteModeManager = App.Services.GetRequiredService<LiteModeManager>();
        _mainViewModel = App.Services.GetRequiredService<MainViewModel>();
        _updateService = App.Services.GetRequiredService<VelopackUpdateService>();
        _mainViewModel.InitializeShell();
        _mainViewModel.PropertyChanged += TitleBarViewModel_PropertyChanged;
        Title = AppDisplayName;
        ConfigureTitleBar();
        ConfigureInitialWindowSize();
        this.AppWindow.Changed += OnAppWindowChanged;
        UpdateTitleBarProxyControls();
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

        var preferredWidth = (int)Math.Round(900 * scale);
        var preferredHeight = (int)Math.Round(600 * scale);
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
            LogNavButton,
            SettingsNavButton,
            TitleBarStartStopButton
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

    private void TitleBarViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentState) or
            nameof(MainViewModel.RunStateLabel) or
            nameof(MainViewModel.ControlButtonText) or
            nameof(MainViewModel.CanStartStop))
            UpdateTitleBarProxyControls();
    }

    private void UpdateTitleBarProxyControls()
    {
        var currentState = _mainViewModel.CurrentState;
        var stateChanged = !_hasTitleBarStateSnapshot || currentState != _lastTitleBarState;

        TitleBarStatusText.Text = currentState switch
        {
            State.Started => "运行中",
            State.Starting => "启动中",
            State.Stopping => "停止中",
            _ => "未运行"
        };
        TitleBarStartStopButton.Content = currentState is State.Started or State.Starting
            ? "停止代理"
            : "启动代理";
        TitleBarStartStopButton.IsEnabled = _mainViewModel.CanStartStop;

        TitleBarStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            currentState switch
            {
                State.Started => ColorHelper.FromArgb(255, 30, 166, 114),
                State.Starting or State.Stopping => ColorHelper.FromArgb(255, 224, 151, 42),
                _ => ColorHelper.FromArgb(255, 139, 151, 168)
            });

        var shouldRestoreFocus = _hasTitleBarStateSnapshot &&
                                 TitleBarStartStopButton.IsEnabled &&
                                 currentState is State.Waiting or State.Stopped or State.Started &&
                                 stateChanged;
        _lastTitleBarState = currentState;
        _hasTitleBarStateSnapshot = true;

        if (shouldRestoreFocus)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (TitleBarStartStopButton.IsEnabled)
                    TitleBarStartStopButton.Focus(FocusState.Programmatic);
            });
    }

    private void TitleBarStartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainViewModel.StartStopCommand.CanExecute(null))
            _mainViewModel.StartStopCommand.Execute(null);
    }

    private void SetupTrayIcon()
    {
        if (File.Exists(AppIconPath))
            _trayDrawingIcon = new System.Drawing.Icon(AppIconPath);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = AppDisplayName,
            Icon = _trayDrawingIcon,
            ContextMenuMode = H.NotifyIcon.ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true
        };

        var menu = new MenuFlyout();

        var showItem = new MenuFlyoutItem
        {
            Text = "显示主窗口",
            Command = new RelayCommand(ShowWindow)
        };
        menu.Items.Add(showItem);

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出 NPP",
            Command = new AsyncRelayCommand(RequestExitAsync)
        };
        menu.Items.Add(exitItem);

        _trayIcon.ContextFlyout = menu;
        _trayIcon.LeftClickCommand = new RelayCommand(ShowWindow);
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
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
        SetWindowVisibility(WindowNative.GetWindowHandle(this), ShowWindowCommandShow);
        this.Activate();
        var presenter = this.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        if (presenter != null && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            presenter.Restore();
    }

    public void ActivateFromSecondaryInstance()
    {
        ShowWindow();
    }

    public void MinimizeToTray()
    {
        SetWindowVisibility(WindowNative.GetWindowHandle(this), ShowWindowCommandHide);
    }

    public async Task RunStartupActionsAsync()
    {
        var settings = NetchAppContext.Current?.Settings;
        if (settings == null)
            return;

        if (settings.CheckUpdateWhenOpened)
            _ = CheckForUpdatesOnStartupAsync(settings.CheckBetaUpdate);

        await _homeInitializationTask;
        var started = await _mainViewModel.StartIfConfiguredAsync();

        if (Program.IsStartupLaunch)
        {
            MinimizeToTray();
            if (settings.StartWhenOpened && !started)
                ShowNotification(_liteModeManager.StatusText, isInfo: false);
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
        TitleBarStartStopButton.IsEnabled = false;
        try
        {
            await _mainViewModel.WaitForActiveOperationAsync();

            var settings = NetchAppContext.Current?.Settings;
            if (settings?.StopWhenExited == true &&
                _liteModeManager.CurrentState is State.Started or State.Starting or State.Stopping)
                await _liteModeManager.StopAsync();

            await _mainViewModel.SaveSettingsAsync();

            _forceExit = true;
            DisposeTrayIcon();
            Close();
        }
        catch (Exception e)
        {
            Serilog.Log.Error(e, "Failed while shutting down NPP");
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
        try
        {
            _updateService.BeginPreparingRestart();
            _mainViewModel.BeginShutdown();
            TitleBarStartStopButton.IsEnabled = false;
            await _mainViewModel.WaitForActiveOperationAsync();

            // An updater must never inherit the normal "keep proxy running on exit"
            // preference: the driver and redirector need to release their files first.
            if (_liteModeManager.CurrentState is State.Started or State.Starting or State.Stopping)
                await _liteModeManager.StopForUpdateAsync();

            await _mainViewModel.SaveSettingsAsync();
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
            UpdateTitleBarProxyControls();
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
            _mainViewModel.PropertyChanged -= TitleBarViewModel_PropertyChanged;
            _mainViewModel.Dispose();
            _mainViewModelDisposed = true;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
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
            "LogPage" => typeof(LogPage),
            "SettingsPage" => typeof(SettingsPage),
            _ => null
        };

        if (pageType == null)
            return;

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
            PageLoadingText.Text = $"正在打开{item.Content}…";
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
            ShowNotification($"页面打开失败：{item.Content}", isInfo: false);
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
            var type when type == typeof(LogPage) => LogNavButton,
            var type when type == typeof(SettingsPage) => SettingsNavButton,
            _ => null
        };

        if (currentButton != null)
            SetSelectedNav(currentButton);
    }

    private void SetSelectedNav(Button selected)
    {
        foreach (var button in new[] { HomeNavButton, LogNavButton, SettingsNavButton })
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
