using System.Text;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessDetailsView
{
    private ProcessHandleObservation? observation;
    private ProcessHandleInfo? observationHandle;
    private DateTimeOffset? observationTime;

    private void ClearObservation() { observation = null; observationHandle = null; observationTime = null; }

    private bool UpdateObjectDetail()
    {
        if (mode is not (ProcessDetailsCommand.Memory or ProcessDetailsCommand.Handles)) return false;
        var text = new StringBuilder();
        if (SelectedRow is ProcessMemoryRegion region) {
            text.Append("Základní adresa: 0x").Append(region.BaseAddress.ToString("X16")).AppendLine();
            text.Append("Konec oblasti: 0x").Append((region.BaseAddress + region.RegionSize).ToString("X16")).AppendLine();
            text.Append("Velikost: ").Append(region.RegionSize.ToString("N0", Czech)).Append(" B").AppendLine();
            text.Append("Stav: ").Append(ProcessMemoryRegion.StateText(region.State)).AppendLine();
            text.Append("Typ: ").Append(ProcessMemoryRegion.TypeText(region.Type)).AppendLine();
            text.Append("Ochrana: ").Append(region.State == 0x1000 ? Protection(region.Protection) : "Pro tento stav není určena").AppendLine();
            text.Append("Základní adresa alokace: ").Append(region.State == 0x10000 ? "Volná oblast" : "0x" + region.AllocationBase.ToString("X16")).AppendLine();
            text.Append("Původní ochrana alokace: ").Append(region.State == 0x10000 ? "Volná oblast" : Protection(region.AllocationProtection));
        } else if (SelectedHandle is { } handle) {
            text.Append("Hodnota handle: 0x").Append(handle.Value.ToString("X")).AppendLine();
            text.Append("Přístup ve snímku: 0x").Append(handle.GrantedAccess.ToString("X8")).AppendLine();
            text.Append("Index typu ve snímku: ").Append(handle.TypeIndex.ToString(Czech)).AppendLine();
            text.Append("Atributy ve snímku: 0x").Append(handle.Attributes.ToString("X8")).AppendLine().AppendLine();
            if (observation is not null && ReferenceEquals(observationHandle, handle)) {
                text.Append("Aktuálně zachycený typ: ").Append(ObjectText(observation.Type, observation.TypeState, observation.TypeNtStatus, false)).AppendLine();
                text.Append("Aktuálně zachycený název: ").Append(ObjectText(observation.Name, observation.NameState, observation.NameNtStatus, true)).AppendLine();
                text.Append("Pozorováno: ").Append(observationTime?.LocalDateTime.ToString("g", Czech)).AppendLine();
            } else text.AppendLine("Typ a název objektu zatím nebyly dotázané.");
            text.AppendLine().Append("Dotaz zachytí objekt aktuálně duplikovaný pod touto hodnotou. Po uzavření a opětovném použití handle může jít o jiný objekt než ve snímku.");
        } else text.Append("Vyberte řádek ze seznamu.");
        detail.Text = text.ToString(); return true;
    }

    private static string ObjectText(string value, SampleState state, int status, bool name)
        => state == SampleState.Valid ? value.Length == 0 ? name ? "Nepojmenovaný objekt" : "Prázdný typ" : value
            : MetricState(state, 0) + (status == 0 ? "" : " (NT 0x" + unchecked((uint)status).ToString("X8") + ")");

    private static string Protection(uint value)
    {
        string basis = (value & 0xff) switch {
            1 => "Bez přístupu", 2 => "Čtení", 4 => "Čtení / zápis", 8 => "Kopie při zápisu",
            0x10 => "Spuštění", 0x20 => "Spuštění / čtení", 0x40 => "Spuštění / čtení / zápis", 0x80 => "Spuštění / kopie při zápisu", _ => "Neuvedena"
        };
        if ((value & 0x100) != 0) basis += " · GUARD";
        if ((value & 0x200) != 0) basis += " · NOCACHE";
        if ((value & 0x400) != 0) basis += " · WRITECOMBINE";
        return basis + " (0x" + value.ToString("X8") + ")";
    }
}
