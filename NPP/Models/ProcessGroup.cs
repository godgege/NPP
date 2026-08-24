using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Netch.App.Models;

public partial class ProcessGroup : ObservableObject
{
    public string GroupName { get; init; } = "";

    public string SourceKey { get; init; } = "";

    public string Initial
    {
        get
        {
            var trimmed = GroupName.Trim();
            return string.IsNullOrEmpty(trimmed) ? "?" : trimmed[..1].ToUpperInvariant();
        }
    }

    public Brush LogoBackground => GroupName.Contains("Android", StringComparison.OrdinalIgnoreCase)
        ? BrushFrom("#E3F5EF")
        : GroupName.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
            ? BrushFrom("#E2F3FD")
            : BrushFrom("#E8EDF3");

    public Brush LogoForeground => GroupName.Contains("Android", StringComparison.OrdinalIgnoreCase)
        ? BrushFrom("#15916F")
        : GroupName.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
            ? BrushFrom("#278AC5")
            : BrushFrom("#596779");

    public Visibility LogoTextVisibility => GroupName.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility TelegramIconVisibility => GroupName.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public BulkObservableCollection<ProcessEntry> Processes { get; } = new();

    public string ProcessCountText => $"{Processes.Count} 个进程";

    [ObservableProperty]
    private string _displayText = "";

    public ProcessGroup()
    {
        Processes.CollectionChanged += OnProcessesChanged;
    }

    private void OnProcessesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        DisplayText = string.Join(" · ", Processes.Select(p => p.FileName));
        OnPropertyChanged(nameof(ProcessCountText));
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
