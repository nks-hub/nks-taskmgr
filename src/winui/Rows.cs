using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessRow : INotifyPropertyChanged
{
    public ProcessRow(ProcessKey key) { Key = key; PidText = key.Pid.ToString(CultureInfo.InvariantCulture); }
    public ProcessKey Key { get; }
    public string PidText { get; }
    public string Name { get; private set; } = string.Empty;
    public string DisplayName => (HasChildren ? Collapsed ? "› " : "⌄ " : "") + Name + (Ended ? " · ukončeno" : Stale ? " · zastaralé" : "");
    public uint ParentPid { get; private set; }
    public ulong Created100ns { get; private set; }
    public uint Threads { get; private set; }
    public uint Handles { get; private set; }
    public ulong WorkingBytes { get; private set; }
    public ulong PrivateBytes { get; private set; }
    public Metric Cpu { get; private set; }
    public Metric ReadIo { get; private set; }
    public Metric WriteIo { get; private set; }
    public bool Ended { get; private set; }
    public bool Stale { get; private set; }
    public int Depth { get; private set; }
    public bool HasChildren { get; private set; }
    public bool Collapsed { get; private set; }
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Note => Ended ? $"PID {PidText} · ukončeno" : $"PID {PidText} · {Threads} vláken";
    public string CpuText => Ended ? "Ukončeno" : Stale ? "Zastaralé" : Display.Metric(Cpu, " %");
    public string WorkingText => Ended ? "Ukončeno" : Stale ? "Zastaralé" : Display.Number(WorkingBytes / 1048576d, " MiB", 0);
    public string PrivateText => Ended ? "Ukončeno" : Stale ? "Zastaralé" : Display.Number(PrivateBytes / 1048576d, " MiB", 0);
    public string ReadText => Ended ? "Ukončeno" : Stale ? "Zastaralé" : Display.Metric(ReadIo, " MiB/s", 1048576);
    public string WriteText => Ended ? "Ukončeno" : Stale ? "Zastaralé" : Display.Metric(WriteIo, " MiB/s", 1048576);
    public event PropertyChangedEventHandler? PropertyChanged;

    public unsafe void Update(in ProcessInfo source, bool notify = true)
    {
        bool nameChanged = !new ReadOnlySpan<char>(source.Name.Data, checked((int)source.Name.Length)).SequenceEqual(Name.AsSpan());
        bool parentChanged = notify && ParentPid != source.ParentPid, threadsChanged = notify && Threads != source.Threads, handlesChanged = notify && Handles != source.Handles;
        bool workingChanged = notify && WorkingBytes != source.WorkingSetBytes, privateChanged = notify && PrivateBytes != source.PrivateBytes;
        bool cpuChanged = notify && !SameMetric(Cpu, source.CpuPercent), readChanged = notify && !SameMetric(ReadIo, source.ReadIoBytesPerSecond), writeChanged = notify && !SameMetric(WriteIo, source.WriteIoBytesPerSecond);
        bool statusChanged = notify && (Ended || Stale);
        if (nameChanged) Name = source.Name.Copy();
        ParentPid = source.ParentPid; Threads = source.Threads; Handles = source.Handles;
        WorkingBytes = source.WorkingSetBytes; PrivateBytes = source.PrivateBytes;
        Cpu = source.CpuPercent; ReadIo = source.ReadIoBytesPerSecond; WriteIo = source.WriteIoBytesPerSecond;
        Ended = Stale = false;
        if (!notify) return;
        if (statusChanged) { NotifyUpdated(); return; }
        if (nameChanged) { Notify(nameof(Name)); Notify(nameof(DisplayName)); }
        if (parentChanged) Notify(nameof(ParentPid));
        if (threadsChanged) { Notify(nameof(Threads)); Notify(nameof(Note)); }
        if (handlesChanged) Notify(nameof(Handles));
        if (workingChanged) { Notify(nameof(WorkingBytes)); Notify(nameof(WorkingText)); }
        if (privateChanged) { Notify(nameof(PrivateBytes)); Notify(nameof(PrivateText)); }
        if (cpuChanged) { Notify(nameof(Cpu)); Notify(nameof(CpuText)); }
        if (readChanged) { Notify(nameof(ReadIo)); Notify(nameof(ReadText)); }
        if (writeChanged) { Notify(nameof(WriteIo)); Notify(nameof(WriteText)); }
    }

    private static bool SameMetric(Metric left, Metric right) => left.Value.Equals(right.Value) && left.State == right.State && left.Source == right.Source;
    private void Notify(string property)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        NotifyOwnTableValue(property);
    }
    public void NotifyUpdated() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public void SetStatus(bool ended, bool stale, bool notify = true)
    {
        if (Ended == ended && Stale == stale) return;
        Ended = ended; Stale = stale; if (notify) NotifyUpdated();
    }

    public void SetCreatedTime(ulong value) => Created100ns = value;

    public void SetTree(int depth, bool children, bool collapsed)
    {
        if (Depth == depth && HasChildren == children && Collapsed == collapsed) return;
        Depth = depth; HasChildren = children; Collapsed = collapsed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    }
}
