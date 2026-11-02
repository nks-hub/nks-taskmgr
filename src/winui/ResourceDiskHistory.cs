using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ResourceRow
{
    public HistorySeries DiskReadHistory { get; private set; } = new();
    public HistorySeries DiskWriteHistory { get; private set; } = new();

    internal void AttachDiskHistory(HistorySeries series, bool write)
    {
        if (Kind != ResourceKind.Disk) return;
        if (write) DiskWriteHistory = series; else DiskReadHistory = series;
    }

    internal void RecordDiskHistory(long time, Metric read, Metric write)
    {
        if (Kind != ResourceKind.Disk) return;
        DiskReadHistory.Append(time, read);
        DiskWriteHistory.Append(time, write);
    }
}
