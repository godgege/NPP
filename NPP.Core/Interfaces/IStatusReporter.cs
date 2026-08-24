namespace Netch.Interfaces;

public interface IStatusReporter
{
    void ReportStatus(string text);
    void ReportBandwidthState(bool visible);
    void ReportBandwidthUpdated(ulong download);
}
