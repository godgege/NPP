using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Netch.App.ViewModels;

namespace Netch.App.Views;

public sealed partial class LogPage : Page
{
    private const double BottomTolerance = 4;
    private bool _isPinnedToBottom = true;
    private bool _isActive;
    private bool _scrollPending;
    private ObservableCollection<string>? _observedEntries;

    public LogViewModel ViewModel { get; }

    public LogPage()
    {
        ViewModel = App.Services.GetRequiredService<LogViewModel>();
        InitializeComponent();
        Loaded += LogPage_Loaded;
        Unloaded += LogPage_Unloaded;
    }

    private async void LogPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isActive)
            return;

        _isActive = true;
        ObserveVisibleEntries();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        LogScrollViewer.ViewChanged += LogScrollViewer_ViewChanged;

        await ViewModel.ActivateAsync();
        if (_isActive)
            RequestScrollToBottom();
    }

    private void LogPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_isActive)
            return;

        _isActive = false;
        StopObservingVisibleEntries();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        LogScrollViewer.ViewChanged -= LogScrollViewer_ViewChanged;
        ViewModel.Deactivate();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.VisibleLogEntries))
        {
            ObserveVisibleEntries();
            _isPinnedToBottom = true;
            RequestScrollToBottom();
        }
        else if (e.PropertyName == nameof(ViewModel.ShowAllProcessEvents))
        {
            _isPinnedToBottom = true;
            RequestScrollToBottom();
        }
    }

    private void ObserveVisibleEntries()
    {
        StopObservingVisibleEntries();
        _observedEntries = ViewModel.VisibleLogEntries;
        _observedEntries.CollectionChanged += LogEntries_CollectionChanged;
    }

    private void StopObservingVisibleEntries()
    {
        if (_observedEntries is null)
            return;

        _observedEntries.CollectionChanged -= LogEntries_CollectionChanged;
        _observedEntries = null;
    }

    private void LogEntries_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isPinnedToBottom)
            RequestScrollToBottom();
    }

    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _isPinnedToBottom = IsAtBottom();
    }

    private void RequestScrollToBottom()
    {
        if (_scrollPending || !_isActive)
            return;

        _scrollPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _scrollPending = false;
            if (!_isActive || !_isPinnedToBottom)
                return;

            LogScrollViewer.UpdateLayout();
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
            _isPinnedToBottom = true;
        });
    }

    private bool IsAtBottom()
    {
        return LogScrollViewer.ScrollableHeight - LogScrollViewer.VerticalOffset <= BottomTolerance;
    }
}
