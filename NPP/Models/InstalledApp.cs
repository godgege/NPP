using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Netch.App.Models;

public partial class InstalledApp : ObservableObject
{
    public required string Name { get; init; }
    public required string InstallPath { get; init; }
    public string? ExePath { get; init; }

    public string Initial
    {
        get
        {
            var trimmed = Name.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return "?";
            if (trimmed.StartsWith("Antigravity IDE", StringComparison.OrdinalIgnoreCase))
                return "AI";
            if (trimmed.StartsWith("Antigravity", StringComparison.OrdinalIgnoreCase))
                return "AG";
            return trimmed[..1].ToUpperInvariant();
        }
    }

    public Brush RowBackground => IsSelected ? BrushFrom("#F7F6FE") : BrushFrom("#00FFFFFF");

    public Brush RowBorderBrush => IsSelected ? BrushFrom("#BDBEE9") : BrushFrom("#00FFFFFF");

    public Brush CheckBackground => IsSelected ? BrushFrom("#5546CC") : BrushFrom("#00FFFFFF");

    public Brush CheckBorderBrush => IsSelected ? BrushFrom("#5546CC") : BrushFrom("#DFE6EF");

    public Visibility CheckVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public Brush LogoBackground => Name.Contains("Android", StringComparison.OrdinalIgnoreCase)
        ? BrushFrom("#E3F5EF")
        : Name.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
            ? BrushFrom("#E2F3FD")
            : BrushFrom("#E8EDF3");

    public Brush LogoForeground => Name.Contains("Android", StringComparison.OrdinalIgnoreCase)
        ? BrushFrom("#15916F")
        : Name.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
            ? BrushFrom("#278AC5")
            : BrushFrom("#596779");

    public Visibility LogoTextVisibility => IsTelegram || IsPackage ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TelegramIconVisibility => IsTelegram ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PackageIconVisibility => IsPackage ? Visibility.Visible : Visibility.Collapsed;

    private bool IsTelegram => Name.Contains("Telegram", StringComparison.OrdinalIgnoreCase);

    private bool IsPackage => Name.Equals("App Installer", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(RowBorderBrush));
        OnPropertyChanged(nameof(CheckBackground));
        OnPropertyChanged(nameof(CheckBorderBrush));
        OnPropertyChanged(nameof(CheckVisibility));
    }

    private static SolidColorBrush BrushFrom(string value)
    {
        var hasAlpha = value.Length == 9;
        var colorOffset = hasAlpha ? 3 : 1;
        return new SolidColorBrush(ColorHelper.FromArgb(
            hasAlpha ? Convert.ToByte(value.Substring(1, 2), 16) : (byte)255,
            Convert.ToByte(value.Substring(colorOffset, 2), 16),
            Convert.ToByte(value.Substring(colorOffset + 2, 2), 16),
            Convert.ToByte(value.Substring(colorOffset + 4, 2), 16)));
    }
}
