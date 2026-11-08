using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;

namespace NksTaskMgr.WinUI;

/// <summary>One row of a grouped table: a group (user session, application) with totals, or one of its processes with own values.</summary>
public sealed partial class GroupLine : INotifyPropertyChanged
{
    internal GroupLine(object group, ProcessRow? process, ProcessRow? icon)
    {
        Group = group; Process = process; IconRow = icon;
    }

    internal object Group { get; }
    internal ProcessRow? Process { get; }
    internal bool IsGroup => Process is null;
    internal Action<GroupLine>? Toggle { get; set; }
    public ProcessRow? IconRow { get; }
    public Visibility ProcessIconVisibility => IconRow is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UserIconVisibility => IconRow is null ? Visibility.Visible : Visibility.Collapsed;
    public Thickness Indent => new(IsGroup ? 0 : 20 + Depth * 14, 0, 0, 0);
    internal int Depth { get; private set; }

    internal void SetDepth(int depth)
    {
        if (Depth == depth) return;
        Depth = depth; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indent)));
    }
    public FontWeight TitleWeight => IsGroup ? FontWeights.SemiBold : FontWeights.Normal;

    public string Chevron { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string State { get; private set; } = "";
    public string Cpu { get; private set; } = "";
    public string Memory { get; private set; } = "";
    public string MemoryTip { get; private set; } = "";
    public string Disk { get; private set; } = "";
    public string Network { get; private set; } = "";
    public string Tip { get; private set; } = "";
    internal string Accessible { get; private set; } = "";
    /// <summary>The list container currently showing this line; its screen reader name follows state changes.</summary>
    internal ListViewItem? Container { get; set; }
    internal string SortName { get; private set; } = "";
    internal double CpuValue { get; private set; }
    internal double MemoryValue { get; private set; }
    internal double DiskValue { get; private set; }
    internal double NetworkValue { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Visibility ChevronVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChevronSpacerVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
    public string ChevronName => Chevron == "⌄" ? Loc.T("Sbalit") : Loc.T("Rozbalit");

    public void ChevronClick(object sender, RoutedEventArgs args) { if (IsGroup) Toggle?.Invoke(this); }

    internal void Set(string chevron, string title, string state, string cpu, string memory, string memoryTip, string disk, string network, string tip, string accessible)
    {
        if (Chevron != chevron) { Chevron = chevron; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronName))); }
        Update(Title, title, value => Title = value, nameof(Title));
        Update(State, state, value => State = value, nameof(State));
        Update(Cpu, cpu, value => Cpu = value, nameof(Cpu));
        Update(Memory, memory, value => Memory = value, nameof(Memory));
        Update(MemoryTip, memoryTip, value => MemoryTip = value, nameof(MemoryTip));
        Update(Disk, disk, value => Disk = value, nameof(Disk));
        Update(Network, network, value => Network = value, nameof(Network));
        Update(Tip, tip, value => Tip = value, nameof(Tip));
        if (Accessible == accessible) return;
        Accessible = accessible;
        if (Container is { } item && ReferenceEquals(item.Content, this)) AutomationProperties.SetName(item, accessible);
    }

    internal void SetSort(string name, double cpu, double memory, double disk, double network)
    {
        SortName = name; CpuValue = cpu; MemoryValue = memory; DiskValue = disk; NetworkValue = network;
    }

    // Writing only changed texts keeps the per-second refresh from re-laying out unchanged cells.
    private void Update(string current, string value, Action<string> assign, string property)
    {
        if (current == value) return;
        assign(value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>Orders siblings by the clicked column; numbers default to the largest first like Task Manager.</summary>
    internal static int Compare(GroupLine a, GroupLine b, int column, bool descending)
    {
        int order = column switch {
            1 => string.Compare(a.State, b.State, StringComparison.CurrentCultureIgnoreCase),
            2 => a.CpuValue.CompareTo(b.CpuValue), 3 => a.MemoryValue.CompareTo(b.MemoryValue),
            4 => a.DiskValue.CompareTo(b.DiskValue), 5 => a.NetworkValue.CompareTo(b.NetworkValue),
            _ => string.Compare(a.SortName, b.SortName, StringComparison.CurrentCultureIgnoreCase)
        };
        if (descending) order = -order;
        return order != 0 ? order : string.Compare(a.SortName, b.SortName, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>Rows bound to a grouped ListView; small changes move or insert rows, large ones are published as one reset.</summary>
[WinRT.GeneratedWinRTExposedType]
public sealed partial class GroupLines : ObservableCollection<GroupLine>
{
    private bool bulk;

    internal void Sync(IReadOnlyList<GroupLine> wanted)
    {
        if (Count == wanted.Count && this.SequenceEqual(wanted)) return;
        var keep = new HashSet<GroupLine>(wanted);
        int changes = this.Count(line => !keep.Contains(line)) + Math.Abs(wanted.Count - Count);
        for (int i = 0; i < Math.Min(Count, wanted.Count) && changes <= 64; ++i) if (!ReferenceEquals(this[i], wanted[i])) ++changes;
        if (changes > 64) {
            bulk = true;
            try { Clear(); foreach (var line in wanted) Add(line); }
            finally { bulk = false; }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }
        for (int i = Count - 1; i >= 0; --i) if (!keep.Contains(this[i])) RemoveAt(i);
        for (int i = 0; i < wanted.Count; ++i) {
            if (i < Count && ReferenceEquals(this[i], wanted[i])) continue;
            int at = IndexOf(wanted[i]);
            if (at >= 0) Move(at, i); else Insert(i, wanted[i]);
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e) { if (!bulk) base.OnCollectionChanged(e); }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { if (!bulk) base.OnPropertyChanged(e); }
}
