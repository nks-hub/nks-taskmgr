using Microsoft.UI.Xaml;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessDetailsTemplates : ResourceDictionary
{
    public ProcessDetailsTemplates() { InitializeComponent(); }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProcessDetailsRow
{
    internal object Source { get; }
    private string? text;
    public string Text => text ??= Source.ToString()!;
    public override string ToString() => Text;
    internal ProcessDetailsRow(object source) { Source = source; }

    // Table columns per row kind; the headers above the list come from Headers() for the same kind.
    public string First => Source switch {
        ProcessThreadInfo thread => "TID " + thread.Id, ProcessModuleInfo module => module.Name.Length == 0 ? Loc.T("(název nedostupný)") : module.Name,
        ProcessMemoryRegion region => "0x" + region.BaseAddress.ToString("X16"), ProcessHandleInfo handle => "0x" + handle.Value.ToString("X"), _ => Text };
    public string Second => Source switch {
        ProcessThreadInfo thread => thread.BasePriority + (thread.PriorityState == NksTaskMgr.Native.SampleState.Valid ? " (" + thread.RelativePriority.ToString("+0;-0;0") + ")" : ""),
        ProcessModuleInfo module => Display.Bytes(module.ImageSize), ProcessMemoryRegion region => Display.Bytes(region.RegionSize),
        ProcessHandleInfo handle => ProcessFacts.ObjectTypeName(handle.TypeIndex), _ => "" };
    public string Third => Source switch {
        ProcessThreadInfo thread => thread.TimesState == NksTaskMgr.Native.SampleState.Valid ? Display.Number((thread.Kernel100ns + thread.User100ns) / 10_000_000d, " s", 3) : "—",
        ProcessModuleInfo module => "0x" + module.BaseAddress.ToString("X"), ProcessMemoryRegion region => ProcessMemoryRegion.StateText(region.State) + " · " + ProcessMemoryRegion.TypeText(region.Type),
        ProcessHandleInfo handle => "0x" + handle.GrantedAccess.ToString("X8"), _ => "" };
    public string Fourth => Source switch {
        ProcessThreadInfo thread => thread.TimesState == NksTaskMgr.Native.SampleState.Valid && thread.Created100ns is > 0 and <= long.MaxValue
            ? DateTime.FromFileTimeUtc((long)thread.Created100ns).ToLocalTime().ToString(Loc.Language == "cs" ? "d. M. HH:mm:ss" : "G", Loc.Culture) : "—",
        ProcessModuleInfo module => module.Path, ProcessMemoryRegion region => Loc.F("ochrana 0x{0}", region.Protection.ToString("X")), _ => "" };

    internal static string[] Headers(ProcessDetailsCommand mode) => mode switch {
        ProcessDetailsCommand.Threads => [Loc.T("Vlákno"), Loc.T("Priorita"), Loc.T("Čas CPU"), Loc.T("Spuštěno")],
        ProcessDetailsCommand.Modules => [Loc.T("Modul"), Loc.T("Velikost"), Loc.T("Adresa"), Loc.T("Cesta")],
        ProcessDetailsCommand.Memory => [Loc.T("Adresa"), Loc.T("Velikost"), Loc.T("Stav a typ"), Loc.T("Ochrana")],
        ProcessDetailsCommand.Handles => [Loc.T("Handle"), Loc.T("Typ objektu"), Loc.T("Přístup"), ""],
        _ => ["", "", "", ""]
    };
}
