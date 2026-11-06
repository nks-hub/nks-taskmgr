using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public enum ServiceFilter { All, Running, Stopped, Other }

public sealed record ServiceConfiguration(uint Error, uint StartType, uint DelayedError, bool Delayed,
    string Account, string Group, string Command, string[] Dependencies)
{
    public static readonly ServiceConfiguration Empty = new(232, uint.MaxValue, 0, false, "", "", "", []);
    public static unsafe ServiceConfiguration Copy(in ServiceConfigInfo source) => new(source.Error, source.StartType, source.DelayedError, source.Delayed != 0,
        source.Account.Copy(), source.Group.Copy(), source.Command.Copy(), Strings(source.Dependencies, source.DependencyCount));
    internal static unsafe string[] Strings(String16* source, uint count)
    {
        if (count > 16_384) throw new NativeMonitorException(NativeResult.AbiMismatch);
        var result = new string[count];
        for (uint i = 0; i < count; ++i) result[i] = source[i].Copy();
        return result;
    }
    public unsafe bool Matches(in ServiceConfigInfo source)
    {
        if (Error != source.Error || StartType != source.StartType || DelayedError != source.DelayedError || Delayed != (source.Delayed != 0)
            || !Same(Account, source.Account) || !Same(Group, source.Group) || !Same(Command, source.Command)
            || Dependencies.Length != source.DependencyCount) return false;
        for (int i = 0; i < Dependencies.Length; ++i) if (!Same(Dependencies[i], source.Dependencies[i])) return false;
        return true;
    }
    internal static unsafe bool Same(string value, String16 source) => value.AsSpan().SequenceEqual(new ReadOnlySpan<char>(source.Data, checked((int)source.Length)));
    public string Startup => Error != 0 ? ServiceText.Error(Error) : StartType switch {
        0 => Loc.T("Při bootu"), 1 => Loc.T("Systém"), 2 => Delayed ? Loc.T("Automaticky (odloženě)") : DelayedError != 0 ? Loc.T("Automaticky (?)") : Loc.T("Automaticky"),
        3 => Loc.T("Ručně"), 4 => Loc.T("Zakázáno"), _ => Loc.T("Neznámé")
    };
}

public static class ServiceText
{
    public static string Count(int value) => value + " " + MainWindow.PluralForm(value, Loc.T("služba"), Loc.T("služby"), Loc.T("služeb"));
    public static string State(uint value) => value switch {
        1 => Loc.T("Zastaveno"), 2 => Loc.T("Spouští se"), 3 => Loc.T("Zastavuje se"), 4 => Loc.T("Běží"), 5 => Loc.T("Obnovuje se"), 6 => Loc.T("Pozastavuje se"), 7 => Loc.T("Pozastaveno"), _ => Loc.F("Neznámý stav {0}", value)
    };
    public static string Error(uint value) => value == 5 ? Loc.T("Chybí oprávnění (5)") : Loc.F("Nedostupné ({0})", value);
}

public sealed partial class ServiceRow(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Title { get; private set; } = name;
    public uint State { get; private set; }
    public uint Pid { get; private set; }
    public int ReportedHostCount { get; private set; }
    public ServiceConfiguration Config { get; private set; } = ServiceConfiguration.Empty;
    public string StateText => ServiceText.State(State);
    public override string ToString() => Title + " (" + Name + "), " + StateText;
    public string PidText => Pid == 0 ? "—" : Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string HostCountText => ReportedHostCount > 1 ? ServiceText.Count(ReportedHostCount) : "";
    // An empty second line would still take its height and push the PID above the row centre.
    public Visibility HostCountVisibility => ReportedHostCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string HostSummary => Pid == 0 ? Loc.T("PID není hlášen.") : Loc.F("Hlášený PID {0} · {1}", PidText, ServiceText.Count(ReportedHostCount));
    public string HostPeerDescription => StateText + " · " + Account;
    public string Startup => Config.Startup;
    public string Account => Config.Error == 0 ? Config.Account : ServiceText.Error(Config.Error);
    private SolidColorBrush? stateBrush;
    public SolidColorBrush StateBrush => stateBrush ??= Display.Brush(State == 4 ? 0x83bca0u : 0x95a4adu);
    public event PropertyChangedEventHandler? PropertyChanged;
    public unsafe bool Update(in ServiceInfo source)
    {
        bool changed = State != source.State || Pid != source.Pid || !ServiceConfiguration.Same(Title, source.Title) || !Config.Matches(source.Config);
        if (!changed) return false;
        if (State != source.State) stateBrush = null;
        Title = source.Title.Copy(); State = source.State; Pid = source.Pid;
        if (!Config.Matches(source.Config)) Config = ServiceConfiguration.Copy(source.Config);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); return true;
    }
    public void SetReportedHostCount(int count)
    {
        if (ReportedHostCount == count) return;
        ReportedHostCount = count;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReportedHostCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HostCountText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HostCountVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HostSummary)));
    }
    public bool Matches(ServiceFilter filter, string query) => (filter switch {
        ServiceFilter.Running => State == 4, ServiceFilter.Stopped => State == 1, ServiceFilter.Other => State != 1 && State != 4, _ => true
    }) && (query.Length == 0 || Name.Contains(query, StringComparison.OrdinalIgnoreCase) || Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Config.Account.Contains(query, StringComparison.OrdinalIgnoreCase) || (Pid != 0 && PidText.Contains(query, StringComparison.Ordinal)));
}

public sealed record ServiceDetailData(long Time, string Name, string Description, ServiceConfiguration Config,
    uint StatusError, uint DescriptionError, uint DependentsError, uint HostError, uint Pid, uint State, ulong HostCreated, string[] Dependents)
{
    public ProcessIdentityInfo? HostCreationIdentity { get; init; }
    public ProcessIdentityInfo? HostSequenceIdentity { get; init; }
    public bool MatchesHost(ServiceRow service, ProcessRow process) => StringComparer.OrdinalIgnoreCase.Equals(service.Name, Name)
        && service.State == 4 && State == 4 && service.Pid != 0 && service.Pid == Pid && process.Key.Pid == Pid
        && StatusError == 0 && HostError == 0 && HostCreated != 0 && !process.Ended
        && MatchesIdentity(process.Key);
    private bool MatchesIdentity(ProcessKey key)
    {
        var identity = key.Kind switch {
            GenerationKind.CreationTime => HostCreationIdentity,
            GenerationKind.SequenceNumber => HostSequenceIdentity,
            _ => null
        };
        return identity is { State: SampleState.Valid } host && !host.Boot.IsEmpty && host.Generation != 0
            && host.Boot == key.Boot && host.Pid == key.Pid && host.Kind == key.Kind && host.Generation == key.Generation;
    }
    public static unsafe ServiceDetailData Copy(in ServiceDetailInfo value) => new(value.MeasuredAtNs, value.Name.Copy(), value.Description.Copy(),
        ServiceConfiguration.Copy(value.Config), value.StatusError, value.DescriptionError, value.DependentsError, value.HostError,
        value.Pid, value.State, value.HostCreated100ns, ServiceConfiguration.Strings(value.Dependents, value.DependentCount));
}
