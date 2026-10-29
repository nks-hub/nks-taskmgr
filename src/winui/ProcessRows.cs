using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NksTaskMgr.WinUI;

[WinRT.GeneratedWinRTExposedType]
public sealed partial class ProcessRows : ObservableCollection<ProcessRow>
{
    public event Action? Replacing;
    public event Action? Replaced;
    public void RefreshView() { Replacing?.Invoke(); Replaced?.Invoke(); }

    public void Apply(IReadOnlyList<ProcessRow> rows)
    {
        if (Count == rows.Count && this.SequenceEqual(rows)) return;
        Replacing?.Invoke();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        Replaced?.Invoke();
    }
}
