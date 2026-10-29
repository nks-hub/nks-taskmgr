using System.ComponentModel;

namespace NksTaskMgr.WinUI;

public sealed partial class ProcessSlot(ProcessRow row) : INotifyPropertyChanged
{
    public ProcessRow Row { get; private set; } = row;
    public string AccessibleName => Row.Name + ", PID " + Row.PidText;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Assign(ProcessRow value)
    {
        if (ReferenceEquals(Row, value)) return;
        Row = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Row)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }
}
