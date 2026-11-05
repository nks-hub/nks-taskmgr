using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32.SafeHandles;
using NksTaskMgr.Native;

namespace NksTaskMgr.WinUI;

/// <summary>
/// The Overview tab: what the process is (file, publisher, version, command line, user, parent, start) and what it
/// uses right now (CPU, memory, disk, threads, handles). Threads, modules, memory and handles stay as tables.
/// </summary>
internal sealed partial class ProcessDetailsView
{
    private readonly Button overviewTab = Command("Přehled");
    private readonly Grid overview = new() { RowSpacing = 14, Visibility = Visibility.Collapsed };
    private readonly Grid listHeader = new() { ColumnSpacing = 12, Padding = new Thickness(10, 0, 10, 0), MinHeight = 26 };
    private readonly TextBlock overviewDescription = Label("", 13);
    private readonly Grid overviewTiles = new() { ColumnSpacing = 10 };
    private readonly Dictionary<string, TextBlock> tileValues = [];
    private readonly StackPanel overviewFacts = new() { Spacing = 10 };
    private readonly Button openLocation = Command("Otevřít umístění");
    private DispatcherQueueTimer? overviewTimer;
    private Grid? filtersPanel;
    private readonly Button elevate = Command("Spustit jako správce");
    /// <summary>Set by the main window: restarts the application elevated after an access-denied read.</summary>
    internal Action? RequestElevation { get; set; }

    private void ShowAccessDenied(bool denied)
    {
        elevate.Visibility = denied && !MainWindow.RunningElevated && RequestElevation is not null ? Visibility.Visible : Visibility.Collapsed;
        if (denied) detail.Text = "Tento proces patří jinému uživateli nebo systému. Bez práv správce ho Windows nedovolí číst.";
    }
    private Func<ProcessRow?>? liveRow;
    private Func<uint, string?>? processNameOf;
    private string? imagePath;
    private bool showingOverview = true, overviewLoaded;

    private void ConfigureOverview(StackPanel modes, Func<ProcessRow?>? live, Func<uint, string?>? nameOf)
    {
        liveRow = live; processNameOf = nameOf;
        elevate.Visibility = Visibility.Collapsed;
        Identify(elevate, "process-details-elevate", "Spustit NKS znovu jako správce");
        ToolTipService.SetToolTip(elevate, "NKS se spustí znovu jako správce po potvrzení výzvy UAC.");
        elevate.Click += (_, _) => RequestElevation?.Invoke();
        if (live?.Invoke() is { } iconRow) {
            layout.Children.Remove(title);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(new ProcessIconView { Row = iconRow, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title); layout.Children.Insert(0, head);
        }
        modes.Children.Insert(0, overviewTab);
        Identify(overviewTab, "process-details-overview", "Přehled procesu");
        overviewTab.Click += (_, _) => ShowOverview(true);
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overview.RowDefinitions.Add(new RowDefinition());
        overviewDescription.TextWrapping = TextWrapping.Wrap; overviewDescription.IsTextSelectionEnabled = true;
        overview.Children.Add(overviewDescription);
        string[] tiles = ["CPU", "Paměť", "Disk", "Vlákna", "Handly"];
        for (int i = 0; i < tiles.Length; ++i) {
            overviewTiles.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new StackPanel { Spacing = 4, Padding = new Thickness(12, 10, 12, 10) };
            panel.Children.Add(Label(tiles[i], 11));
            var value = Label("—", 18); value.Foreground = Display.Brush(0xe9eef0); panel.Children.Add(value);
            tileValues[tiles[i]] = value;
            var card = new Border { Child = panel, CornerRadius = new CornerRadius(6), Background = Display.Brush(0x1d252a) };
            Grid.SetColumn(card, i); overviewTiles.Children.Add(card);
        }
        Grid.SetRow(overviewTiles, 1); overview.Children.Add(overviewTiles);
        var facts = new ScrollViewer { Content = overviewFacts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(facts, 2); overview.Children.Add(facts);
        AutomationProperties.SetAutomationId(overview, "process-details-overview-panel");
        openLocation.Height = 30; openLocation.HorizontalAlignment = HorizontalAlignment.Left;
        openLocation.Click += (_, _) => {
            if (imagePath is null) return;
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + imagePath + "\"") { UseShellExecute = false })?.Dispose(); }
            catch (Win32Exception) { }
        };
        for (int i = 0; i < 4; ++i) {
            listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i is 0 or 3 ? 2 : 1, GridUnitType.Star) });
            var text = Label("", 11); Grid.SetColumn(text, i); listHeader.Children.Add(text);
        }
    }

    private void ShowOverview(bool visible)
    {
        if (operation is not null && visible) return;
        showingOverview = visible;
        overview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in new UIElement?[] { body, count, filtersPanel }) if (element is not null) element.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        overviewTab.Background = Display.Brush(visible ? 0x28343bu : 0x1d252au);
        UpdateListHeader(); UpdateAvailability();
        if (visible) {
            if (!overviewLoaded) { overviewLoaded = true; _ = LoadOverviewAsync(); }
            overviewTimer ??= StartOverviewTimer();
            RefreshOverviewMetrics();
        }
    }

    private void UpdateListHeader()
    {
        var headers = ProcessDetailsRow.Headers(mode);
        for (int i = 0; i < 4; ++i) ((TextBlock)listHeader.Children[i]).Text = headers[i];
    }

    private DispatcherQueueTimer StartOverviewTimer()
    {
        var timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => { if (showingOverview && !closed) RefreshOverviewMetrics(); };
        timer.Start(); Closed += (_, _) => timer.Stop();
        return timer;
    }

    private void RefreshOverviewMetrics()
    {
        if (liveRow?.Invoke() is not { } row) {
            foreach (var value in tileValues.Values) value.Text = "—";
            return;
        }
        tileValues["CPU"].Text = row.CpuText;
        tileValues["Paměť"].Text = Display.Bytes(row.WorkingBytes);
        ToolTipService.SetToolTip(tileValues["Paměť"], MainWindow.MemoryTip("Pracovní sada", row.WorkingBytes, row.PrivateBytes));
        tileValues["Disk"].Text = MainWindow.DiskText(row, out _);
        tileValues["Vlákna"].Text = row.Threads.ToString("N0", Czech);
        tileValues["Handly"].Text = row.Handles.ToString("N0", Czech);
    }

    private async Task LoadOverviewAsync()
    {
        var row = liveRow?.Invoke();
        var key = Key;
        var facts = await Task.Run(() => ProcessFacts.Read(key));
        if (closed) return;
        imagePath = facts.Path;
        string description = string.Join(" · ", new[] { facts.Description, facts.Company, facts.Version is { Length: > 0 } version ? "verze " + version : "" }.Where(part => part.Length > 0));
        overviewDescription.Text = description.Length > 0 ? description : "Popis souboru není dostupný.";
        overviewFacts.Children.Clear();
        void Fact(string label, string value, UIElement? extra = null) {
            var block = new StackPanel { Spacing = 3 };
            block.Children.Add(Label(label, 11));
            var text = Label(value.Length > 0 ? value : "Nedostupné", 12); text.Foreground = Display.Brush(0xe9eef0);
            text.TextWrapping = TextWrapping.Wrap; text.IsTextSelectionEnabled = true; block.Children.Add(text);
            if (extra is not null) block.Children.Add(extra);
            overviewFacts.Children.Add(block);
        }
        Fact("Soubor", facts.Path ?? "", facts.Path is null ? null : openLocation);
        string hidden = MainWindow.RunningElevated ? "Nedostupné" : "Nedostupné bez práv správce";
        Fact("Příkazový řádek", facts.CommandLine.Length > 0 ? facts.CommandLine : hidden);
        Fact("Uživatel", facts.User.Length > 0 ? facts.User : hidden);
        uint parent = row?.ParentPid ?? 0;
        Fact("Nadřazený proces", parent == 0 ? "" : (processNameOf?.Invoke(parent) is { Length: > 0 } name ? name + " · " : "") + "PID " + parent.ToString(Czech));
        Fact("Spuštěno", created100ns is > 0 and <= long.MaxValue ? StartedText(DateTime.FromFileTimeUtc((long)created100ns).ToLocalTime()) : "");
        Fact("Podpis", "Ověřuji…");
        var signatureBlock = (StackPanel)overviewFacts.Children[^1];
        if (facts.Path is { } path) {
            try {
                var result = await client.ReadFileSignatureAsync(key.Boot, path, CancellationToken.None);
                if (closed) return;
                ((TextBlock)signatureBlock.Children[1]).Text = result.Signature is { } signed ? signed.State switch {
                    ModuleSignatureState.Valid => "Platný · " + (signed.Publisher.Length > 0 ? signed.Publisher : "vydavatel neuveden"),
                    ModuleSignatureState.Unsigned => "Bez podpisu",
                    ModuleSignatureState.Untrusted => "Neplatný podpis" + (signed.Publisher.Length > 0 ? " · " + signed.Publisher : ""),
                    _ => "Nezjištěno"
                } : "Nezjištěno";
            } catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or ArgumentException or OperationCanceledException) {
                ((TextBlock)signatureBlock.Children[1]).Text = "Nezjištěno";
            }
        } else ((TextBlock)signatureBlock.Children[1]).Text = "Soubor není dostupný";
    }

    private static string StartedText(DateTime started)
    {
        var running = DateTime.Now - started;
        string duration = running.TotalDays >= 1 ? $"{(int)running.TotalDays} d {running.Hours} h" : running.TotalHours >= 1 ? $"{(int)running.TotalHours} h {running.Minutes} min" : $"{running.Minutes} min {running.Seconds} s";
        return started.ToString("d. M. yyyy HH:mm:ss", Czech) + " · běží " + duration;
    }
}

/// <summary>Reads what Task Manager shows about a process image, checking the exact process generation first.</summary>
internal static partial class ProcessFacts
{
    internal sealed record Facts(string? Path, string Description, string Company, string Version, string CommandLine, string User);

    internal static Facts Read(ProcessKey key)
    {
        // shortcut: the fallback names the PID without opening it, so a reuse right after the snapshot is not detected; display only.
        string? path = ProcessIcons.ImagePath(key) ?? ImageNameById(key.Pid);
        string description = "", company = "", version = "";
        if (path is not null) {
            try {
                var info = FileVersionInfo.GetVersionInfo(path);
                description = info.FileDescription?.Trim() ?? ""; company = info.CompanyName?.Trim() ?? ""; version = info.ProductVersion?.Trim() ?? info.FileVersion?.Trim() ?? "";
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        string commandLine = "", user = "";
        using var process = OpenProcess(0x1000, false, key.Pid);
        try {
            if (!process.IsInvalid && NativeMonitor.QueryProcessIdentity(process, key.Kind) is { State: SampleState.Valid } identity
                && identity.Generation == key.Generation && identity.Boot == key.Boot) {
                commandLine = CommandLine(process);
                user = User(process);
            }
        } catch (NativeMonitorException) { }
        return new Facts(path, description, company, version, commandLine, user);
    }

    /// <summary>Win32 image path from SystemProcessIdInformation (88), which needs no process handle.</summary>
    private static unsafe string? ImageNameById(uint pid)
    {
        var name = new char[32768];
        fixed (char* buffer = name) {
            byte* info = stackalloc byte[24];
            *(ulong*)info = pid; *(ushort*)(info + 8) = 0; *(ushort*)(info + 10) = (ushort)(name.Length * 2 - 2); *(char**)(info + 16) = buffer;
            if (NtQuerySystemInformation(88, info, 24, out _) != 0) return null;
            string device = new(buffer, 0, *(ushort*)(info + 8) / 2);
            var target = new char[1024];
            for (char drive = 'A'; drive <= 'Z'; ++drive) {
                fixed (char* dos = target) {
                    uint length = QueryDosDevice(drive + ":", dos, (uint)target.Length);
                    if (length == 0) continue;
                    string prefix = new(dos);
                    if (device.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) return drive + ":" + device[prefix.Length..];
                }
            }
            return null;
        }
    }

    private static Dictionary<uint, string>? objectTypes;

    /// <summary>Object type name for a handle type index, read once from ObjectTypesInformation.</summary>
    internal static string ObjectTypeName(uint index)
    {
        objectTypes ??= ObjectTypes();
        return objectTypes.TryGetValue(index, out var name) ? name : "typ " + index.ToString(CultureInfo.InvariantCulture);
    }

    // OBJECT_TYPES_INFORMATION: count, then pointer-aligned OBJECT_TYPE_INFORMATION (0x68 bytes on x64, TypeIndex at 0x5A)
    // each followed by its name buffer. TypeIndex is filled since Windows 8.1.
    private static unsafe Dictionary<uint, string> ObjectTypes()
    {
        var types = new Dictionary<uint, string>();
        int size = 64 * 1024;
        for (int attempt = 0; attempt < 4; ++attempt, size *= 2) {
            var buffer = new byte[size];
            fixed (byte* data = buffer) {
                int status = NtQueryObject(IntPtr.Zero, 3, data, size, out _);
                if (status == unchecked((int)0xC0000004)) continue;
                if (status != 0) return types;
                uint count = *(uint*)data;
                long offset = 8;
                for (uint i = 0; i < count && offset + 0x68 <= size; ++i) {
                    byte* entry = data + offset;
                    ushort length = *(ushort*)entry, maximum = *(ushort*)(entry + 2);
                    char* name = *(char**)(entry + 8);
                    if (name != null && length > 0) types[entry[0x5A]] = new string(name, 0, length / 2);
                    offset += 0x68 + ((maximum + 7) & ~7);
                }
                return types;
            }
        }
        return types;
    }

    // ProcessCommandLineInformation (60) needs only PROCESS_QUERY_LIMITED_INFORMATION on Windows 8.1 and later.
    private static unsafe string CommandLine(SafeProcessHandle process)
    {
        int size = 4096;
        for (int attempt = 0; attempt < 3; ++attempt) {
            var buffer = new byte[size];
            fixed (byte* data = buffer) {
                int status = NtQueryInformationProcess(process, 60, data, size, out int needed);
                if (status == 0) {
                    ushort length = *(ushort*)data;
                    char* text = *(char**)(data + 8);
                    return length == 0 || text == null ? "" : new string(text, 0, length / 2);
                }
                if (needed <= size || needed > 1 << 20) return "";
                size = needed;
            }
        }
        return "";
    }

    private static string User(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) return "";
        using (token) {
            try { using var identity = new WindowsIdentity(token.DangerousGetHandle()); return identity.Name; }
            catch (Exception error) when (error is SystemException) { return ""; }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess")]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int informationClass, byte* information, int length, out int returned);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDevice(string device, char* target, uint length);
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryObject(IntPtr handle, int informationClass, byte* information, int length, out int returned);
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, byte* information, int length, out int returned);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
