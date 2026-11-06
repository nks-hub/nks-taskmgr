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
        status.Text = Loc.T("Připravuji náhled úklidu…");
        using var session = await client.PrepareCleanupAsync(token);
        preparedCleanup = session;
        if (closed || token.IsCancellationRequested) return;
        var plan = session.Plan;
        bool hasCandidates = plan.Items.Any(item => item.Outcome == UpdateCleanupOutcome.Eligible);
        var content = new StackPanel { Spacing = 12, MaxWidth = 620 };
        content.Children.Add(Text(Loc.F("Lze odstranit: {0}", CleanupTotalsLabel(plan.Summary.Eligible))));
        content.Children.Add(Text(Loc.F("Zachováno: {0}", CleanupTotalsLabel(plan.Summary.Kept))));
        if (plan.Items.Any(item => item.Outcome == UpdateCleanupOutcome.Unknown))
            content.Children.Add(Text(Loc.F("Neověřené položky: {0}", CleanupTotalsLabel(plan.Summary.Unknown))));
        if (!plan.CompleteInventory)
            content.Children.Add(Text(Loc.T("Přehled cache není úplný. Uvedené velikosti nejsou velikostí celé cache; odstranit lze jen ověřené položky v tomto náhledu.")));
        content.Children.Add(Text(hasCandidates
            ? Loc.T("Odstraní se pouze uvedené nepotřebné soubory aktualizací. Před smazáním se znovu ověří. Používané nebo změněné soubory se přeskočí. Nastavení aplikace zůstane zachované.")
            : Loc.T("V tomto náhledu nejsou žádné ověřené položky k odstranění.")));
        cleanupDeadline = Text("");
        content.Children.Add(cleanupDeadline);
        content.Children.Add(CleanupItemsView(plan.Items));
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = Loc.T("Úklid cache aktualizací"),
            Content = new ScrollViewer { Content = content, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = hasCandidates ? Loc.T("Odstranit uvedené soubory") : "", CloseButtonText = hasCandidates ? Loc.T("Zrušit") : Loc.T("Zavřít"),
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
            if (!closed) status.Text = Loc.T("Úklid nebyl spuštěný.");
            return;
        }
        status.Text = Loc.T("Odstraňuji potvrzené položky…");
        var outcome = await session.CommitAsync(token);
        if (closed) return;
        ShowUnavailable(Loc.T("Přehled balíčků se po úklidu změnil."), Loc.T("Před další operací načtěte aktuální přehled."), false);
        cleanupUncertain = !outcome.Completed && outcome.MayHaveDeleted;
        status.Text = !outcome.Completed
            ? outcome.MayHaveDeleted ? Loc.T("Úklid nemá potvrzený výsledek.") : Loc.T("Úklid nebyl dokončený.")
            : outcome.Status == UpdateStatus.Ok ? Loc.T("Úklid dokončen.") : Loc.T("Úklid skončil s omezením.");
        var lines = new List<string>();
        if (outcome.Summary.Deleted.CountsKnown) lines.Add(Loc.F("Odstraněno: {0}", CleanupTotalsLabel(outcome.Summary.Deleted)));
        else lines.Add(Loc.T("Počet odstraněných souborů a uvolněná velikost nejsou potvrzené."));
        if (outcome.Completed) {
            foreach (var (label, totals) in new[] { (Loc.T("Přeskočeno"), outcome.Summary.Skipped), (Loc.T("Chyby"), outcome.Summary.Failed), (Loc.T("Neověřeno"), outcome.Summary.Unknown) })
                if (totals.Files != 0 || totals.Directories != 0 || totals.UnknownSizeCount != 0)
                    lines.Add(label + ": " + CleanupTotalsLabel(totals));
            foreach (var item in outcome.Items.Where(item => item.Outcome is UpdateCleanupOutcome.Skipped or UpdateCleanupOutcome.Failed or UpdateCleanupOutcome.Unknown))
                lines.Add(CleanupItemLabel(item));
        }
        if (outcome.Detail.Length != 0) lines.Add(Bound(outcome.Detail));
        lines.Add(cleanupUncertain
            ? Loc.T("Část souborů už mohla být odstraněná. Po skončení pomocníka načtěte přehled. Úklid se neopakuje automaticky.")
            : Loc.T("Před další operací načtěte aktuální přehled balíčků."));
        result.Text = string.Join("\n", lines);
    }

    private static Expander CleanupItemsView(IReadOnlyList<UpdateCleanupItem> items)
    {
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 300,
            FontFamily = UiTypography.Font, FontSize = 12,
            Text = items.Count == 0 ? Loc.T("Žádné položky.") : string.Join("\n\n", items.Select(CleanupItemLabel)) };
        ScrollViewer.SetVerticalScrollBarVisibility(details, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(details, Loc.T("Položky náhledu úklidu a důvod jejich zachování nebo odstranění"));
        return new Expander { Header = Loc.T("Položky a důvody"), Content = details, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static string CleanupTotalsLabel(UpdateCleanupTotals value)
    {
        if (!value.CountsKnown) return Loc.T("počet a velikost nejsou potvrzené");
        string files = value.Files.ToString("N0", Loc.Culture);
        string count = value.UnknownSizeCount == 0 ? Loc.F("soubory: {0}", files) : Loc.F("známé údaje: soubory: {0}", files);
        if (value.Directories != 0) count += Loc.F(", složky: {0}", value.Directories.ToString("N0", Loc.Culture));
        if (value.KnownBytes != 0 || value.UnknownSizeCount == 0) count += " · " + CleanupBytes(value.KnownBytes);
        if (value.UnknownSizeCount != 0) count += Loc.F("; položky s nezjištěnou velikostí: {0}", value.UnknownSizeCount);
        return count;
    }

    private static string CleanupBytes(ulong? value) => value is null ? Loc.T("velikost nezjištěná") : Display.Bytes(value.Value);

    private static string CleanupItemLabel(UpdateCleanupItem item)
    {
        string name = item.Name.Length == 0 ? item.Id : item.Name;
        string outcome = item.Outcome switch {
            UpdateCleanupOutcome.Eligible => Loc.T("K odstranění"), UpdateCleanupOutcome.Kept => Loc.T("Zachováno"), UpdateCleanupOutcome.Deleted => Loc.T("Odstraněno"),
            UpdateCleanupOutcome.Skipped => Loc.T("Přeskočeno"), UpdateCleanupOutcome.Failed => Loc.T("Chyba"), _ => Loc.T("Neověřeno")
        };
        string deleted = item.DeletedFiles == 0 && item.DeletedDirectories == 0 ? "" : "\n" + Loc.F("Skutečně odstraněno: {0}",
            CleanupTotalsLabel(new(item.DeletedFiles, item.DeletedDirectories, item.DeletedBytes, 0)));
        return outcome + " · " + Bound(name) + " · " + CleanupBytes(item.Outcome == UpdateCleanupOutcome.Deleted ? item.DeletedBytes : item.Bytes)
            + deleted + "\n" + CleanupReasonText(item.Reason)
            + (item.Detail.Length == 0 ? "" : "\n" + Bound(item.Detail));
    }

    private static string CleanupReasonText(UpdateCleanupReason reason) => reason switch {
        UpdateCleanupReason.Unused => Loc.T("Nepotřebná ověřená položka."),
        UpdateCleanupReason.Current => Loc.T("Balíček současné verze se zachovává."),
        UpdateCleanupReason.Previous => Loc.T("Balíček předchozí verze se zachovává pro návrat."),
        UpdateCleanupReason.LiveWorker => Loc.T("Aktualizační pomocník ještě běží."),
        UpdateCleanupReason.LiveInstaller => Loc.T("Instalátor ještě běží."),
        UpdateCleanupReason.LifetimeUnknown => Loc.T("Ukončení původního procesu není ověřené."),
        UpdateCleanupReason.Unresolved => Loc.T("Záznam nedokončené operace zůstává zachovaný."),
        UpdateCleanupReason.ResolutionProof => Loc.T("Doklad potřebný pro ověření obnovy zůstává zachovaný."),
        UpdateCleanupReason.RootAnchor => Loc.T("Záznam potřebný pro historii této kopie aplikace zůstává zachovaný."),
        UpdateCleanupReason.UnknownRoot => Loc.T("Složku aplikace nelze ověřit."),
        UpdateCleanupReason.UnknownData => Loc.T("Neznámá data se neodstraňují."),
        UpdateCleanupReason.MissingProof => Loc.T("Chybí důkaz pro bezpečné odstranění."),
        UpdateCleanupReason.StageInUse => Loc.T("Pracovní složka je používaná nebo není prázdná."),
        UpdateCleanupReason.Changed => Loc.T("Položka se od náhledu změnila."),
        UpdateCleanupReason.Denied => Loc.T("Přístup k položce byl odmítnutý."),
        UpdateCleanupReason.Io => Loc.T("Položku nelze zpracovat kvůli chybě souboru."),
        UpdateCleanupReason.Dependency => Loc.T("Položku potřebuje jiný zachovaný záznam."),
        UpdateCleanupReason.Cancelled => Loc.T("Úklid byl zrušený."),
        UpdateCleanupReason.Legacy => Loc.T("Starší záznam nemá všechny potřebné údaje."),
        UpdateCleanupReason.AlreadyAbsent => Loc.T("Položka už není přítomná; nepočítá se jako odstraněná tímto úklidem."),
        _ => Loc.T("Důvod nebyl rozpoznaný; položka se neodstraňuje.")
    };
}
