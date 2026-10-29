param(
    [ValidateRange(10, 3600)][int]$Seconds = 60,
    [ValidateSet('Performance', 'Processes', 'Services')][string]$View = 'Performance',
    [ValidateSet('Win32', 'WinUI')][string]$Candidate = 'Win32',
    [string]$Resource = '',
    [string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'nks-taskmgr-performance.json')
)

$ErrorActionPreference = 'Stop'
if ($Resource -and ($Candidate -ne 'WinUI' -or $View -ne 'Performance')) { throw '-Resource requires the WinUI Performance view.' }
$executable = Join-Path (Split-Path -Parent $PSScriptRoot) $(if ($Candidate -eq 'WinUI') { 'build\winui-aot\NksTaskMgr.WinUI.exe' } else { 'build\msvc\Release\nks-taskmgr.exe' })
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the Release desktop application first.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NksDesktopBench {
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate uint PowerCallback(IntPtr context, uint type, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct PowerRecipient { public PowerCallback callback; public IntPtr context; }
    [DllImport("powrprof.dll")] public static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref PowerRecipient recipient, out IntPtr handle);
    [DllImport("powrprof.dll")] public static extern uint PowerSettingUnregisterNotification(IntPtr handle);
    public static int DisplayState(bool console = false) {
        int state = -1;
        using (var ready = new System.Threading.ManualResetEvent(false)) {
            PowerCallback callback = (context,type,data) => { if(type == 0x8013 && data != IntPtr.Zero && Marshal.ReadInt32(data,16) == 4) { state = Marshal.ReadInt32(data,20); ready.Set(); } return 0; };
            var recipient = new PowerRecipient { callback = callback };
            var setting = new Guid(console ? "6fe69556-704a-47a0-8f24-c28d936fda47" : "2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
            if (PowerSettingRegisterNotification(ref setting,2,ref recipient,out var handle) != 0) return -1;
            try { ready.WaitOne(1000); } finally { PowerSettingUnregisterNotification(handle); GC.KeepAlive(callback); }
        }
        return state;
    }
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("kernel32.dll")] public static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint state);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@

$logicalProcessors = [NksDesktopBench]::GetActiveProcessorCount(65535)
if (-not $logicalProcessors) { throw 'Cannot read the logical processor count.' }
$previousDisplay = [NksDesktopBench]::SetThreadExecutionState(2147483650)
if (-not $previousDisplay) { throw 'Cannot keep the display active during measurement.' }
$app = $null
try {
    $app = if ($Candidate -eq 'WinUI') { Start-Process -FilePath $executable -ArgumentList "--view=$View" -WindowStyle Normal -PassThru }
        else { Start-Process -FilePath $executable -WindowStyle Normal -PassThru }
    Write-Output "Desktop benchmark PID: $($app.Id)"
    Start-Sleep -Seconds 10
    $app.Refresh()
    if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'The desktop window did not start.' }
    $window = $app.MainWindowHandle
    $displayState = [NksDesktopBench]::DisplayState()
    if ($displayState -ne 1 -and $displayState -ne 2) { throw 'Wake the display before measuring UI rendering.' }
    [NksDesktopBench]::SetForegroundWindow($window) | Out-Null
    if ($Candidate -eq 'Win32' -and $View -eq 'Processes') { [NksDesktopBench]::SendMessageW($window, 0x111, [IntPtr]1, [IntPtr]::Zero) | Out-Null; Start-Sleep -Seconds 2 }
    if ($Candidate -eq 'Win32' -and $View -eq 'Services') {
        [NksDesktopBench]::SendMessageW($window, 0x111, [IntPtr]7, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Seconds 2
        $list = [NksDesktopBench]::GetDlgItem($window, 102)
        $dpi = [NksDesktopBench]::GetDpiForWindow($window)
        $point = [IntPtr](([int](20 * $dpi / 96)) -bor (([int](65 * $dpi / 96)) -shl 16))
        [NksDesktopBench]::PostMessageW($list, 0x201, [IntPtr]1, $point) | Out-Null
        [NksDesktopBench]::PostMessageW($list, 0x202, [IntPtr]::Zero, $point) | Out-Null
        Start-Sleep -Seconds 2
        if ([NksDesktopBench]::SendMessageW($list, 0x1032, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32() -ne 1) { throw 'The service detail was not selected.' }
    }
    $selectedService = ''
    if ($Candidate -eq 'WinUI' -and $View -eq 'Services') {
        Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
        $root = [Windows.Automation.AutomationElement]::FromHandle($window)
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'services-list')
        $list = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $list) { throw 'The WinUI service list was not found.' }
        $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)
        $item = $list.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $item) { throw 'No service is available for detail measurement.' }
        $selectedService = $item.Current.Name
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 2
    }
    if ($Resource) {
        Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase
        $root = [Windows.Automation.AutomationElement]::FromHandle($window)
        $name = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Jednotlivé prostředky systému')
        $inventory = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $name)
        if (-not $inventory) { throw 'The resource inventory was not found.' }
        $name = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $Resource)
        $item = $inventory.FindFirst([Windows.Automation.TreeScope]::Descendants, $name)
        $pattern = $null
        while ($item -and -not $item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $item = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
        }
        if (-not $pattern) { throw 'The requested resource is not available for measurement.' }
        $pattern.Select()
        if (-not $pattern.Current.IsSelected) { throw 'The resource was not selected.' }
        Start-Sleep -Seconds 2
    }
    $results = @()
    foreach ($phase in @('visible', 'minimized')) {
        if ($phase -eq 'minimized') { [NksDesktopBench]::ShowWindow($window, 6) | Out-Null; Start-Sleep -Seconds 2 }
        $app.Refresh()
        $beganCpu = $app.TotalProcessorTime.TotalSeconds
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $privatePeak = 0L; $workingPeak = 0L; $handlesPeak = 0; $gdiPeak = 0
        while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
            Start-Sleep -Milliseconds 250
            $app.Refresh()
            if ($app.HasExited) { throw 'Desktop application exited during measurement.' }
            if ($phase -eq 'visible' -and [NksDesktopBench]::GetForegroundWindow() -ne $window) { throw 'The measured window lost foreground.' }
            $privatePeak = [Math]::Max($privatePeak, $app.PrivateMemorySize64)
            $workingPeak = [Math]::Max($workingPeak, $app.WorkingSet64)
            $handlesPeak = [Math]::Max($handlesPeak, $app.HandleCount)
            $gdiPeak = [Math]::Max($gdiPeak, [NksDesktopBench]::GetGuiResources($app.Handle, 0))
        }
        $cpuSeconds = $app.TotalProcessorTime.TotalSeconds - $beganCpu
        $results += [ordered]@{
            phase = $phase
            seconds = $watch.Elapsed.TotalSeconds
            cpuPercentWholeMachine = $cpuSeconds / $watch.Elapsed.TotalSeconds / $logicalProcessors * 100
            peakPrivateBytes = $privatePeak
            peakWorkingSetBytes = $workingPeak
            peakHandles = $handlesPeak
            peakGdiObjects = $gdiPeak
        }
    }
    [ordered]@{
        scenario = 'desktop_default_1s_' + $View.ToLowerInvariant() + '_view'
        sourceRevision = (git -C (Split-Path -Parent $PSScriptRoot) rev-parse HEAD)
        candidate = $Candidate
        displayState = $displayState
        consoleDisplayState = [NksDesktopBench]::DisplayState($true)
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        selectedService = $selectedService
        selectedResource = $Resource
        executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        nativeMonitorSha256 = if ($Candidate -eq 'WinUI') { (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $executable) 'nks-monitor.dll') -Algorithm SHA256).Hash } else { '' }
        logicalProcessors = $logicalProcessors
        osVersion = [Environment]::OSVersion.Version.ToString()
        results = $results
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Get-Content -LiteralPath $OutputPath
} finally {
    [NksDesktopBench]::SetThreadExecutionState($previousDisplay) | Out-Null
    if ($app) {
        $app.Refresh()
        if (-not $app.HasExited) {
            $app.CloseMainWindow() | Out-Null
            if (-not $app.WaitForExit(5000)) { throw 'The benchmark application did not close within 5 seconds.' }
        }
        $app.Dispose()
    }
}
