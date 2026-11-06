using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private void ConfigureNetworkFilters(FrameworkElement inventoryHeader)
    {
        model.SetNetworkFilters(NetworkViewPreference.Read());
        inventoryHeader.ContextRequested += (_, args) => {
            if (args.Handled || view != DesktopView.Performance) return;
            var menu = new MenuFlyout(); AddNetworkItems(menu);
            ShowContextMenu(menu, inventoryHeader, args);
        };
    }

    private void AddNetworkItems(MenuFlyout menu)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var disconnected = new ToggleMenuFlyoutItem { Text = Loc.T("Zobrazit odpojené adaptéry"), IsChecked = model.NetworkFilters.ShowDisconnected };
        AutomationProperties.SetAutomationId(disconnected, "context-network-disconnected");
        disconnected.Click += (_, _) => { if (view == DesktopView.Performance) ChangeNetworkFilters(model.NetworkFilters with { ShowDisconnected = disconnected.IsChecked }); };
        var auxiliary = new ToggleMenuFlyoutItem { Text = Loc.T("Zobrazit pomocná/virtuální rozhraní"), IsChecked = model.NetworkFilters.ShowAuxiliary };
        AutomationProperties.SetAutomationId(auxiliary, "context-network-auxiliary");
        auxiliary.Click += (_, _) => { if (view == DesktopView.Performance) ChangeNetworkFilters(model.NetworkFilters with { ShowAuxiliary = auxiliary.IsChecked }); };
        menu.Items.Add(disconnected); menu.Items.Add(auxiliary);
    }

    private void ChangeNetworkFilters(NetworkFilterOptions value)
    {
        var previous = selectedResource;
        model.SetNetworkFilters(value);
        resourceList.SelectedItem = NetworkResourceFilter.SelectionAfterFilter(model.Resources, previous);
        RefreshPerformance();
        if (!NetworkViewPreference.Save(value)) footer.Text = Loc.T("Pohled sítě je změněný, ale volbu se nepodařilo uložit pro příští spuštění.");
    }
}

internal static class NetworkViewPreference
{
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "network-view.txt");
    internal static NetworkFilterOptions Parse(string value) => value.Trim() switch {
        "all" or "v2;disconnected=0;auxiliary=1" => new(false, true),
        "v2;disconnected=1;auxiliary=0" => new(true, false),
        "v2;disconnected=1;auxiliary=1" => new(true, true),
        _ => default
    };
    internal static string Format(NetworkFilterOptions value) => $"v2;disconnected={(value.ShowDisconnected ? 1 : 0)};auxiliary={(value.ShowAuxiliary ? 1 : 0)}";

    internal static NetworkFilterOptions Read()
    {
        try {
            using var reader = new StreamReader(path);
            Span<char> value = stackalloc char[128]; int count = reader.ReadBlock(value);
            return reader.EndOfStream ? Parse(new string(value[..count])) : default;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return default; }
    }
    internal static bool Save(NetworkFilterOptions value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, Format(value), new UTF8Encoding(false));
            File.Move(temporary, path, true); return true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
