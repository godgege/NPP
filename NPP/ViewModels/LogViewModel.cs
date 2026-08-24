using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace Netch.App.ViewModels;

public partial class LogViewModel : ObservableObject, IDisposable
{
    private static readonly ObservableCollection<string> EmptyLogEntries = new();
    private readonly ObservableCollection<string> _allLogEntries;
    private readonly ObservableCollection<string> _defaultLogEntries;
    private bool _showAllProcessEvents;
    private bool _active;
    private bool _subscribed;
    private bool _hasLoaded;
    private bool _disposed;
    private int _activationVersion;

    [ObservableProperty]
    private bool _isLoading = true;

    public ObservableCollection<string> VisibleLogEntries => ActiveSourceLogEntries();

    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LogEntriesVisibility => !IsLoading && VisibleLogEntries.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility EmptyLogVisibility => !IsLoading && VisibleLogEntries.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public LogViewModel()
    {
        _allLogEntries = App.UiLogSink?.AllLogEntries ?? EmptyLogEntries;
        _defaultLogEntries = App.UiLogSink?.DefaultLogEntries ?? EmptyLogEntries;
    }

    public bool ShowAllProcessEvents
    {
        get => _showAllProcessEvents;
        set
        {
            if (!SetProperty(ref _showAllProcessEvents, value) || !_active)
                return;

            OnPropertyChanged(nameof(VisibleLogEntries));
            NotifyVisibilityChanged();
        }
    }

    public async Task ActivateAsync()
    {
        if (_disposed || _active)
            return;

        var activationVersion = ++_activationVersion;
        _active = true;
        var showLoading = !_hasLoaded;
        if (showLoading)
            IsLoading = true;

        // Let the cached page shell render before taking the initial snapshot.
        await Task.Yield();
        if (!_active || _disposed || activationVersion != _activationVersion)
            return;

        SubscribeToSources();
        OnPropertyChanged(nameof(VisibleLogEntries));
        NotifyVisibilityChanged();

        _hasLoaded = true;
        if (showLoading)
            IsLoading = false;
    }

    public void Deactivate()
    {
        _activationVersion++;
        if (!_active)
            return;

        _active = false;
        UnsubscribeFromSources();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Deactivate();
        _disposed = true;
    }

    private void SourceLogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_active || _disposed || !ReferenceEquals(sender, ActiveSourceLogEntries()))
            return;

        NotifyVisibilityChanged();
    }

    private void SubscribeToSources()
    {
        if (_subscribed)
            return;

        _allLogEntries.CollectionChanged += SourceLogEntries_CollectionChanged;
        _defaultLogEntries.CollectionChanged += SourceLogEntries_CollectionChanged;
        _subscribed = true;
    }

    private void UnsubscribeFromSources()
    {
        if (!_subscribed)
            return;

        _allLogEntries.CollectionChanged -= SourceLogEntries_CollectionChanged;
        _defaultLogEntries.CollectionChanged -= SourceLogEntries_CollectionChanged;
        _subscribed = false;
    }

    private void NotifyVisibilityChanged()
    {
        OnPropertyChanged(nameof(LogEntriesVisibility));
        OnPropertyChanged(nameof(EmptyLogVisibility));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(LoadingVisibility));
        NotifyVisibilityChanged();
    }

    private ObservableCollection<string> ActiveSourceLogEntries()
    {
        return ShowAllProcessEvents ? _allLogEntries : _defaultLogEntries;
    }

}
