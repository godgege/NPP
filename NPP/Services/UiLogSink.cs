using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Serilog.Core;
using Serilog.Events;

namespace Netch.App.Services;

public class UiLogSink : ILogEventSink
{
    private const int MaxEntries = 1000;
    private const int MaxPendingEntries = 2048;
    private const int DrainBatchSize = 128;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IReadOnlyList<string>> _currentProcessNames;
    private readonly ConcurrentQueue<string> _pendingEntries = new();
    private readonly BatchObservableCollection<string> _allLogEntries = new();
    private readonly BatchObservableCollection<string> _defaultLogEntries = new();
    private int _pendingCount;
    private int _drainScheduled;

    public ObservableCollection<string> AllLogEntries => _allLogEntries;

    public ObservableCollection<string> DefaultLogEntries => _defaultLogEntries;

    public ObservableCollection<string> LogEntries => AllLogEntries;

    public UiLogSink(DispatcherQueue dispatcher, Func<IReadOnlyList<string>> currentProcessNames)
    {
        _dispatcher = dispatcher;
        _currentProcessNames = currentProcessNames;
    }

    public void Emit(LogEvent logEvent)
    {
        var message = $"[{logEvent.Timestamp:HH:mm:ss}] {logEvent.RenderMessage()}";
        _pendingEntries.Enqueue(message);
        var pendingCount = Interlocked.Increment(ref _pendingCount);

        while (pendingCount > MaxPendingEntries && _pendingEntries.TryDequeue(out _))
        {
            pendingCount = Interlocked.Decrement(ref _pendingCount);
        }

        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            return;

        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, DrainPendingEntries))
            Interlocked.Exchange(ref _drainScheduled, 0);
    }

    private void DrainPendingEntries()
    {
        var allBatch = new List<string>(DrainBatchSize);
        while (allBatch.Count < DrainBatchSize && _pendingEntries.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _pendingCount);
            allBatch.Add(entry);
        }

        if (allBatch.Count > 0)
        {
            var processNames = _currentProcessNames();
            var defaultBatch = allBatch
                .Where(entry => ShouldShowInDefaultLog(entry, processNames))
                .ToArray();

            _allLogEntries.AppendBatch(allBatch, MaxEntries);
            _defaultLogEntries.AppendBatch(defaultBatch, MaxEntries);
        }

        Interlocked.Exchange(ref _drainScheduled, 0);
        if (!_pendingEntries.IsEmpty)
            ScheduleDrain();
    }

    private static bool ShouldShowInDefaultLog(string entry, IReadOnlyList<string> processNames)
    {
        if (!entry.Contains("[Redirector][EventHandler]", StringComparison.Ordinal))
            return true;

        if (IsRedirectedTrafficEntry(entry))
            return true;

        foreach (var name in processNames)
        {
            if (entry.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsRedirectedTrafficEntry(string entry)
    {
        if (!entry.Contains("[tcpConnectRequest]", StringComparison.Ordinal) &&
            !entry.Contains("[udpCreated]", StringComparison.Ordinal))
            return false;

        return !entry.Contains("[!", StringComparison.Ordinal) &&
               !entry.Contains("[checkBypassName]", StringComparison.Ordinal);
    }

    private sealed class BatchObservableCollection<T> : ObservableCollection<T>
    {
        public void AppendBatch(IEnumerable<T> newItems, int maximumCount)
        {
            var changed = false;
            foreach (var item in newItems)
            {
                Items.Add(item);
                changed = true;
            }

            var overflow = Items.Count - maximumCount;
            for (var index = 0; index < overflow; index++)
                Items.RemoveAt(0);

            if (!changed && overflow <= 0)
                return;

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
