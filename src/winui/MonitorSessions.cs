using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>One logged-on or process-bearing session with totals over its processes from the same frame.</summary>
internal sealed record UserSession(uint Id, SessionConnectState State, SessionProtocol Protocol, SampleState InfoState,
    string User, string Domain, string Station, int Members, ProcessBranchMetric Cpu, ProcessBranchBytes Working,
    ProcessBranchBytes Private, ProcessBranchMetric Io)
{
    internal static ProcessBranchMetric Metric(SessionSum sum) => new(sum.Value, (int)sum.Known, sum.Overflow != 0);
    internal static ProcessBranchBytes Bytes(SessionSum sum) => new(sum.Overflow != 0 ? 0 : (ulong)sum.Value, (int)sum.Known, sum.Overflow != 0);
}

public sealed partial class MonitorModel
{
    private bool sessionViewVisible;
    internal IReadOnlyList<UserSession> Sessions { get; private set; } = [];
    internal Dictionary<ProcessKey, uint> ProcessSessions { get; } = [];
    internal SampleState SessionState { get; private set; } = SampleState.First;

    /// <summary>Session data costs a WTS query per frame, so it is read only while the view is shown.</summary>
    internal void SetSessionViewVisible(bool value) => sessionViewVisible = value;

    private void ReadSessions(NativeFrame frame, FrameView view)
    {
        if (sessionViewVisible && (view.Flags & FrameFlags.ProcessData) != 0) frame.ReadSessions(data => ApplySessions(view, data));
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
            // Listener sessions such as RDP-Tcp have neither a user nor processes.
            if (item.ProcessCount == 0 && item.User.Length == 0) continue;
            sessions.Add(new(item.SessionId, item.State, item.Protocol, item.InfoState, item.User.Copy(), item.Domain.Copy(), item.Station.Copy(),
                (int)item.ProcessCount, UserSession.Metric(item.CpuPercent), UserSession.Bytes(item.WorkingSetBytes),
                UserSession.Bytes(item.PrivateBytes), UserSession.Metric(item.IoBytesPerSecond)));
        }
        Sessions = sessions;
    }
}
