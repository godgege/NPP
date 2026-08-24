using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Netch.App.ViewModels;

namespace Netch.App.Views;

public sealed partial class SettingsPage : Page
{
    private const double TwoColumnBreakpoint = 780;
    private const double CompactPaddingBreakpoint = 520;

    private bool? _isTwoColumnLayout;
    private bool? _usesCompactPadding;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveLayout(ActualWidth);
    }

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAdaptiveLayout(e.NewSize.Width);
    }

    private void ApplyAdaptiveLayout(double width)
    {
        var twoColumns = width >= TwoColumnBreakpoint;
        var compactPadding = width < CompactPaddingBreakpoint;

        if (_usesCompactPadding != compactPadding)
        {
            _usesCompactPadding = compactPadding;
            SettingsScroll.Padding = compactPadding
                ? new Thickness(14, 12, 14, 16)
                : new Thickness(22, 14, 22, 18);
        }

        if (_isTwoColumnLayout == twoColumns)
            return;

        _isTwoColumnLayout = twoColumns;
        SettingsLayout.ColumnSpacing = twoColumns ? 12 : 0;
        SettingsLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        SettingsLayout.ColumnDefinitions[1].Width = twoColumns
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        Grid.SetRow(StartupCard, 0);
        Grid.SetColumn(StartupCard, 0);

        Grid.SetRow(SecondaryColumn, twoColumns ? 0 : 1);
        Grid.SetColumn(SecondaryColumn, twoColumns ? 1 : 0);

    }
}
