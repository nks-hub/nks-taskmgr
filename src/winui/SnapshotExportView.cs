using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using System.Runtime.InteropServices;

namespace NksTaskMgr.WinUI;

public sealed partial class MainWindow
{
    private readonly Button exportSnapshot = Button("");
    private bool exporting;
    private Task? smokeSnapshotExport;

    private void ConfigureSnapshotExport()
    {
        exportSnapshot.Width = 35; exportSnapshot.Padding = new Thickness(0); exportSnapshot.IsEnabled = false;
        exportSnapshot.Content = UiIcon.Create("export", 0x95a4ad, 19);
        AutomationProperties.SetName(exportSnapshot, "Uložit snímek");
        AutomationProperties.SetAutomationId(exportSnapshot, "snapshot-export");
        ToolTipService.SetToolTip(exportSnapshot, "Uložit snímek");
        exportSnapshot.Click += async (_, _) => await ExportSnapshotAsync();
    }

    private async Task ExportSnapshotAsync()
    {
        if (exporting || model.ProcessTime <= 0) return;
        exporting = true; exportSnapshot.IsEnabled = false;
        try {
            var snapshot = SnapshotDocument.Capture(model);
            var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, Height = 190 };
            AutomationProperties.SetAutomationId(preview, "snapshot-preview");
            AutomationProperties.SetName(preview, "Náhled exportu JSON");
            var body = new StackPanel { Spacing = 10, MinWidth = 330 };
            string devices = snapshot.ResourceTimeNs > 0 ? $"{snapshot.Resources.Length} zařízení" : snapshot.ResourceError ? "zařízení nedostupná" : "zařízení čekají na vzorek";
            var summary = Label($"{snapshot.Processes.Length} procesů · {devices} · {snapshot.CapturedAt.LocalDateTime:g}", 12, 0x95a4ad);
            summary.TextWrapping = TextWrapping.Wrap; body.Children.Add(summary);
            var checks = new Dictionary<SnapshotFields, CheckBox>();
            var metrics = new Grid { ColumnSpacing = 14, RowSpacing = 2 };
            metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.RowDefinitions.Add(new RowDefinition()); metrics.RowDefinitions.Add(new RowDefinition()); body.Children.Add(metrics);
            CheckBox AddChoice(SnapshotFields field, string text, string id) {
                var check = new CheckBox { Content = text, IsChecked = SnapshotFields.Default.HasFlag(field), FontSize = 12 };
                AutomationProperties.SetAutomationId(check, id); checks.Add(field, check);
                check.Checked += (_, _) => RefreshPreview(); check.Unchecked += (_, _) => RefreshPreview();
                return check;
            }
            int position = 0;
            foreach (var (field, text, id) in new[] {
                (SnapshotFields.Cpu, "Procesor", "snapshot-cpu"), (SnapshotFields.Memory, "Paměť", "snapshot-memory"),
                (SnapshotFields.Io, "Procesové I/O", "snapshot-io"), (SnapshotFields.Devices, "Zařízení", "snapshot-devices") }) {
                var check = AddChoice(field, text, id); Grid.SetColumn(check, position % 2); Grid.SetRow(check, position++ / 2); metrics.Children.Add(check);
            }
            body.Children.Add(AddChoice(SnapshotFields.Names, "Zahrnout názvy procesů a zařízení", "snapshot-names"));
            var ids = AddChoice(SnapshotFields.DeviceIds, "Zahrnout identifikátory zařízení", "snapshot-device-ids"); body.Children.Add(ids);
            var privacy = Label("Názvy a identifikátory mohou obsahovat osobní údaje nebo sériová čísla. Příkazy, argumenty a účty se neukládají.", 11, 0x95a4ad);
            privacy.TextWrapping = TextWrapping.Wrap; body.Children.Add(privacy);
            var previewLabel = Label("Ukázka: nejvýše 3 položky z každého seznamu. Soubor obsahuje celý snímek.", 11, 0x95a4ad);
            previewLabel.TextWrapping = TextWrapping.Wrap; body.Children.Add(previewLabel); body.Children.Add(preview);
            SnapshotFields Fields() => checks.Aggregate((SnapshotFields)0, (value, pair) => pair.Value.IsChecked == true ? value | pair.Key : value);
            void RefreshPreview() => preview.Text = SnapshotExport.Preview(snapshot, Fields());
            RefreshPreview();
            var dialog = Appearance.Dialog(new ContentDialog { Title = "Uložit snímek", XamlRoot = root.XamlRoot,
                PrimaryButtonText = "Uložit…", CloseButtonText = "Zrušit", DefaultButton = ContentDialogButton.Primary,
                Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(60, Math.Min(520, root.ActualHeight - 280)) } });
            AutomationProperties.SetAutomationId(dialog, "snapshot-dialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "nks-snapshot-" + snapshot.CapturedAt.ToString("yyyyMMdd-HHmmss"), DefaultFileExtension = ".json" };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            string? backup = await SnapshotExport.SaveAsync(file.Path, snapshot, Fields());
            footer.Text = "Uložen snímek: " + Path.GetFileName(file.Path);
            if (backup is not null) await Appearance.Dialog(new ContentDialog { Title = "Snímek je uložený", XamlRoot = root.XamlRoot,
                Content = "Původní zálohu se nepodařilo odstranit. Zůstala v souboru: " + backup, CloseButtonText = "Zavřít" }).ShowAsync();
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException) {
            await Appearance.Dialog(new ContentDialog { Title = "Snímek se nepodařilo uložit", Content = error.Message,
                XamlRoot = root.XamlRoot, CloseButtonText = "Zavřít" }).ShowAsync();
        } finally { exporting = false; exportSnapshot.IsEnabled = model.ProcessTime > 0; }
    }
}
