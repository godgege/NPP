using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using Netch.App.Models;
using Netch.App.ViewModels;

namespace Netch.App.Views;

public sealed partial class MainPage : Page, INotifyPropertyChanged
{
    private bool? _appliedConfigurationEnabled;
    private bool _isPageActive;
    private bool _shouldLoadHomeContent;

    public MainViewModel ViewModel { get; }

    public bool ShouldLoadHomeContent => _shouldLoadHomeContent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isPageActive = true;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        TryMaterializeHomeContent();
        if (ViewModel.IsHomeLoaded)
            DispatcherQueue.TryEnqueue(UpdateConfigurationEditState);
        IsTabStop = true;
        Focus(FocusState.Programmatic);
        IsTabStop = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isPageActive = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentState) or
            nameof(MainViewModel.CanEditConfiguration) or
            nameof(MainViewModel.IsHomeLoaded))
            DispatcherQueue.TryEnqueue(UpdateConfigurationEditState);

        if (e.PropertyName == nameof(MainViewModel.IsHomeLoaded))
            TryMaterializeHomeContent();
    }

    private void TryMaterializeHomeContent()
    {
        if (_shouldLoadHomeContent || !_isPageActive || !ViewModel.IsHomeLoaded)
            return;

        _shouldLoadHomeContent = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShouldLoadHomeContent)));
    }

    private void UpdateConfigurationEditState()
    {
        if (!ViewModel.IsHomeLoaded ||
            ProxySettingsCard == null ||
            AppSelectionCard == null ||
            ProxyRulesCard == null)
            return;

        var enabled = ViewModel.CanEditConfiguration;
        if (_appliedConfigurationEnabled == enabled)
            return;

        _appliedConfigurationEnabled = enabled;
        ProxySettingsCard.IsHitTestVisible = enabled;
        AppSelectionCard.IsHitTestVisible = enabled;
        ProxyRulesCard.IsHitTestVisible = enabled;
        SetControlsEnabled(ProxySettingsCard, enabled);
        SetControlsEnabled(AppSelectionCard, enabled);
        SetControlsEnabled(ProxyRulesCard, enabled);
        if (enabled)
        {
            ProxyHostTextBox.IsEnabled = ViewModel.IsHostEditable;
            ScanDirectoryButton.IsEnabled = ViewModel.CanScanDirectory;
        }
        ConfigurationLockOverlay.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetControlsEnabled(DependencyObject root, bool enabled)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Control control)
                control.IsEnabled = enabled;

            SetControlsEnabled(child, enabled);
        }
    }

    private async void AppCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: InstalledApp app })
            await ViewModel.OnAppSelectionChangedAsync(app);
    }

    private async void AppRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstalledApp app })
            return;

        app.IsSelected = !app.IsSelected;
        await ViewModel.OnAppSelectionChangedAsync(app);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelection();
    }

    private async void AdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditConfiguration)
            return;

        ApplyAdvancedProxySettings(ViewModel.GetAdvancedProxySettings());
        AdvancedProxyErrorBar.IsOpen = false;
        AdvancedProxyDialog.XamlRoot = XamlRoot;

        try
        {
            await AdvancedProxyDialog.ShowAsync();
        }
        catch (Exception exception)
        {
            App.MainWindow.ShowNotification($"高级设置无法打开：{exception.GetBaseException().Message}", isInfo: false);
        }
    }

    private async void AdvancedProxyDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var settings = new MainViewModel.AdvancedProxySettings(
                AdvancedUdpToggle.IsOn,
                AdvancedChildProcessToggle.IsOn,
                AdvancedBypassLanToggle.IsOn,
                AdvancedDnsHostTextBox.Text);

            await ViewModel.SaveAdvancedProxySettingsAsync(settings);
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            AdvancedProxyErrorBar.Message = exception.GetBaseException().Message;
            AdvancedProxyErrorBar.IsOpen = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void AdvancedProxyDialog_SecondaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        AdvancedProxyErrorBar.IsOpen = false;
        ApplyAdvancedProxySettings(MainViewModel.GetDefaultAdvancedProxySettings());
    }

    private void ApplyAdvancedProxySettings(MainViewModel.AdvancedProxySettings settings)
    {
        AdvancedUdpToggle.IsOn = settings.EnableUdp;
        AdvancedChildProcessToggle.IsOn = settings.IncludeChildProcesses;
        AdvancedBypassLanToggle.IsOn = settings.BypassLan;
        AdvancedDnsHostTextBox.Text = settings.DnsHost;
        UpdateAdvancedProxyDependencies();
    }

    private void AdvancedUdpToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AdvancedProxyErrorBar.IsOpen = false;
        UpdateAdvancedProxyDependencies();
    }

    private void AdvancedDnsHostTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AdvancedProxyErrorBar.IsOpen = false;
    }

    private void AdvancedProxyDialogContent_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (AdvancedDnsHostTextBox.FocusState == FocusState.Unfocused ||
            e.OriginalSource is not DependencyObject source ||
            IsInsideControl(source, AdvancedProxyDialogContent))
            return;

        var wasTabStop = AdvancedProxyDialog.IsTabStop;
        try
        {
            AdvancedProxyDialog.IsTabStop = true;
            AdvancedProxyDialog.Focus(FocusState.Pointer);
        }
        finally
        {
            AdvancedProxyDialog.IsTabStop = wasTabStop;
        }
    }

    private static bool IsInsideControl(DependencyObject source, DependencyObject boundary)
    {
        for (DependencyObject? current = source;
             current is not null && current != boundary;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Control)
                return true;
        }

        return false;
    }

    private void UpdateAdvancedProxyDependencies()
    {
        AdvancedDnsSection.Visibility = AdvancedUdpToggle.IsOn
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DirectoryProcessCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ProcessEntry process })
            ViewModel.OnDirectoryProcessSelectionChanged(process);
    }

    private void AddToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessGroup group })
            ViewModel.AddToGroupCommand.Execute(group);
    }

    private void AddExeToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessGroup group })
            ViewModel.AddExeToGroupCommand.Execute(group);
    }

    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessGroup group })
            ViewModel.RemoveGroupCommand.Execute(group);
    }
}

