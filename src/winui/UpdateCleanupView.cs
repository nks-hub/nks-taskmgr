using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

internal sealed partial class UpdateWindow
{
    private TextBlock? cleanupDeadline;

    private async Task CleanupAsync(CancellationToken token)
    {
        status.Text = "Připravuji náhled úklidu…";
        using var session = await client.PrepareCleanupAsync(token);
        preparedCleanup = session;
        if (closed || token.IsCancellationRequested) return;
        var plan = session.Plan;
        bool hasCandidates = plan.Items.Any(item => item.Outcome == UpdateCleanupOutcome.Eligible);
        var content = new StackPanel { Spacing = 12, MaxWidth = 620 };
        content.Children.Add(Text("Lze odstranit: " + CleanupTotalsLabel(plan.Summary.Eligible)));
        content.Children.Add(Text("Zachováno: " + CleanupTotalsLabel(plan.Summary.Kept)));
        if (plan.Items.Any(item => item.Outcome == UpdateCleanupOutcome.Unknown))
            content.Children.Add(Text("Neověřené položky: " + CleanupTotalsLabel(plan.Summary.Unknown)));
        if (!plan.CompleteInventory)
            content.Children.Add(Text("Přehled cache není úplný. Uvedené velikosti nejsou velikostí celé cache; odstranit lze jen ověřené položky v tomto náhledu."));
        content.Children.Add(Text(hasCandidates
            ? "Odstraní se pouze uvedené nepotřebné soubory aktualizací. Před smazáním se znovu ověří. Používané nebo změněné soubory se přeskočí. Nastavení aplikace zůstane zachované."
            : "V tomto náhledu nejsou žádné ověřené položky k odstranění."));
        cleanupDeadline = Text("");
        content.Children.Add(cleanupDeadline);
        content.Children.Add(CleanupItemsView(plan.Items));
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Úklid cache aktualizací",
            Content = new ScrollViewer { Content = content, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = hasCandidates ? "Odstranit uvedené soubory" : "", CloseButtonText = hasCandidates ? "Zrušit" : "Zavřít",
            DefaultButton = ContentDialogButton.Close };
        Appearance.Dialog(dialog); confirmation = dialog;
        dialog.PrimaryButtonClick += (_, args) => {
            if (!hasCandidates || token.IsCancellationRequested || UpdateProtocol.NowMs >= session.CommitDeadlineMonotonicMs) args.Cancel = true;
        };
        RefreshButtons();
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        finally { confirmation = null; cleanupDeadline = null; }
        if (choice != ContentDialogResult.Primary || closed || token.IsCancellationRequested) {
            if (!closed) status.Text = "Úklid nebyl spuštěný.";
            return;
        }
        status.Text = "Odstraňuji potvrzené položky…";
        var outcome = await session.CommitAsync(token);
        if (closed) return;
        ShowUnavailable("Přehled balíčků se po úklidu změnil.", "Před další operací načtěte aktuální přehled.", false);
        cleanupUncertain = !outcome.Completed && outcome.MayHaveDeleted;
        status.Text = !outcome.Completed
            ? outcome.MayHaveDeleted ? "Úklid nemá potvrzený výsledek." : "Úklid nebyl dokončený."
            : outcome.Status == UpdateStatus.Ok ? "Úklid dokončen." : "Úklid skončil s omezením.";
        var lines = new List<string>();
        if (outcome.Summary.Deleted.CountsKnown) lines.Add("Odstraněno: " + CleanupTotalsLabel(outcome.Summary.Deleted));
        else lines.Add("Počet odstraněných souborů a uvolněná velikost nejsou potvrzené.");
        if (outcome.Completed) {
            foreach (var (label, totals) in new[] { ("Přeskočeno", outcome.Summary.Skipped), ("Chyby", outcome.Summary.Failed), ("Neověřeno", outcome.Summary.Unknown) })
                if (totals.Files != 0 || totals.Directories != 0 || totals.UnknownSizeCount != 0)
                    lines.Add(label + ": " + CleanupTotalsLabel(totals));
            foreach (var item in outcome.Items.Where(item => item.Outcome is UpdateCleanupOutcome.Skipped or UpdateCleanupOutcome.Failed or UpdateCleanupOutcome.Unknown))
                lines.Add(CleanupItemLabel(item));
        }
        if (outcome.Detail.Length != 0) lines.Add(Bound(outcome.Detail));
        lines.Add(cleanupUncertain
            ? "Část souborů už mohla být odstraněná. Po skončení pomocníka načtěte přehled. Úklid se neopakuje automaticky."
            : "Před další operací načtěte aktuální přehled balíčků.");
        result.Text = string.Join("\n", lines);
    }

    private static Expander CleanupItemsView(IReadOnlyList<UpdateCleanupItem> items)
    {
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 300,
            FontFamily = UiTypography.Font, FontSize = 12,
            Text = items.Count == 0 ? "Žádné položky." : string.Join("\n\n", items.Select(CleanupItemLabel)) };
        ScrollViewer.SetVerticalScrollBarVisibility(details, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(details, "Položky náhledu úklidu a důvod jejich zachování nebo odstranění");
        return new Expander { Header = "Položky a důvody", Content = details, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static string CleanupTotalsLabel(UpdateCleanupTotals value)
    {
        if (!value.CountsKnown) return "počet a velikost nejsou potvrzené";
        string count = (value.UnknownSizeCount == 0 ? "" : "známé údaje: ") + "soubory: " + value.Files.ToString("N0", CultureInfo.GetCultureInfo("cs-CZ"));
        if (value.Directories != 0) count += ", složky: " + value.Directories.ToString("N0", CultureInfo.GetCultureInfo("cs-CZ"));
        if (value.KnownBytes != 0 || value.UnknownSizeCount == 0) count += " · " + CleanupBytes(value.KnownBytes);
        if (value.UnknownSizeCount != 0) count += "; položky s nezjištěnou velikostí: " + value.UnknownSizeCount;
        return count;
    }

    private static string CleanupBytes(ulong? value) => value switch {
        null => "velikost nezjištěná",
        >= 1073741824 => Display.Number(value.Value / 1073741824d, " GiB", 2),
        >= 1048576 => Display.Number(value.Value / 1048576d, " MiB", 1),
        >= 1024 => Display.Number(value.Value / 1024d, " KiB", 1),
        _ => value.Value.ToString("N0", CultureInfo.GetCultureInfo("cs-CZ")) + " B"
    };

    private static string CleanupItemLabel(UpdateCleanupItem item)
    {
        string name = item.Name.Length == 0 ? item.Id : item.Name;
        string outcome = item.Outcome switch {
            UpdateCleanupOutcome.Eligible => "K odstranění", UpdateCleanupOutcome.Kept => "Zachováno", UpdateCleanupOutcome.Deleted => "Odstraněno",
            UpdateCleanupOutcome.Skipped => "Přeskočeno", UpdateCleanupOutcome.Failed => "Chyba", _ => "Neověřeno"
        };
        string deleted = item.DeletedFiles == 0 && item.DeletedDirectories == 0 ? "" : "\nSkutečně odstraněno: "
            + CleanupTotalsLabel(new(item.DeletedFiles, item.DeletedDirectories, item.DeletedBytes, 0));
        return outcome + " · " + Bound(name) + " · " + CleanupBytes(item.Outcome == UpdateCleanupOutcome.Deleted ? item.DeletedBytes : item.Bytes)
            + deleted + "\n" + CleanupReasonText(item.Reason)
            + (item.Detail.Length == 0 ? "" : "\n" + Bound(item.Detail));
    }

    private static string CleanupReasonText(UpdateCleanupReason reason) => reason switch {
        UpdateCleanupReason.Unused => "Nepotřebná ověřená položka.",
        UpdateCleanupReason.Current => "Balíček současné verze se zachovává.",
        UpdateCleanupReason.Previous => "Balíček předchozí verze se zachovává pro návrat.",
        UpdateCleanupReason.LiveWorker => "Aktualizační pomocník ještě běží.",
        UpdateCleanupReason.LiveInstaller => "Instalátor ještě běží.",
        UpdateCleanupReason.LifetimeUnknown => "Ukončení původního procesu není ověřené.",
        UpdateCleanupReason.Unresolved => "Záznam nedokončené operace zůstává zachovaný.",
        UpdateCleanupReason.ResolutionProof => "Doklad potřebný pro ověření obnovy zůstává zachovaný.",
        UpdateCleanupReason.RootAnchor => "Záznam potřebný pro historii této kopie aplikace zůstává zachovaný.",
        UpdateCleanupReason.UnknownRoot => "Složku aplikace nelze ověřit.",
        UpdateCleanupReason.UnknownData => "Neznámá data se neodstraňují.",
        UpdateCleanupReason.MissingProof => "Chybí důkaz pro bezpečné odstranění.",
        UpdateCleanupReason.StageInUse => "Pracovní složka je používaná nebo není prázdná.",
        UpdateCleanupReason.Changed => "Položka se od náhledu změnila.",
        UpdateCleanupReason.Denied => "Přístup k položce byl odmítnutý.",
        UpdateCleanupReason.Io => "Položku nelze zpracovat kvůli chybě souboru.",
        UpdateCleanupReason.Dependency => "Položku potřebuje jiný zachovaný záznam.",
        UpdateCleanupReason.Cancelled => "Úklid byl zrušený.",
        UpdateCleanupReason.Legacy => "Starší záznam nemá všechny potřebné údaje.",
        UpdateCleanupReason.AlreadyAbsent => "Položka už není přítomná; nepočítá se jako odstraněná tímto úklidem.",
        _ => "Důvod nebyl rozpoznaný; položka se neodstraňuje."
    };
}
