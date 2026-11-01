param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Benchmark,
    [switch]$Fresh,
    [switch]$WinUI,
    [string]$DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer was not found.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Install the Visual Studio C++ desktop workload.' }
$cmakeBin = Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'
$cmake = Join-Path $cmakeBin 'cmake.exe'
$ctest = Join-Path $cmakeBin 'ctest.exe'
if (-not (Test-Path -LiteralPath $cmake)) { throw 'Install the Visual Studio CMake component.' }

$sdkRoot = $null
foreach ($registryPath in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots', 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots')) {
    $candidate = (Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue).KitsRoot10
    if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'Lib\10.0.26100.0\ucrt\x64\ucrt.lib'))) {
        $sdkRoot = $candidate
        break
    }
}
if (-not $sdkRoot) { throw 'Install Windows SDK 10.0.26100.0 including the Universal CRT.' }

$previousUcrtRoot = $env:UCRTContentRoot
Push-Location $projectRoot
try {
    # The x86 and x64 registry views can point to different SDK installations.
    $env:UCRTContentRoot = $sdkRoot
    $configureArguments = @('--preset', 'windows-msvc')
    if ($Fresh) { $configureArguments += '--fresh' }
    & $cmake @configureArguments
    if ($LASTEXITCODE) { throw "CMake configuration failed: $LASTEXITCODE" }
    $preset = $Configuration.ToLowerInvariant()
    & $cmake --build --preset $preset --parallel 4
    if ($LASTEXITCODE) { throw "Build failed: $LASTEXITCODE" }
    & $cmake -S src\dump_worker -B build\msvc-dump-x86 -G 'Visual Studio 17 2022' -A Win32 -T 'v143,host=x64' '-DCMAKE_SYSTEM_VERSION=10.0.26100.0'
    if ($LASTEXITCODE) { throw "x86 dump helper configuration failed: $LASTEXITCODE" }
    & $cmake --build build\msvc-dump-x86 --config $Configuration --target nks-dump-worker-x86 --parallel 4
    if ($LASTEXITCODE) { throw "x86 dump helper build failed: $LASTEXITCODE" }
    if ($Test) {
        & $ctest --preset $preset
        if ($LASTEXITCODE) { throw "Tests failed: $LASTEXITCODE" }
    }
    if ($Benchmark) {
        & (Join-Path $projectRoot "build\msvc\$Configuration\nks-collect-bench.exe")
        if ($LASTEXITCODE) { throw "Collection benchmark failed: $LASTEXITCODE" }
    }
    if ($WinUI) {
        $winuiOutput = Join-Path $projectRoot 'build\winui-aot'
        $nativeMonitor = Join-Path $projectRoot "build\msvc\$Configuration\nks-monitor.dll"
        $iconWorker = Join-Path $projectRoot "build\msvc\$Configuration\nks-icon-worker.exe"
        $dumpWorkerX86 = Join-Path $projectRoot "build\msvc-dump-x86\$Configuration\nks-dump-worker-x86.exe"
        & $DotNet publish src\winui\NksTaskMgr.WinUI.csproj -c $Configuration -r win-x64 -o $winuiOutput "-p:NativeMonitorPath=$nativeMonitor" "-p:IconWorkerPath=$iconWorker" "-p:DumpWorkerX86Path=$dumpWorkerX86"
        if ($LASTEXITCODE) { throw "WinUI Native AOT publication failed: $LASTEXITCODE" }
        if ($Test) {
            $resultPath = Join-Path ([IO.Path]::GetTempPath()) ("nks-winui-" + [guid]::NewGuid().ToString('N') + '.txt')
            $candidate = Start-Process -FilePath (Join-Path $winuiOutput 'NksTaskMgr.WinUI.exe') -ArgumentList "--smoke=$resultPath" -WindowStyle Hidden -PassThru
            try {
                if (-not $candidate.WaitForExit(45000)) { $candidate.Kill(); throw 'WinUI runtime check exceeded 45 seconds.' }
                $result = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw } else { '' }
                if ($candidate.ExitCode -ne 0 -or $result -notmatch 'aot=True;ready=True;models=True;minimized=True;services=True;columns=True;') {
                    throw "WinUI runtime check failed: exit $($candidate.ExitCode). Output: $resultPath"
                }
                Write-Output "WinUI Native AOT runtime check passed. Output: $resultPath"
            } finally { $candidate.Dispose() }
        }
    }
} finally {
    Pop-Location
    $env:UCRTContentRoot = $previousUcrtRoot
}
