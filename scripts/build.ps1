param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Benchmark,
    [switch]$Fresh
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
    if ($Test) {
        & $ctest --preset $preset
        if ($LASTEXITCODE) { throw "Tests failed: $LASTEXITCODE" }
    }
    if ($Benchmark) {
        & (Join-Path $projectRoot "build\msvc\$Configuration\nks-collect-bench.exe")
        if ($LASTEXITCODE) { throw "Collection benchmark failed: $LASTEXITCODE" }
    }
} finally {
    Pop-Location
    $env:UCRTContentRoot = $previousUcrtRoot
}
