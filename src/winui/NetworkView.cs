using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private FrameworkElement BuildNetworkFilter()
    {
        model.SetShowVirtualNetworkAdapters(NetworkViewPreference.Read());
        var choice = new CheckBox { Content = "Virtuální síťová rozhraní", IsChecked = model.ShowVirtualNetworkAdapters,
            FontSize = 11, MinHeight = 32, Margin = new Thickness(8, 0, 0, 8) };
        AutomationProperties.SetName(choice, "Zobrazit i virtuální síťová rozhraní");
        AutomationProperties.SetAutomationId(choice, "network-show-virtual");
        ToolTipService.SetToolTip(choice, "Fyzická rozhraní včetně odpojených zůstávají vždy viditelná. Volba přidá virtuální rozhraní a loopback podle údajů Windows; měření je po jednotlivých rozhraních.");
        void Change() {
            bool value = choice.IsChecked == true;
            model.SetShowVirtualNetworkAdapters(value);
            if (selectedResource is null || !model.Resources.Contains(selectedResource)) resourceList.SelectedItem = model.Resources.FirstOrDefault();
            RefreshPerformance();
            if (!NetworkViewPreference.Save(value)) footer.Text = "Pohled sítě je změněný, ale volbu se nepodařilo uložit pro příští spuštění.";
        }
        choice.Checked += (_, _) => Change(); choice.Unchecked += (_, _) => Change();
        return choice;
    }
}

internal static class NetworkViewPreference
{
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NksTaskMgr", "network-view.txt");
    internal static bool Read()
    {
        try {
            using var reader = new StreamReader(path);
            Span<char> value = stackalloc char[32]; int count = reader.ReadBlock(value);
            return reader.EndOfStream && value[..count].Trim().SequenceEqual("all".AsSpan());
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static bool Save(bool showVirtual)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, showVirtual ? "all" : "physical", new UTF8Encoding(false));
            File.Move(temporary, path, true); return true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
