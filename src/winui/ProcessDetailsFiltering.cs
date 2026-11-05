using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NksTaskMgr.WinUI;

internal sealed partial class ProcessDetailsView
{
    private readonly Button memory = Command("Paměť"), handles = Command("Handly"), observeObject = Command("Zjistit typ a název objektu");
    private readonly TextBox find = new() { Height = 34, MinHeight = 0, FontFamily = UiTypography.Font, FontSize = 12 };
    private readonly ComboBox stateFilter = new() { Width = 170, MinHeight = 32 };
    private DispatcherQueueTimer searchTimer = null!;
    private CancellationTokenSource? filterOperation;
    private ProcessDetailsSnapshot? memorySnapshot, handlesSnapshot;
    private ProcessDetailsCommand? configuredMode;
    private bool buildingFilters, applyingRows;

    private ProcessHandleInfo? SelectedHandle => mode == ProcessDetailsCommand.Handles ? SelectedRow as ProcessHandleInfo : null;
    private ProcessDetailsSnapshot? SnapshotFor(ProcessDetailsCommand value) => value switch {
        ProcessDetailsCommand.Threads => threadSnapshot, ProcessDetailsCommand.Modules => moduleSnapshot,
        ProcessDetailsCommand.Memory => memorySnapshot, ProcessDetailsCommand.Handles => handlesSnapshot, _ => null
    };
    private static string ModeName(ProcessDetailsCommand value) => value switch {
        ProcessDetailsCommand.Threads => "vlákna", ProcessDetailsCommand.Modules => "moduly",
        ProcessDetailsCommand.Memory => "oblasti paměti", ProcessDetailsCommand.Handles => "handly", _ => "podrobnosti"
    };

    private void ConfigureObjects(StackPanel modes)
    {
        modes.Children.Add(memory); modes.Children.Add(handles);
        memory.Click += async (_, _) => await SelectModeAsync(ProcessDetailsCommand.Memory);
        handles.Click += async (_, _) => await SelectModeAsync(ProcessDetailsCommand.Handles);
        observeObject.Click += async (_, _) => { if (SelectedHandle is { } handle) await ReadAsync(ProcessDetailsCommand.HandleInfo, handle: handle); };
        Identify(memory, "process-details-memory", "Mapa virtuální paměti procesu");
        Identify(handles, "process-details-handles", "Handly procesu");
        Identify(observeObject, "process-details-object-name", "Zjistit aktuální typ a název duplikovaného objektu");
        inspector.Children.Add(observeObject);
        ConfigureHandleClose();
        var filters = filtersPanel = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(find); filters.Children.Add(stateFilter); Grid.SetColumn(stateFilter, 1);
        Grid.SetRow(filters, 3); layout.Children.Add(filters);
        Identify(find, "process-details-find", "Filtrovat pouze již načtené výsledky");
        Identify(stateFilter, "process-details-filter", "Stav nebo vlastnost načtených výsledků");
        searchTimer = DispatcherQueue.CreateTimer(); searchTimer.Interval = TimeSpan.FromMilliseconds(150); searchTimer.IsRepeating = false;
        searchTimer.Tick += (_, _) => _ = FilterRowsAsync();
        find.TextChanged += (_, _) => { if (!buildingFilters) QueueFilter(false); };
        stateFilter.SelectionChanged += (_, _) => { if (!buildingFilters) QueueFilter(true); };
    }

    private void ConfigureFilters()
    {
        if (configuredMode == mode) return;
        buildingFilters = true;
        try {
            configuredMode = mode; find.Text = ""; stateFilter.Items.Clear();
            string[] options = mode switch {
                ProcessDetailsCommand.Memory => ["Vše", "Potvrzené", "Rezervované", "Volné"],
                ProcessDetailsCommand.Handles => ["Vše", "S udělenými právy", "Bez udělených práv"],
                ProcessDetailsCommand.Threads => ["Vše", "Dostupné časy", "Nedostupné časy"], _ => ["Vše"]
            };
            foreach (string option in options) stateFilter.Items.Add(option);
            stateFilter.SelectedIndex = 0; stateFilter.IsEnabled = options.Length > 1;
            find.PlaceholderText = mode switch { ProcessDetailsCommand.Memory => "Adresa, velikost, ochrana nebo typ",
                ProcessDetailsCommand.Handles => "Hodnota handle, index typu nebo přístup", ProcessDetailsCommand.Modules => "Název nebo cesta modulu", _ => "TID nebo základní priorita" };
        } finally { buildingFilters = false; }
    }

    private void QueueFilter(bool immediate)
    {
        filterOperation?.Cancel(); searchTimer.Stop();
        if (immediate) _ = FilterRowsAsync(); else searchTimer.Start();
    }

    private async Task FilterRowsAsync()
    {
        filterOperation?.Cancel();
        using var current = new CancellationTokenSource(); filterOperation = current;
        var selectedMode = mode; var snapshot = SnapshotFor(selectedMode); string query = find.Text.Trim(); int filter = stateFilter.SelectedIndex;
        if (snapshot is null) { list.ItemsSource = null; count.Text = "Seznam zatím není načtený."; UpdateDetail(); filterOperation = null; return; }
        object[] all = selectedMode switch {
            ProcessDetailsCommand.Threads => snapshot.Threads, ProcessDetailsCommand.Modules => snapshot.Modules,
            ProcessDetailsCommand.Memory => snapshot.Memory, ProcessDetailsCommand.Handles => snapshot.Handles, _ => []
        };
        try {
            var rows = query.Length == 0 && filter <= 0 ? all : await Task.Run(() => {
                var found = new List<object>();
                for (int i = 0; i < all.Length; ++i) {
                    if ((i & 63) == 0) current.Token.ThrowIfCancellationRequested();
                    if (Matches(all[i], query, filter)) found.Add(all[i]);
                }
                return found.ToArray();
            }, current.Token);
            current.Token.ThrowIfCancellationRequested();
            if (closed || mode != selectedMode || !ReferenceEquals(snapshot, SnapshotFor(mode))) return;
            object? previous = SelectedRow;
            applyingRows = true;
            try {
                list.ItemsSource = rows.Select(row => new ProcessDetailsRow(row)).ToArray();
                int index = previous is null ? -1 : Array.FindIndex(rows, row => ReferenceEquals(row, previous));
                list.SelectedIndex = index >= 0 ? index : rows.Length > 0 ? 0 : -1;
            } finally { applyingRows = false; }
            if (!ReferenceEquals(previous, SelectedRow)) { signature = null; signatureTime = null; }
            if (!ReferenceEquals(observationHandle, SelectedHandle)) ClearObservation();
            count.Text = rows.Length.ToString(Czech) + " / " + all.Length.ToString(Czech) + " · snímek z " + snapshot.ReceivedAt.LocalDateTime.ToString("HH:mm:ss", Czech);
            UpdateDetail(); UpdateAvailability();
        } catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(filterOperation, current)) filterOperation = null; }
    }

    private static bool Matches(object row, string query, int filter)
    {
        if (row is ProcessMemoryRegion memory && filter > 0 && memory.State != (filter == 1 ? 0x1000u : filter == 2 ? 0x2000u : 0x10000u)
            || row is ProcessHandleInfo handle && (filter == 1 && handle.GrantedAccess == 0 || filter == 2 && handle.GrantedAccess != 0)
            || row is ProcessThreadInfo thread && (filter == 1 && thread.TimesState != NksTaskMgr.Native.SampleState.Valid || filter == 2 && thread.TimesState == NksTaskMgr.Native.SampleState.Valid)) return false;
        if (query.Length == 0) return true;
        if (row.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return true;
        return row switch {
            ProcessModuleInfo module => module.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || module.Path.Contains(query, StringComparison.OrdinalIgnoreCase) || ContainsAddress(query, module.BaseAddress, module.ImageSize),
            ProcessMemoryRegion region => Protection(region.Protection).Contains(query, StringComparison.OrdinalIgnoreCase)
                || ProcessMemoryRegion.TypeText(region.Type).Contains(query, StringComparison.OrdinalIgnoreCase)
                || TryNumber(query, out var bytes) && region.RegionSize == bytes
                || ContainsAddress(query, region.BaseAddress, region.RegionSize),
            ProcessHandleInfo entry => TryNumber(query, out var value) && entry.Value == value,
            _ => false
        };
    }

    private static bool ContainsAddress(string query, ulong start, ulong length)
        => TryNumber(query, out var address) && address >= start && address - start < length;

    private static bool TryNumber(string query, out ulong number)
    {
        bool hex = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return ulong.TryParse(hex ? query.AsSpan(2) : query.AsSpan(), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer,
            CultureInfo.InvariantCulture, out number);
    }

    private void SelectionUpdated()
    {
        if (applyingRows) return;
        signature = null; signatureTime = null;
        if (!ReferenceEquals(observationHandle, SelectedHandle)) ClearObservation();
        UpdateDetail(); UpdateAvailability();
    }
}
