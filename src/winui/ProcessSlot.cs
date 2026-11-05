using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NksTaskMgr.WinUI;

/// <summary>Slot list bound to the process ListView; large membership changes are published as one reset instead of one notification per slot.</summary>
[WinRT.GeneratedWinRTExposedType]
public sealed partial class ProcessSlots : ObservableCollection<ProcessSlot>
{
    internal const int BulkThreshold = 64;
    private bool bulk;

    /// <summary>Suppresses change notifications until <see cref="EndBulk"/> when the count will change by more than <see cref="BulkThreshold"/>.</summary>
    internal void BeginBulk(int targetCount) => bulk = Math.Abs(targetCount - Count) > BulkThreshold;

    internal void EndBulk()
    {
        if (!bulk) return;
        bulk = false;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        if (spare.Count > 4096) spare.RemoveRange(4096, spare.Count - 4096);
    }

    private readonly List<ProcessSlot> spare = [];

    /// <summary>Drops slots past <paramref name="count"/>; inside a bulk update they are cut in one step and kept for <see cref="Append"/>.</summary>
    internal void TrimTo(int count)
    {
        if (count >= Count) return;
        if (!bulk) { while (Count > count) RemoveAt(Count - 1); return; }
        var items = (List<ProcessSlot>)Items;
        spare.AddRange(items.GetRange(count, items.Count - count));
        items.RemoveRange(count, items.Count - count);
    }

    /// <summary>Appends a slot for <paramref name="row"/>, reusing one trimmed during a bulk update when available.</summary>
    internal ProcessSlot Append(ProcessRow row, Func<ProcessRow, ProcessSlot> create)
    {
        ProcessSlot slot;
        if (bulk && spare.Count > 0) { slot = spare[^1]; spare.RemoveAt(spare.Count - 1); slot.Assign(row); }
        else slot = create(row);
        Add(slot);
        return slot;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e) { if (!bulk) base.OnCollectionChanged(e); }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { if (!bulk) base.OnPropertyChanged(e); }
}

public sealed partial class ProcessSlot(ProcessRow row) : INotifyPropertyChanged
{
    public ProcessRow Row { get; private set; } = row;
    public string AccessibleName => Row.Name + ", PID " + Row.PidText;
    public override string ToString() => AccessibleName;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Assign(ProcessRow value)
    {
        if (ReferenceEquals(Row, value)) return;
        Row = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }
}
