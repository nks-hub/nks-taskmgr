using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>One logged-on or process-bearing session with totals over its processes from the same frame.</summary>
internal sealed record UserSession(uint Id, SessionConnectState State, SessionProtocol Protocol, SampleState InfoState,
    string User, string Domain, string Station, string Client, int Members, ProcessBranchMetric Cpu, ProcessBranchBytes Working,
    ProcessBranchBytes Private, ProcessBranchMetric Io)
{
    internal static ProcessBranchMetric Metric(SessionSum sum) => new(sum.Value, (int)sum.Known, sum.Overflow != 0);
    internal static ProcessBranchBytes Bytes(SessionSum sum) => new(sum.Overflow != 0 ? 0 : (ulong)sum.Value, (int)sum.Known, sum.Overflow != 0);
}

public sealed partial class MonitorModel
{
    private bool sessionViewVisible;
    private long sessionsQueriedAt;
    internal IReadOnlyList<UserSession> Sessions { get; private set; } = [];
    internal Dictionary<ProcessKey, uint> ProcessSessions { get; } = [];
    internal SampleState SessionState { get; private set; } = SampleState.First;
    /// <summary>True while WM_WTSSESSION_CHANGE invalidates the native session cache; otherwise it is refreshed every 5 s.</summary>
    internal bool SessionNotifications { get; set; }

    /// <summary>Session data is aggregated per frame only while the view is shown; WTS itself is queried on change.</summary>
    internal void SetSessionViewVisible(bool value)
    {
        if (value && !sessionViewVisible) NativeFrame.InvalidateSessions();
        sessionViewVisible = value;
    }

    /// <summary>True while per-process network rates exist, from the ETW capture or else from the energy counters.</summary>
    internal bool NetworkAvailable { get; private set; }

    /// <summary>Copies per-generation network rates to the rows; true when the network sort order may have changed.</summary>
    private bool ApplyProcessNetwork()
    {
        var rates = ProcessNetwork.Rates(out bool partial, out bool totalPartial);
        bool energy = rates is null;
        if (energy) rates = ProcessEnergyNetwork.Rates(processes.Values.Where(row => !row.Ended && !row.Stale).Select(row => row.Key));
        else ProcessEnergyNetwork.Clear();
        NetworkAvailable = true;
        bool changed = false;
        foreach (var row in processes.Values) {
            var key = energy ? row.Key : CaptureKey(row);
            // ETW lists only processes with traffic; an energy counter that could not be read stays unknown.
            ProcessNetworkRate? rate = key.Kind != GenerationKind.CreationTime && (!energy || key.Kind != GenerationKind.SequenceNumber) ? null
                : rates!.TryGetValue(key, out var known) ? known : energy ? null : default(ProcessNetworkRate);
            changed |= row.SetNetwork(rate, partial, totalPartial, ProcessVisualUpdates);
        }
        return changed && sortColumn == 7;
    }

    /// <summary>ETW samples carry the creation time even where rows are keyed by sequence number.</summary>
    private static ProcessKey CaptureKey(ProcessRow row) => row.Key.Kind == GenerationKind.SequenceNumber && row.Created100ns != 0
        ? row.Key with { Kind = GenerationKind.CreationTime, Generation = row.Created100ns } : row.Key;

    /// <summary>Top processes by bytes read and written on one physical disk, from the ETW capture; null when it has no complete window.</summary>
    public List<(ProcessRow Row, double BytesPerSecond)>? TopDiskConsumers(uint disk)
    {
        if (ProcessNetwork.DiskRates(disk) is not { } rates) return null;
        var result = new List<(ProcessRow Row, double BytesPerSecond)>();
        foreach (var row in processes.Values)
            if (row.Key.Pid != 0 && !row.Ended && !row.Stale && rates.TryGetValue(CaptureKey(row), out var rate) && rate.Read + rate.Write > 0)
                result.Add((row, rate.Read + rate.Write));
        result.Sort((a, b) => b.BytesPerSecond.CompareTo(a.BytesPerSecond));
        if (result.Count > 3) result.RemoveRange(3, result.Count - 3);
        return result;
    }

    private void ReadSessions(NativeFrame frame, FrameView view)
    {
        if (!sessionViewVisible || (view.Flags & FrameFlags.ProcessData) == 0) return;
        long now = Environment.TickCount64;
        if (!SessionNotifications && now - sessionsQueriedAt >= 5000) { NativeFrame.InvalidateSessions(); sessionsQueriedAt = now; }
        frame.ReadSessions(data => ApplySessions(view, data));
    }

    private unsafe void ApplySessions(FrameView view, SessionData data)
    {
        SessionState = data.State;
        ProcessSessions.Clear();
        uint count = Math.Min(data.ProcessCount, view.Processes.ProcessCount);
        for (uint i = 0; i < count; ++i) {
            var source = view.Processes.Processes[i];
            ProcessSessions[new ProcessKey(BootIdentity.Identifier, source.Pid, source.GenerationKind, source.Generation)] = data.ProcessSessions[i];
        }
        var sessions = new List<UserSession>((int)data.SessionCount);
        for (uint i = 0; i < data.SessionCount; ++i) {
            var item = data.Sessions[i];
            // The native list already omits sessions WTS reports without a user; this also drops unreadable empty ones.
            if (item.ProcessCount == 0 && item.User.Length == 0) continue;
            sessions.Add(new(item.SessionId, item.State, item.Protocol, item.InfoState, item.User.Copy(), item.Domain.Copy(), item.Station.Copy(),
                item.Client.Copy(), (int)item.ProcessCount, UserSession.Metric(item.CpuPercent), UserSession.Bytes(item.WorkingSetBytes),
                UserSession.Bytes(item.PrivateBytes), UserSession.Metric(item.IoBytesPerSecond)));
        }
        Sessions = sessions;
    }
}
