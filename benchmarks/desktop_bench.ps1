param(
    [ValidateRange(10, 3600)][int]$Seconds = 60,
    [ValidateSet('Performance', 'Processes', 'Services')][string]$View = 'Performance',
    [string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'nks-taskmgr-performance.json')
)

$ErrorActionPreference = 'Stop'
$executable = Join-Path (Split-Path -Parent $PSScriptRoot) 'build\msvc\Release\nks-taskmgr.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the Release desktop application first.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NksDesktopBench {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("kernel32.dll")] public static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@

$logicalProcessors = [NksDesktopBench]::GetActiveProcessorCount(65535)
if (-not $logicalProcessors) { throw 'Cannot read the logical processor count.' }
$app = Start-Process -FilePath $executable -WindowStyle Normal -PassThru
Write-Output "Desktop benchmark PID: $($app.Id)"
try {
    Start-Sleep -Seconds 10
    $app.Refresh()
    if ($app.HasExited -or $app.MainWindowHandle -eq 0) { throw 'The desktop window did not start.' }
    $window = $app.MainWindowHandle
    if ($View -eq 'Processes') { [NksDesktopBench]::SendMessageW($window, 0x111, [IntPtr]1, [IntPtr]::Zero) | Out-Null; Start-Sleep -Seconds 2 }
    if ($View -eq 'Services') {
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
        executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        logicalProcessors = $logicalProcessors
        osVersion = [Environment]::OSVersion.Version.ToString()
        results = $results
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
    Get-Content -LiteralPath $OutputPath
} finally {
    $app.Refresh()
    if (-not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { throw 'The benchmark application did not close within 5 seconds.' }
    }
    $app.Dispose()
}
