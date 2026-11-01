using System.Globalization;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed record GlobalObjectSearchQuery(string Name, string Type)
{
    internal bool IsValid => Name.Trim().Length >= 2 || Type.Trim().Length >= 2;
}

public sealed class GlobalObjectSearchResult
{
    internal ProcessKey Owner { get; }
    internal string OwnerName { get; }
    internal ProcessHandleInfo Handle { get; }
    internal ProcessHandleObservation Observation { get; }
    internal string DisplayName { get; }
    internal DateTimeOffset ReceivedAt { get; }
    internal GlobalObjectSearchResult(ProcessKey owner, string ownerName, ProcessHandleInfo handle,
        ProcessHandleObservation observation, string displayName, DateTimeOffset receivedAt)
    {
        Owner = owner; OwnerName = ownerName; Handle = handle; Observation = observation; DisplayName = displayName; ReceivedAt = receivedAt;
    }
    public string OwnerText => GlobalObjectSearchText.Flat(OwnerName.Length == 0 ? "Proces" : OwnerName, 100) + " · PID " + Owner.Pid;
    public string HandleText => "0x" + Handle.Value.ToString("X", CultureInfo.InvariantCulture);
    public string TypeText => Observation.TypeState == SampleState.Valid ? Observation.Type.Length == 0 ? "(prázdný typ)" : Observation.Type
        : "Typ: " + GlobalObjectSearchText.State(Observation.TypeState);
    public string NameText => Observation.NameState != SampleState.Valid ? "Název: " + GlobalObjectSearchText.State(Observation.NameState)
        : DisplayName.Length == 0 ? "Bez názvu" : GlobalObjectSearchText.Flat(DisplayName, 200);
    public string ObjectText => GlobalObjectSearchText.Flat(TypeText, 80) + " · " + NameText;
}

internal sealed record GlobalObjectSearchProgress(int TotalOwners, int ExaminedOwners, int SearchedOwners, int UnavailableOwners,
    int EndedOwners, int LimitedOwners, int ObjectsExamined, int ObjectsSkipped, int Matches, bool Partial,
    bool Finished, bool Cancelled, string CurrentOwner, string Note);

internal sealed record GlobalObjectSearchBatch(GlobalObjectSearchResult[] Results, GlobalObjectSearchProgress Progress);

internal static class GlobalObjectSearchText
{
    internal static string Flat(string value, int maximum)
    {
        string flat = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        int length = Math.Min(maximum, flat.Length);
        if (length < flat.Length && length > 0 && char.IsHighSurrogate(flat[length - 1])) --length;
        return length < flat.Length ? flat[..length] + "…" : flat;
    }
    internal static string State(SampleState state) => state switch {
        SampleState.Valid => "Dostupné", SampleState.First => "První vzorek", SampleState.AccessDenied => "Chybí oprávnění",
        SampleState.Unsupported => "Nepodporováno", SampleState.Stale => "Zastaralé", SampleState.Error => "Chyba",
        SampleState.Gap => "Mezera", _ => "Nedostupné"
    };
}
