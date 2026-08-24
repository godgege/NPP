using CommunityToolkit.Mvvm.ComponentModel;

namespace Netch.App.Models;

public partial class ProcessEntry : ObservableObject
{
    public required string FullPath { get; init; }

    public string FileName => Path.GetFileName(FullPath);

    [ObservableProperty]
    private bool _isSelected = true;
}
