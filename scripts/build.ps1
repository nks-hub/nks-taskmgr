#requires -Version 7.2
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Benchmark,
    [switch]$Fresh,
    [switch]$WinUI,
    [string]$DotNet = 'dotnet',
    [string]$SentryDsn = '',
    [ValidatePattern('^[a-zA-Z0-9._-]{1,64}$')][string]$SentryEnvironment = 'production',
    [ValidatePattern('^[a-zA-Z0-9._+@-]{0,200}$')][string]$SentryRelease = '',
    [ValidatePattern('^([0-9a-fA-F]{64})?$')][string]$BrokerPublisherSpkiSha256 = '',
    [ValidatePattern('^([0-9a-fA-F]{64})?$')][string]$UpdatePublisherSpkiSha256 = '',
    [ValidatePattern('^([0-9a-fA-F]{40})?$')][string]$SigningCertificateThumbprint = '',
    [string]$SignTool = '',
    [string]$TimestampServer = ''
)

$ErrorActionPreference = 'Stop'
if ($SentryDsn) {
    if (!$WinUI) { throw 'Use -WinUI when configuring Sentry.' }
    $sentryUri = $null
    if (![Uri]::TryCreate($SentryDsn, [UriKind]::Absolute, [ref]$sentryUri) -or
        $sentryUri.Scheme -notin @('http', 'https') -or $sentryUri.UserInfo -notmatch '^[a-zA-Z0-9]+$' -or
        $sentryUri.AbsolutePath -notmatch '^(/[a-zA-Z0-9._~-]+)*/[0-9]+$' -or $sentryUri.Query -or $sentryUri.Fragment) {
        throw 'SentryDsn must be an HTTP(S) DSN with a public key and numeric project ID, without a secret key, query, or fragment.'
    }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'sign.ps1') -SigningCertificateThumbprint $SigningCertificateThumbprint -SignTool $SignTool -TimestampServer $TimestampServer
$signer = $null
if ($SigningCertificateThumbprint) {
    if (!$WinUI) { throw 'Use -WinUI when requesting a signed application payload.' }
    $signer = Get-NksSigningContext $SigningCertificateThumbprint $SignTool $TimestampServer
    foreach ($pin in @($BrokerPublisherSpkiSha256, $UpdatePublisherSpkiSha256)) {
        if ($pin -and $pin -ine $signer.Pin) { throw 'An explicit build pin differs from the selected certificate SPKI SHA-256.' }
    }
    $BrokerPublisherSpkiSha256 = $UpdatePublisherSpkiSha256 = $signer.Pin
} elseif ($SignTool -or $TimestampServer) { throw 'Signing options require SigningCertificateThumbprint.' }
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
    $sourceRevision = (& git -C $projectRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-fA-F]{40}$') { throw 'A full Git source revision is required for build pin evidence.' }
    $configureArguments = @('--preset', 'windows-msvc', "-DNKS_BROKER_PUBLISHER_SPKI_SHA256=$BrokerPublisherSpkiSha256", "-DNKS_UPDATE_PUBLISHER_SPKI_SHA256=$UpdatePublisherSpkiSha256")
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
    $cache = Get-Content -LiteralPath (Join-Path $projectRoot 'build\msvc\CMakeCache.txt')
    foreach ($definition in @(@('NKS_BROKER_PUBLISHER_SPKI_SHA256', $BrokerPublisherSpkiSha256), @('NKS_UPDATE_PUBLISHER_SPKI_SHA256', $UpdatePublisherSpkiSha256))) {
        $entry = @($cache | Where-Object { $_.StartsWith($definition[0] + ':STRING=', [StringComparison]::Ordinal) })
        if ($entry.Count -ne 1 -or $entry[0].Split('=', 2)[1] -ine $definition[1]) { throw 'The configured native publisher pin does not match the build request.' }
    }
    if ((& git -C $projectRoot rev-parse HEAD).Trim() -ine $sourceRevision) { throw 'The source revision changed during the native build. Rebuild before signing.' }
    Write-NksPinEvidence (Join-Path $projectRoot "build\msvc\$Configuration") $sourceRevision $BrokerPublisherSpkiSha256 $UpdatePublisherSpkiSha256
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
        $nativeDirectory = Join-Path $projectRoot "build\msvc\$Configuration"
        $dumpWorkerX86 = Join-Path $projectRoot "build\msvc-dump-x86\$Configuration\nks-dump-worker-x86.exe"
        $publishProperties = @{
            NativeMonitorPath='nks-monitor.dll'; IconWorkerPath='nks-icon-worker.exe'; DetailsWorkerPath='nks-details-worker.exe';
            CollectorWorkerPath='nks-collector-worker.exe'; ActionsWorkerPath='nks-actions-worker.exe'; ServiceActionsWorkerPath='nks-service-actions-worker.exe';
            DumpWorkerPath='nks-dump-worker.exe'; ElevationBrokerPath='nks-elevation-broker.exe'; UpdateWorkerPath='nks-update-worker.exe'
        }
        $publishArguments = @('publish', 'src\winui\NksTaskMgr.WinUI.csproj', '-c', $Configuration, '-r', 'win-x64', '-o', $winuiOutput,
            "-p:DumpWorkerX86Path=$dumpWorkerX86", "-p:BrokerPublisherSpkiSha256=$BrokerPublisherSpkiSha256", "-p:UpdatePublisherSpkiSha256=$UpdatePublisherSpkiSha256",
            "-p:SentryDsn=$SentryDsn", "-p:SentryEnvironment=$SentryEnvironment", "-p:SentryRelease=$SentryRelease")
        foreach ($property in $publishProperties.GetEnumerator()) { $publishArguments += "-p:$($property.Key)=$(Join-Path $nativeDirectory $property.Value)" }
        & $DotNet @publishArguments
        if ($LASTEXITCODE) { throw "WinUI Native AOT publication failed: $LASTEXITCODE" }
        foreach ($name in (Get-NksOwnedArtifactNames)) {
            if (!(Test-Path -LiteralPath (Join-Path $winuiOutput $name) -PathType Leaf)) { throw "The published payload is missing $name." }
        }
        $product = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $winuiOutput 'NksTaskMgr.WinUI.exe'))
        if ($product.ProductVersion.Split('+')[-1] -ine $sourceRevision) { throw 'The published GUI source revision differs from its build evidence.' }
        Write-NksPinEvidence $winuiOutput $sourceRevision $BrokerPublisherSpkiSha256 $UpdatePublisherSpkiSha256
        if ($signer) { Invoke-NksSignPayload $winuiOutput $signer }
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
