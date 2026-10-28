param(
    [ValidateRange(10, 3600)][int]$Seconds = 60,
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
        scenario = 'desktop_default_1s_performance_view'
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
