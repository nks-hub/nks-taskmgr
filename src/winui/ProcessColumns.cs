using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed class ProcessColumn(int id, string title, double width, double minimum)
{
    public int Id { get; } = id;
    public string Title { get; } = title;
    public double Width { get; internal set; } = width;
    public double Minimum { get; } = minimum;
    public bool Visible { get; internal set; } = true;
}

public sealed class ProcessColumns
{
    private readonly List<ProcessColumn> columns = [
        new(0, "Proces", 260, 210), new(1, "PID", 70, 60), new(2, "CPU", 86, 70),
        new(3, "Pracovní sada", 120, 100), new(4, "Soukromá paměť", 126, 110),
        new(5, "Čtení I/O", 108, 90), new(6, "Zápis I/O", 108, 90)
    ];
    private bool fillName = true;
    private double viewport;
    public IReadOnlyList<ProcessColumn> Items => columns;
    public double MinimumWidth => columns.Where(column => column.Visible).Sum(column => column.Width) + 32;
    public event Action? Changed;

    public double WidthOf(int id)
    {
        var column = columns.Single(column => column.Id == id);
        return !column.Visible ? 0 : column.Width + (id == 0 && fillName ? Math.Max(0, viewport - MinimumWidth) : 0);
    }

    public void SetViewport(double value)
    {
        if (Math.Abs(viewport - value) < .1) return;
        viewport = value; Changed?.Invoke();
    }

    public void Resize(int id, double width)
    {
        var column = columns.Single(column => column.Id == id);
        if (id == 0) fillName = false;
        column.Width = Math.Clamp(width, column.Minimum, id == 0 ? 1000 : 500);
        Changed?.Invoke();
    }

    public void SetVisible(int id, bool visible)
    {
        var column = columns.Single(column => column.Id == id);
        if (id == 0 || column.Visible == visible) return;
        column.Visible = visible; Changed?.Invoke();
    }

    public bool CanMove(int id, int direction)
    {
        var visible = columns.Where(column => column.Visible).ToList();
        int index = visible.FindIndex(column => column.Id == id);
        return index >= 0 && index + direction >= 0 && index + direction < visible.Count;
    }

    public void Move(int id, int direction)
    {
        if (!CanMove(id, direction)) return;
        var visible = columns.Where(column => column.Visible).ToList();
        int index = visible.FindIndex(column => column.Id == id);
        MoveTo(id, visible[index + direction].Id);
    }

    public void MoveTo(int id, int targetId)
    {
        int from = columns.FindIndex(column => column.Id == id), to = columns.FindIndex(column => column.Id == targetId);
        if (from == to) return;
        var column = columns[from]; columns.RemoveAt(from); columns.Insert(to, column); Changed?.Invoke();
    }

    public void Reset()
    {
        double[] widths = [260, 70, 86, 120, 126, 108, 108];
        columns.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var column in columns) { column.Width = widths[column.Id]; column.Visible = true; }
        fillName = true; Changed?.Invoke();
    }

    internal void Apply(Grid grid)
    {
        while (grid.ColumnDefinitions.Count < columns.Count) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int position = 0; position < columns.Count; ++position) {
            var column = columns[position];
            grid.ColumnDefinitions[position].Width = new GridLength(WidthOf(column.Id));
            var cell = (FrameworkElement)grid.Children[column.Id]; Grid.SetColumn(cell, position);
            cell.Visibility = column.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
