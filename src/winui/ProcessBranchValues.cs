using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public enum ProcessTableValueMode { Own, ProcessWithDescendants }

/// <param name="Partial">A known value that is itself only a lower bound.</param>
internal readonly record struct ProcessBranchMetric(double Value, int Known, bool Overflow, bool Partial = false)
{
    internal static ProcessBranchMetric From(Metric metric, bool current) => current && metric.State == SampleState.Valid
        && double.IsFinite(metric.Value) && metric.Value >= 0 ? new(metric.Value, 1, false) : default;
    internal ProcessBranchMetric Add(ProcessBranchMetric other)
    {
        double value = Value + other.Value;
        bool overflow = Overflow || other.Overflow || !double.IsFinite(value);
        return new(overflow ? 0 : value, Known + other.Known, overflow, Partial || other.Partial);
    }
    internal double SortValue => Known == 0 || Overflow ? double.NegativeInfinity : Value;
    internal string Text(int members, bool incomplete, string unit, double divisor = 1) => Overflow ? "Přetečení součtu"
        : Known == 0 ? "Měření nedostupné" : (Known != members || incomplete || Partial ? "≥ " : "") + Display.Number(Value / divisor, unit);
    /// <summary>TCP/UDP bytes per second in megabits like the native Task Manager; unavailable without a running ETW capture.</summary>
    internal string NetworkText(int members, bool incomplete) => Known == 0 && !Overflow ? "—" : Text(members, incomplete, " Mb/s", 125000);
}

internal readonly record struct ProcessBranchBytes(ulong Value, int Known, bool Overflow)
{
    internal ProcessBranchBytes Add(ProcessBranchBytes other)
    {
        bool overflow = Overflow || other.Overflow || ulong.MaxValue - Value < other.Value;
        return new(overflow ? 0 : Value + other.Value, Known + other.Known, overflow);
    }
    internal double SortValue => Known == 0 || Overflow ? double.NegativeInfinity : Value;
    internal string Text(int members, bool incomplete) => Overflow ? "Přetečení součtu" : Known == 0 ? "Měření nedostupné"
        : (Known != members || incomplete ? "≥ " : "") + Display.Number(Value / 1048576d, " MiB", 0);
}

internal readonly record struct ProcessBranchTotal(int Members, bool Incomplete, ProcessBranchMetric Cpu,
    ProcessBranchBytes Working, ProcessBranchBytes Private, ProcessBranchMetric Read, ProcessBranchMetric Write, ProcessBranchMetric Network)
{
    internal static ProcessBranchTotal Own(ProcessRow row, bool incomplete)
    {
        bool current = ProcessTree.HasVerifiedIdentity(row) && !row.Stale;
        return new(1, incomplete, ProcessBranchMetric.From(row.Cpu, current),
            current ? new(row.WorkingBytes, 1, false) : default, current ? new(row.PrivateBytes, 1, false) : default,
            ProcessBranchMetric.From(row.ReadIo, current), ProcessBranchMetric.From(row.WriteIo, current), current ? row.Network : default);
    }
    internal ProcessBranchTotal Add(ProcessBranchTotal other) => new(Members + other.Members, Incomplete || other.Incomplete,
        Cpu.Add(other.Cpu), Working.Add(other.Working), Private.Add(other.Private), Read.Add(other.Read), Write.Add(other.Write), Network.Add(other.Network));
    internal string Description => $"Proces a ověření potomci: {Members} procesů. "
        + (Incomplete ? "Neúplné členství: některé vztahy nebo identity nelze ověřit.\n" : "\n")
        + $"Známé aktuální vzorky: CPU {Cpu.Known}/{Members}, RAM {Working.Known}/{Members}, privátní paměť {Private.Known}/{Members}, "
        + $"čtení {Read.Known}/{Members}, zápis {Write.Known}/{Members}, síť {Network.Known}/{Members}.\n"
        + "≥ označuje známou část neúplného součtu. Sdílené stránky RAM mohou být započteny opakovaně."
        + (Cpu.Overflow || Working.Overflow || Private.Overflow || Read.Overflow || Write.Overflow || Network.Overflow
            ? "\nPřetečený součet je nedostupný." : "");
}

public sealed partial class ProcessRow
{
    private bool tableBranchValues;
    internal ProcessBranchTotal BranchTotal { get; private set; }
    public string TableCpuText => !tableBranchValues || Ended ? CpuText : Stale && BranchTotal.Cpu.Known == 0 ? "Zastaralé" : BranchTotal.Cpu.Text(BranchTotal.Members, BranchTotal.Incomplete, " %");
    public string TableWorkingText => !tableBranchValues || Ended ? WorkingText : Stale && BranchTotal.Working.Known == 0 ? "Zastaralé" : BranchTotal.Working.Text(BranchTotal.Members, BranchTotal.Incomplete);
    public string TablePrivateText => !tableBranchValues || Ended ? PrivateText : Stale && BranchTotal.Private.Known == 0 ? "Zastaralé" : BranchTotal.Private.Text(BranchTotal.Members, BranchTotal.Incomplete);
    public string TableReadText => !tableBranchValues || Ended ? ReadText : Stale && BranchTotal.Read.Known == 0 ? "Zastaralé" : BranchTotal.Read.Text(BranchTotal.Members, BranchTotal.Incomplete, " MiB/s", 1048576);
    public string TableWriteText => !tableBranchValues || Ended ? WriteText : Stale && BranchTotal.Write.Known == 0 ? "Zastaralé" : BranchTotal.Write.Text(BranchTotal.Members, BranchTotal.Incomplete, " MiB/s", 1048576);
    public string TableNetworkText => !tableBranchValues || Ended ? NetworkText : Stale && BranchTotal.Network.Known == 0 ? "Zastaralé" : BranchTotal.Network.NetworkText(BranchTotal.Members, BranchTotal.Incomplete);
    public string TableMetricsDescription => Ended ? "Proces byl ukončen; hodnoty již nejsou aktuální." : tableBranchValues
        ? (Stale ? "Procesový inventář je zastaralý.\n" : "") + BranchTotal.Description : $"Vlastní hodnoty PID {PidText}, bez potomků." + networkTotal;

    internal ProcessBranchMetric Network { get; private set; }
    public string NetworkText => Ended ? "Ukončeno" : Stale ? "Zastaralé" : Network.NetworkText(1, false);
    private string networkTotal = "";

    /// <summary>Sets this generation's TCP/UDP rate from the event window's capture; null when it is not measured.</summary>
    internal bool SetNetwork(ProcessNetworkRate? rate, bool partial, bool totalPartial, bool notify)
    {
        var value = rate is { } current ? new ProcessBranchMetric(current.PerSecond, 1, false, partial) : default;
        string total = rate is { } known ? "\nSíť od začátku záznamu ETW: " + (totalPartial ? "≥ " : "") + Display.Bytes(known.TotalBytes) : "";
        if (value == Network && total == networkTotal) return false;
        string text = NetworkText, description = TableMetricsDescription;
        Network = value; networkTotal = total;
        if (notify && text != NetworkText) Notify(nameof(NetworkText));
        if (notify && description != TableMetricsDescription) Notify(nameof(TableMetricsDescription));
        return true;
    }

    internal void SetBranchValues(ProcessBranchTotal total, bool show, bool notify)
    {
        if (BranchTotal == total && tableBranchValues == show) return;
        string[]? before = notify && PropertyChanged is not null ? TableTexts() : null;
        BranchTotal = total; tableBranchValues = show;
        if (before is null) return;
        var after = TableTexts();
        for (int i = 0; i < after.Length; ++i) if (before[i] != after[i]) Notify(TableProperties[i]);
    }

    private static readonly string[] TableProperties = [nameof(TableCpuText), nameof(TableWorkingText), nameof(TablePrivateText),
        nameof(TableReadText), nameof(TableWriteText), nameof(TableNetworkText), nameof(TableMetricsDescription)];
    private string[] TableTexts() => [TableCpuText, TableWorkingText, TablePrivateText, TableReadText, TableWriteText, TableNetworkText, TableMetricsDescription];

    private void NotifyOwnTableValue(string property)
    {
        if (tableBranchValues) return;
        string? tableProperty = property switch {
            nameof(CpuText) => nameof(TableCpuText), nameof(WorkingText) => nameof(TableWorkingText),
            nameof(PrivateText) => nameof(TablePrivateText), nameof(ReadText) => nameof(TableReadText),
            nameof(WriteText) => nameof(TableWriteText), nameof(NetworkText) => nameof(TableNetworkText), _ => null
        };
        if (tableProperty is not null) Notify(tableProperty);
    }
}
