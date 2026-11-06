using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    // Name, Stav, CPU, Paměť, Disk, Síť — shared by Users and Applications so both tables line up the same way.
    internal static readonly double[] GroupColumns = [0, 96, 64, 96, 88, 88];
    private static readonly string[] GroupColumnNames = ["", Loc.T("Stav"), "CPU", Loc.T("Paměť"), Loc.T("Disk"), Loc.T("Síť")];

    /// <summary>Builds the clickable column header of a grouped table; a click sorts by that column, numbers largest first.</summary>
    private static Grid GroupHeader(string firstName, string[] tips, List<(Button Button, TextBlock Text)> headers, Action<int> sort)
    {
        var header = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 0, 8, 0), MinHeight = 30, BorderBrush = Display.Brush(0x2c363d), BorderThickness = new Thickness(0, 0, 0, 1) };
        for (int i = 0; i < GroupColumns.Length; ++i) {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(GroupColumns[i]) });
            string name = i == 0 ? firstName : GroupColumnNames[i];
            var text = Label(name, 11, 0x95a4ad);
            var button = new Button { Content = text, Padding = new Thickness(i == 0 ? 40 : 0, 0, 0, 0), MinHeight = 0, Height = 28,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = i >= 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            int column = i;
            button.Click += (_, _) => sort(column);
            ToolTipService.SetToolTip(button, tips[i]); AutomationProperties.SetName(button, Loc.F("Řadit podle: {0}", name));
            Grid.SetColumn(button, i); header.Children.Add(button); headers.Add((button, text));
        }
        return header;
    }

    private static void UpdateGroupHeader(List<(Button Button, TextBlock Text)> headers, string firstName, int sortColumn, bool descending)
    {
        for (int i = 0; i < headers.Count; ++i) {
            string caption = (i == 0 ? firstName : GroupColumnNames[i]) + (i == sortColumn ? (descending ? " ↓" : " ↑") : "");
            if (headers[i].Text.Text != caption) headers[i].Text.Text = caption;
        }
    }

    /// <summary>Applies the shared row template; the network column collapses while no ETW capture measures it.</summary>
    private void ConfigureGroupList(ListView list, GroupLines items, Func<bool> networkShown)
    {
        list.ItemTemplate = (DataTemplate)groupTemplates["GroupLine"]; list.ItemsSource = items;
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style;
        list.ContainerContentChanging += (_, args) => {
            if (args.InRecycleQueue || args.Item is not GroupLine line) return;
            AutomationProperties.SetName(args.ItemContainer, line.Accessible);
            if (args.ItemContainer.ContentTemplateRoot is Grid grid && grid.ColumnDefinitions.Count == GroupColumns.Length)
                grid.ColumnDefinitions[^1].Width = new GridLength(networkShown() ? GroupColumns[^1] : 0);
        };
    }

    internal static string DiskText(ProcessRow row, out double value)
    {
        bool measured = row.ReadIo.State == SampleState.Valid && row.WriteIo.State == SampleState.Valid;
        value = measured ? row.ReadIo.Value + row.WriteIo.Value : -1;
        return row.Ended || row.Stale ? row.ReadText
            : measured ? Display.Number(value / 1048576, " MiB/s")
            : Display.Metric(row.ReadIo.State != SampleState.Valid ? row.ReadIo : row.WriteIo, " MiB/s", 1048576);
    }
}

/// <summary>Application names like Task Manager shows them: the executable's file description, read once per image off the UI thread.</summary>
internal static class ApplicationNames
{
    private static readonly ConcurrentDictionary<ProcessKey, string> names = new();
    private static readonly ConcurrentDictionary<ProcessKey, byte> pending = new();

    /// <summary>Returns the cached description, or null while it is read; <paramref name="ready"/> runs on the UI thread when it arrives.</summary>
    internal static string? Get(ProcessRow row, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action ready)
    {
        if (names.TryGetValue(row.Key, out var name)) return name;
        if (!pending.TryAdd(row.Key, 0)) return null;
        var key = row.Key; string fallback = row.Name;
        _ = Task.Run(() => {
            string value = fallback.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fallback[..^4] : fallback;
            try {
                if (ProcessIcons.ImagePath(key) is { } path && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
                    value = description.Trim();
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            names[key] = value; pending.TryRemove(key, out _);
            dispatcher.TryEnqueue(() => ready());
        });
        return null;
    }
}
