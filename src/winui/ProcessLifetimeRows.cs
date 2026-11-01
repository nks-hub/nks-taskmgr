using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessEventRow
{
    internal ProcessLifetimeEntry? Lifetime { get; }
    internal int Mode => Lifetime is not null ? 2 : Traffic is not null ? 1 : 0;
    internal (ulong Epoch, ulong Id) SelectionIdentity => Lifetime is { } life ? (life.Capture, life.Id)
        : Traffic is { } traffic ? (traffic.AttributionEpoch, traffic.SampleId) : (0, Source.Sequence);
    internal ProcessEventRow(ProcessLifetimeEntry entry)
    {
        Lifetime = entry;
        var observation = (entry.Start ?? entry.Stop)!.Value;
        Source = observation.Event; frequency = observation.Frequency; boot = observation.Boot;
        StartedText = "Vznik: " + ProcessLifetimeText.Time(entry.Start);
        EndedText = "Konec: " + ProcessLifetimeText.Time(entry.Stop);
        DurationText = entry.DurationSeconds is { } duration ? duration.ToString("N3", CultureInfo.GetCultureInfo("cs-CZ")) + " s" : "Délka neurčena";
        TimeText = StartedText + " · " + EndedText;
        KindText = ProcessLifetimeText.State(entry);
        string name = entry.Start?.Event.ImageName ?? "";
        if (name.Length == 0) name = entry.Stop?.Event.ImageName ?? "";
        NameText = name.Length == 0 ? "Název není dostupný" : GlobalObjectSearchText.Flat(name, 256);
        TargetText = Source.Pid == 0 ? "PID nelze určit" : "PID " + Source.Pid.ToString(CultureInfo.InvariantCulture);
        IdentityText = (entry.Key is not null ? "Boot + PID + CreateTime ze zdroje" : "Neověřený samostatný záznam")
            + ProcessLifetimeText.Issues(entry);
        EstimatedBytes = 2048 + 2 * ((entry.Start?.Event.ImageName.Length ?? 0) + (entry.Stop?.Event.ImageName.Length ?? 0)
            + NameText.Length + TargetText.Length + TimeText.Length + KindText.Length + IdentityText.Length + StartedText.Length + EndedText.Length + DurationText.Length);
    }
    internal bool LifetimeMatches(int filter) => Lifetime is { } life && (filter switch {
        0 => true, 1 => life.Paired, 2 => life.Start is not null && life.Stop is null, 3 => life.Start is null && life.Stop is not null,
        4 => life.Key is null, 5 => life.Issues.HasFlag(ProcessLifetimeIssues.Conflict),
        6 => life.Issues.HasFlag(ProcessLifetimeIssues.Late), 7 => life.Issues.HasFlag(ProcessLifetimeIssues.Gap), _ => false
    });
    public string StartedText { get; } = "";
    public string EndedText { get; } = "";
    public string DurationText { get; } = "";
}
