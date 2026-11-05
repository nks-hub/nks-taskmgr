using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ApplicationGroupRow
{
    public ProcessRow? IconRow { get; private set; }
    public string TitleTooltip { get; private set; } = "";
    public string CountText { get; private set; } = "";
    public string CpuText { get; private set; } = "";
    public string CpuTooltip { get; private set; } = "";
    internal double CpuValue { get; private set; }
    internal double MemoryValue { get; private set; }
    internal bool HasCpu { get; private set; }
    internal bool HasMemory { get; private set; }

    private bool UpdatePresentation(bool partial)
    {
        var live = Members.Where(row => !row.Stale && !row.Ended).ToArray();
        var known = live.Where(row => row.Cpu.State == SampleState.Valid && double.IsFinite(row.Cpu.Value) && row.Cpu.Value >= 0).ToArray();
        CpuValue = known.Sum(row => row.Cpu.Value); HasCpu = known.Length > 0 && double.IsFinite(CpuValue);
        MemoryValue = live.Sum(row => (double)row.WorkingBytes); HasMemory = live.Length > 0;
        string windows = Windows.Length + (Windows.Length == 1 ? " okno" : Windows.Length is >= 2 and <= 4 ? " okna" : " oken");
        string counts = Members.Length + (Members.Length == 1 ? " proces" : Members.Length is >= 2 and <= 4 ? " procesy" : " procesů");
        // ≥ marks a known part of an incomplete sum, as in the process tree and Users.
        string cpu = (!HasCpu ? "—" : (partial || known.Length != Members.Length ? "≥ " : "") + Display.Number(CpuValue, " %", 1))
            + (HasMemory ? " · " + Display.Bytes((ulong)MemoryValue) : "");
        string tooltip = windows + ". " + (HasCpu ? "CPU je součet z " + known.Length + " / " + Members.Length + " procesů." : "Aktuální CPU této skupiny není dostupné.")
            + (HasMemory ? " Paměť je součet pracovních sad." : "");
        if (partial) tooltip += " Přehled aplikací je částečný nebo zastaralý.";
        else if (known.Length != Members.Length) tooltip += " U ostatních procesů chybí aktuální měření.";
        string summary = counts + " · " + windows + " · CPU " + cpu;
        bool changed = CountText != counts || CpuText != cpu || CpuTooltip != tooltip || Summary != summary;
        CountText = counts; CpuText = cpu; CpuTooltip = tooltip; Summary = summary;
        return changed;
    }
}

internal enum ApplicationGroupSort { Name, Cpu, Memory, Processes }

internal sealed partial class ApplicationGroupModel
{
    internal ApplicationGroupSort Sort { get; private set; }
    internal bool SortDescending { get; private set; }
    internal event Action? ViewChanging;
    internal event Action? ViewChanged;

    internal void SetSort(ApplicationGroupSort value, bool descending)
    {
        if (Sort == value && SortDescending == descending) return;
        Sort = value; SortDescending = descending; ApplyVisible(Ordered(Visible));
    }

    private ApplicationGroupRow[] Ordered(IEnumerable<ApplicationGroupRow> source)
    {
        var rows = source.ToArray();
        Array.Sort(rows, (a, b) => {
            if (Sort == ApplicationGroupSort.Cpu && a.HasCpu != b.HasCpu) return a.HasCpu ? -1 : 1;
            if (Sort == ApplicationGroupSort.Memory && a.HasMemory != b.HasMemory) return a.HasMemory ? -1 : 1;
            int result = Sort switch {
                ApplicationGroupSort.Cpu => a.CpuValue.CompareTo(b.CpuValue), ApplicationGroupSort.Memory => a.MemoryValue.CompareTo(b.MemoryValue),
                ApplicationGroupSort.Processes => a.Members.Length.CompareTo(b.Members.Length), _ => StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title)
            };
            if (result != 0) return SortDescending ? -result : result;
            result = a.Key.Pid.CompareTo(b.Key.Pid); if (result != 0) return result;
            result = a.Key.Kind.CompareTo(b.Key.Kind); if (result != 0) return result;
            result = a.Key.Generation.CompareTo(b.Key.Generation); if (result != 0) return result;
            result = a.Key.Boot.Low.CompareTo(b.Key.Boot.Low);
            return result != 0 ? result : a.Key.Boot.High.CompareTo(b.Key.Boot.High);
        });
        return rows;
    }

    private void ApplyVisible(ApplicationGroupRow[] rows)
    {
        if (Visible.SequenceEqual(rows)) return;
        bool previous = Updating; Updating = true;
        try {
            ViewChanging?.Invoke();
            var wanted = rows.ToHashSet();
            for (int i = Visible.Count - 1; i >= 0; --i) if (!wanted.Contains(Visible[i])) Visible.RemoveAt(i);
            for (int i = 0; i < rows.Length; ++i) {
                if (i < Visible.Count && ReferenceEquals(Visible[i], rows[i])) continue;
                int found = Visible.IndexOf(rows[i]);
                if (found >= 0) Visible.Move(found, i); else Visible.Insert(i, rows[i]);
            }
        } finally {
            try { ViewChanged?.Invoke(); }
            finally { Updating = previous; }
        }
    }
}
