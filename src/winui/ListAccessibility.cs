using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>Gives list item containers a screen-reader name; other row types name themselves through ToString.</summary>
internal static class ListAccessibility
{
    private sealed class ResourceItemName(DependencyObject container)
    {
        private ResourceRow? row;
        private string? title;

        internal void Bind(ResourceRow? value)
        {
            if (row is not null) row.PropertyChanged -= Changed;
            row = value; title = null;
            if (row is null) return;
            row.PropertyChanged += Changed; Changed(null, null);
        }

        private void Changed(object? sender, PropertyChangedEventArgs? args)
        {
            if (row is null || ReferenceEquals(row.Title, title)) return;
            title = row.Title;
            AutomationProperties.SetName(container, title.Length > 0 ? title : Kind(row.Kind));
        }
    }

    private static string Kind(ResourceKind kind) => kind switch {
        ResourceKind.Cpu => "Procesor", ResourceKind.Memory => "Paměť", ResourceKind.Disk => "Disk", ResourceKind.Gpu => "Grafická karta", _ => "Síť" };

    /// <summary>Names each container after its resource; the name follows the row when the container is recycled or the title is published later.</summary>
    internal static void NameResourceItems(ListViewBase list)
    {
        var names = new ConditionalWeakTable<DependencyObject, ResourceItemName>();
        list.ContainerContentChanging += (_, args) =>
            names.GetValue(args.ItemContainer, container => new ResourceItemName(container)).Bind(args.InRecycleQueue ? null : args.Item as ResourceRow);
    }

    /// <summary>Throws unless realized containers expose a UIA name that is neither empty nor the CLR type name of the item.</summary>
    internal static void RequireItemNames(ListViewBase list, string label)
    {
        int realized = 0;
        for (int i = 0; i < Math.Min(list.Items.Count, 200); ++i) {
            if (list.ContainerFromIndex(i) is not ListViewItem container) continue;
            var item = list.Items[i]; var type = item.GetType();
            string name = FrameworkElementAutomationPeer.CreatePeerForElement(container).GetName();
            if (string.IsNullOrWhiteSpace(name) || name == type.FullName || name == type.Name)
                throw new InvalidOperationException($"{label} item {i} exposes UIA name \"{name}\" instead of a readable name.");
            ++realized;
        }
        if (realized == 0) throw new InvalidOperationException(label + " has no realized item to verify the UIA names.");
    }
}
