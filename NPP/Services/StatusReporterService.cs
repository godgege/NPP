using Microsoft.UI.Dispatching;
using Netch.Interfaces;

namespace Netch.App.Services;

public class StatusReporterService : IStatusReporter
{
    private readonly DispatcherQueue _dispatcherQueue;

    public event Action<string>? StatusChanged;
    public event Action<bool>? BandwidthStateChanged;
    public event Action<ulong>? BandwidthUpdated;

    public StatusReporterService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public void ReportStatus(string text)
    {
        _dispatcherQueue.TryEnqueue(() => StatusChanged?.Invoke(text));
    }

    public void ReportBandwidthState(bool visible)
    {
        _dispatcherQueue.TryEnqueue(() => BandwidthStateChanged?.Invoke(visible));
    }

    public void ReportBandwidthUpdated(ulong download)
    {
        _dispatcherQueue.TryEnqueue(() => BandwidthUpdated?.Invoke(download));
    }
}
