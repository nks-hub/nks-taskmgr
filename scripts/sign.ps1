#requires -Version 7.2
param(
    [string]$AppDirectory = '',
    [string]$SetupPath = '',
    [ValidatePattern('^([0-9a-fA-F]{40})?$')][string]$SigningCertificateThumbprint = '',
    [string]$SignTool = '',
    [string]$TimestampServer = ''
)

$ErrorActionPreference = 'Stop'

function Get-NksOwnedArtifactNames {
    @('NksTaskMgr.WinUI.exe', 'nks-monitor.dll', 'nks-icon-worker.exe', 'nks-details-worker.exe',
      'nks-collector-worker.exe', 'nks-actions-worker.exe', 'nks-service-actions-worker.exe',
      'nks-dump-worker.exe', 'nks-dump-worker-x86.exe', 'nks-elevation-broker.exe', 'nks-update-worker.exe')
}

function Get-NksSpkiSha256([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    [void][Reflection.Assembly]::Load('System.Formats.Asn1')
    $reader = [System.Formats.Asn1.AsnReader]::new([System.ReadOnlyMemory[byte]]::new($Certificate.RawData), [System.Formats.Asn1.AsnEncodingRules]::DER)
    $certificateSequence = $reader.ReadSequence()
    $body = $certificateSequence.ReadSequence()
    $versionTag = [System.Formats.Asn1.Asn1Tag]::new([System.Formats.Asn1.TagClass]::ContextSpecific, 0, $true)
    if ($body.PeekTag().HasSameClassAndValue($versionTag)) { [void]$body.ReadEncodedValue() }
    # RFC 5280: SubjectPublicKeyInfo follows the optional version and five fields.
    for ($i = 0; $i -lt 5; ++$i) { [void]$body.ReadEncodedValue() }
    $spki = $body.ReadEncodedValue().ToArray()
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($spki))
}

function Get-NksSigningContext([string]$Thumbprint, [string]$Tool, [string]$Timestamp) {
    if ($Thumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'Provide one CurrentUser certificate thumbprint for signing.' }
    if (![IO.Path]::IsPathFullyQualified($Tool) -or [IO.Path]::GetFileName($Tool) -ine 'signtool.exe') { throw 'Provide the full path to Microsoft signtool.exe.' }
    $toolPath = (Resolve-Path -LiteralPath $Tool).Path
    $toolSignature = Get-AuthenticodeSignature -LiteralPath $toolPath
    $toolVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($toolPath)
    if ($toolSignature.Status -ne 'Valid' -or !$toolSignature.SignerCertificate -or
        $toolSignature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)' -or
        $toolVersion.OriginalFilename -ine 'signtool.exe') { throw 'The signing tool must have a valid Microsoft signature and the SignTool PE role.' }
    if ($Timestamp) {
        $uri = $null
        if (![Uri]::TryCreate($Timestamp, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https' -or
            $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'TimestampServer must be an explicit HTTPS URL without credentials, query, or fragment.' }
    }
    $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $Thumbprint) -ErrorAction Stop
    $now = [DateTime]::UtcNow
    if (!$certificate.HasPrivateKey -or $certificate.NotBefore.ToUniversalTime() -gt $now -or $certificate.NotAfter.ToUniversalTime() -le $now) {
        throw 'The selected CurrentUser signing certificate needs an available private key and a valid lifetime.'
    }
    $usageExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    $usages = if ($usageExtension) { [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usageExtension, $usageExtension.Critical) } else { $null }
    if (!$usages -or @($usages.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) { throw 'The selected certificate must include the Code Signing EKU.' }
    [pscustomobject]@{ Thumbprint=$Thumbprint.ToUpperInvariant(); Pin=(Get-NksSpkiSha256 $certificate); Tool=$toolPath;
        ToolSha256=(Get-FileHash -LiteralPath $toolPath -Algorithm SHA256).Hash; Timestamp=$Timestamp }
}

function Assert-NksSigningPins($Signer, [string]$BrokerPin, [string]$UpdatePin) {
    if ($BrokerPin -ine $Signer.Pin -or $UpdatePin -ine $Signer.Pin) { throw 'The broker and updater build pins must both match the selected certificate SPKI SHA-256.' }
}

function Read-NksPinEvidence([string]$Directory) {
    $path = Join-Path $Directory 'build-pins.json'
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.Length -gt 1048576) { throw 'The build pin evidence exceeds its size limit.' }
    $evidence = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($evidence.schemaVersion -ne 1 -or $evidence.sourceRevision -notmatch '^[0-9a-fA-F]{40}$' -or
        $evidence.brokerPublisherSpkiSha256 -notmatch '^([0-9a-fA-F]{64})?$' -or
        $evidence.updatePublisherSpkiSha256 -notmatch '^([0-9a-fA-F]{64})?$') { throw 'Invalid public build pin evidence.' }
    $evidence
}

function Assert-NksArtifactRoles([string]$Directory, [string]$Revision) {
    $guiVersion = $null
    foreach ($name in @('NksTaskMgr.WinUI.exe', 'nks-update-worker.exe')) {
        $path = Join-Path $Directory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        $description = if ($name -eq 'NksTaskMgr.WinUI.exe') { 'NKS Task Manager' } else { 'NKS Task Manager Update Worker' }
        $original = if ($name -eq 'NksTaskMgr.WinUI.exe') { 'NksTaskMgr.WinUI.dll' } else { $name }
        if (!$info.FileVersion -or $info.ProductName -cne 'NKS Task Manager' -or $info.FileDescription -cne $description -or
            $info.OriginalFilename -cne $original -or $info.ProductVersion -notmatch '^\d+\.\d+\.\d+\+[0-9a-fA-F]{40}$' -or
            $info.ProductVersion.Split('+')[-1] -ine $Revision) { throw "The PE role or source revision is invalid: $name." }
        if ($name -eq 'NksTaskMgr.WinUI.exe') { $guiVersion = $info.ProductVersion }
        elseif ($guiVersion -and $info.ProductVersion -cne $guiVersion) { throw 'The GUI and update worker versions differ.' }
    }
}

function Write-NksPinEvidence([string]$Directory, [string]$Revision, [string]$BrokerPin, [string]$UpdatePin, $Signer = $null) {
    Assert-NksArtifactRoles $Directory $Revision
    $artifacts = @(foreach ($name in (Get-NksOwnedArtifactNames)) {
        $path = Join-Path $Directory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { [ordered]@{ name=$name; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } }
    })
    $value = [ordered]@{ schemaVersion=1; sourceRevision=$Revision; brokerPublisherSpkiSha256=$BrokerPin.ToUpperInvariant();
        updatePublisherSpkiSha256=$UpdatePin.ToUpperInvariant(); artifacts=$artifacts }
    if ($Signer) { $value['signing'] = [ordered]@{ certificateThumbprint=$Signer.Thumbprint; publisherSpkiSha256=$Signer.Pin;
        signToolSha256=$Signer.ToolSha256; timestampRequested=[bool]$Signer.Timestamp } }
    $value | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'build-pins.json') -Encoding utf8NoBOM
}

function Assert-NksArtifactEvidence([string]$Directory, $Evidence) {
    Assert-NksArtifactRoles $Directory $Evidence.sourceRevision
    $names = @(Get-NksOwnedArtifactNames)
    if (@($Evidence.artifacts).Count -gt $names.Count) { throw 'The build pin evidence contains too many artifacts.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Evidence.artifacts) {
        if ($item.name -notin $names -or !$seen.Add($item.name) -or $item.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid artifact in the build pin evidence.' }
        $path = Join-Path $Directory $item.name
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $item.sha256) { throw 'A published artifact differs from its build pin evidence.' }
    }
    foreach ($name in $names) { if (!$seen.Contains($name)) { throw "Missing build pin evidence for $name." } }
    $gui = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory 'NksTaskMgr.WinUI.exe'))
    if ($gui.ProductVersion -notmatch '^\d+\.\d+\.\d+\+[0-9a-fA-F]{40}$' -or $gui.ProductVersion.Split('+')[-1] -ine $Evidence.sourceRevision) {
        throw 'The application source revision differs from the public build pin evidence.'
    }
}

function Invoke-NksSignFile([string]$Path, $Signer) {
    $file = Get-Item -LiteralPath $Path
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Signing a reparse-point artifact is not allowed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    $same = $signature.Status -eq 'Valid' -and $signature.SignerCertificate -and $signature.SignerCertificate.Thumbprint -ieq $Signer.Thumbprint
    if (!$same -or ($Signer.Timestamp -and !$signature.TimeStamperCertificate)) {
        $arguments = @('sign', '/q', '/s', 'My', '/sha1', $Signer.Thumbprint, '/fd', 'SHA256')
        if ($Signer.Timestamp) { $arguments += @('/tr', $Signer.Timestamp, '/td', 'SHA256') }
        & $Signer.Tool @arguments $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Signing failed with exit code $LASTEXITCODE." }
    }
    & $Signer.Tool verify /q /pa $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed with exit code $LASTEXITCODE." }
    $verified = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($verified.Status -ne 'Valid' -or !$verified.SignerCertificate -or $verified.SignerCertificate.Thumbprint -ine $Signer.Thumbprint -or
        (Get-NksSpkiSha256 $verified.SignerCertificate) -ine $Signer.Pin -or ($Signer.Timestamp -and !$verified.TimeStamperCertificate)) {
        throw 'The resulting artifact signature does not match the explicitly selected certificate.'
    }
}

function Invoke-NksSignPayload([string]$Directory, $Signer) {
    $evidence = Read-NksPinEvidence $Directory
    Assert-NksSigningPins $Signer $evidence.brokerPublisherSpkiSha256 $evidence.updatePublisherSpkiSha256
    Assert-NksArtifactEvidence $Directory $evidence
    foreach ($name in (Get-NksOwnedArtifactNames)) { Invoke-NksSignFile (Join-Path $Directory $name) $Signer }
    Write-NksPinEvidence $Directory $evidence.sourceRevision $evidence.brokerPublisherSpkiSha256 $evidence.updatePublisherSpkiSha256 $Signer
}

function Invoke-NksSignSetup([string]$Path, [string]$Version, [string]$Revision, $Signer) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($info.ProductName -cne 'NKS Task Manager' -or $info.FileDescription -cne 'NKS Task Manager Setup' -or
        $info.OriginalFilename -cne 'NksTaskMgrSetup.exe' -or $info.ProductVersion -cne "$Version+$Revision") { throw 'The installer PE role or source revision does not match the package.' }
    Invoke-NksSignFile $Path $Signer
}

if ($MyInvocation.InvocationName -ne '.') {
    if (!$AppDirectory) { throw 'Provide AppDirectory containing the published build pin evidence.' }
    $directory = (Resolve-Path -LiteralPath $AppDirectory).Path
    $signer = Get-NksSigningContext $SigningCertificateThumbprint $SignTool $TimestampServer
    Invoke-NksSignPayload $directory $signer
    if ($SetupPath) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $directory 'NksTaskMgr.WinUI.exe'))
        $version, $revision = $info.ProductVersion.Split('+')
        Invoke-NksSignSetup (Resolve-Path -LiteralPath $SetupPath).Path $version $revision $signer
    }
}
