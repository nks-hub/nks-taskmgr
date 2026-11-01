#requires -Version 7.2
param(
    [string]$AppDirectory = '',
    [string]$Compiler = 'ISCC.exe',
    [string]$OutputDirectory = '',
    [ValidatePattern('^([0-9a-fA-F]{40})?$')][string]$SigningCertificateThumbprint = '',
    [string]$SignTool = '',
    [string]$TimestampServer = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'sign.ps1') -AppDirectory $AppDirectory -SigningCertificateThumbprint $SigningCertificateThumbprint -SignTool $SignTool -TimestampServer $TimestampServer
$signer = $null
if ($SigningCertificateThumbprint) { $signer = Get-NksSigningContext $SigningCertificateThumbprint $SignTool $TimestampServer }
elseif ($SignTool -or $TimestampServer) { throw 'Signing options require SigningCertificateThumbprint.' }
if (!$AppDirectory) { $AppDirectory = Join-Path $projectRoot 'build\winui-aot' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'build\packages' }
$AppDirectory = (Resolve-Path -LiteralPath $AppDirectory).Path
$compilerPath = (Get-Command $Compiler -ErrorAction Stop).Source
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($required in @((Get-NksOwnedArtifactNames) + @('app.ico', 'NksTaskMgr.WinUI.pri'))) {
    if (!(Test-Path -LiteralPath (Join-Path $AppDirectory $required))) { throw "Publish the complete WinUI application first: missing $required." }
}
$information = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $AppDirectory 'NksTaskMgr.WinUI.exe'))
if ($information.ProductVersion -notmatch '^(?<version>\d+\.\d+\.\d+)\+(?<revision>[0-9a-fA-F]{40})$') { throw 'The published application must include a version and Git revision.' }
$version = $Matches.version
$revision = $Matches.revision
$pins = $null
if (Test-Path -LiteralPath (Join-Path $AppDirectory 'build-pins.json') -PathType Leaf) {
    $pins = Read-NksPinEvidence $AppDirectory
    Assert-NksArtifactEvidence $AppDirectory $pins
    if ($pins.sourceRevision -ine $revision) { throw 'The public build pin evidence belongs to another source revision.' }
} elseif ($signer) { throw 'A signed package requires build-pins.json from scripts/build.ps1.' }
$baseName = "NksTaskMgr-$version-$($revision.Substring(0,7))-win-x64"
$stage = Join-Path $projectRoot ('build\package-staging\' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'app'
New-Item -ItemType Directory -Path $payload,$OutputDirectory -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $AppDirectory -Recurse -File | Where-Object Extension -ne '.pdb') {
    $relative = [IO.Path]::GetRelativePath($AppDirectory, $file.FullName)
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $payload
$licenseDir = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party\phnt\LICENSE') -Destination (Join-Path $licenseDir 'phnt-LICENSE')

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio redistributable files are required for packaging.' }
$installations = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = $null
foreach ($installation in $installations) {
    $candidates = Get-ChildItem -Path (Join-Path $installation 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT') -Directory -ErrorAction SilentlyContinue
    $crt = $candidates | Where-Object { (Get-Item (Join-Path $_.FullName 'vcruntime140.dll')).VersionInfo.FileVersion -like '14.44.*' } | Select-Object -First 1
    if ($crt) { break }
}
if (!$crt) { throw 'Install the MSVC 14.44 x64 redistributable files through Visual Studio Installer.' }
Copy-Item -Path (Join-Path $crt.FullName '*.dll') -Destination $payload

$assetsPath = Join-Path $projectRoot 'build\dotnet\obj\NksTaskMgr.WinUI\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) {
    $notices = @($library.Value.files | Where-Object { $_ -match '(^|/)(license[^/]*|notice[^/]*|thirdpartynotices[^/]*)$' })
    foreach ($notice in $notices) {
        foreach ($root in $assets.packageFolders.PSObject.Properties.Name) {
            $source = Join-Path (Join-Path $root $library.Value.path) $notice
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
            $destination = Join-Path (Join-Path $licenseDir ($library.Name.Replace('/', '-'))) $notice
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
            break
        }
    }
}
if ($signer) { Invoke-NksSignPayload $payload $signer; $pins = Read-NksPinEvidence $payload }
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($payload,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$publicPins = if ($pins) { [ordered]@{ brokerPublisherSpkiSha256=$pins.brokerPublisherSpkiSha256; updatePublisherSpkiSha256=$pins.updatePublisherSpkiSha256 } } else { $null }
[ordered]@{ version=$version; sourceRevision=$revision; publisherPins=$publicPins; files=$files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'build-info.json') -Encoding utf8NoBOM

& $compilerPath /Qp "/DAppDirectory=$payload" "/DAppVersion=$version" "/DSourceRevision=$revision" "/DOutputDirectory=$OutputDirectory" "/DOutputBaseName=$baseName-setup" (Join-Path $projectRoot 'installer\nks-taskmgr.iss')
if ($LASTEXITCODE) { throw "Installer compilation failed: $LASTEXITCODE" }
$setup = Join-Path $OutputDirectory ($baseName + '-setup.exe')
$setupInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
if ($setupInfo.ProductName -cne 'NKS Task Manager' -or $setupInfo.FileDescription -cne 'NKS Task Manager Setup' -or
    $setupInfo.OriginalFilename -cne 'NksTaskMgrSetup.exe' -or $setupInfo.ProductVersion -cne "$version+$revision") { throw 'The compiled setup PE role does not match the payload.' }
if ($signer) {
    Invoke-NksSignSetup $setup $version $revision $signer
    [ordered]@{ schemaVersion=1; sourceRevision=$revision; productVersion="$version+$revision"; name=[IO.Path]::GetFileName($setup);
        sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash; publisherSpkiSha256=$signer.Pin; certificateThumbprint=$signer.Thumbprint }
        | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($baseName + '-signature.json')) -Encoding utf8NoBOM
}
$archive = Join-Path $OutputDirectory ($baseName + '.zip')
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive -Force
Get-Item -LiteralPath $setup,$archive | Select-Object Name,Length,FullName
