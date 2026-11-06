using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed record ProcessApplicationInfo(ulong Window, ProcessKey Owner, ProcessKey? Host, string Title,
    string AppId, string Package, SampleState AppIdState, SampleState PackageState);

internal static partial class ProcessDetailsProtocol
{
    private static ProcessDetailsSnapshot ReadApplications(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> request)
    {
        var state = (ProcessDetailsState)UInt(header, 12);
        var result = new ProcessDetailsSnapshot(ProcessDetailsCommand.Applications, state, UInt(header, 56), Int(header, 60),
            (ProcessArchitecture)UInt(header, 64), (UInt(header, 72) & 1) != 0, [], [], null, DateTimeOffset.UtcNow);
        if (state != ProcessDetailsState.Ok) return result;
        var values = new ProcessApplicationInfo[UInt(header, 68)];
        var windows = new HashSet<ulong>();
        var boot = new BootIdentifier(ULong(request, 24), ULong(request, 32));
        int offset = 0;
        for (int i = 0; i < values.Length; ++i) {
            var row = Take(payload, ref offset, 64);
            ulong window = ULong(row, 0), generation = ULong(row, 16), hostGeneration = ULong(row, 32);
            uint pid = UInt(row, 8), kind = UInt(row, 12), hostPid = UInt(row, 24), hostKind = UInt(row, 28), flags = UInt(row, 60);
            var appState = State(UInt(row, 52)); var packageState = State(UInt(row, 56));
            uint titleChars = UInt(row, 40), appChars = UInt(row, 44), packageChars = UInt(row, 48);
            if (window == 0 || !windows.Add(window) || pid == 0 || kind is not (1 or 2) || generation == 0 || flags > 1
                || titleChars > 511 || appChars > 511 || packageChars > 511
                || flags == 0 && (hostPid != 0 || hostKind != 0 || hostGeneration != 0)
                || flags == 1 && (hostPid == 0 || hostKind is not (1 or 2) || hostGeneration == 0)) throw Invalid(Loc.T("Neplatná vazba okna na aplikaci."));
            string title = Text(payload, ref offset, titleChars), app = Text(payload, ref offset, appChars), package = Text(payload, ref offset, packageChars);
            if (appState != SampleState.Valid && app.Length != 0 || packageState != SampleState.Valid && package.Length != 0
                || appState == SampleState.Valid && app.Length == 0 || packageState == SampleState.Valid && package.Length == 0)
                throw Invalid(Loc.T("Neplatný stav metadat aplikace."));
            ProcessKey? host = flags == 1 ? new(boot, hostPid, (GenerationKind)hostKind, hostGeneration) : null;
            values[i] = new(window, new(boot, pid, (GenerationKind)kind, generation), host, title, app, package, appState, packageState);
        }
        if (offset != payload.Length) throw Invalid(Loc.T("Výsledek aplikací obsahuje data mimo rámec."));
        return result with { Applications = values };
    }
}
