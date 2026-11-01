param(
    [string]$NativeDirectory,
    [string]$OutputDirectory,
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$NativeDirectory) { $NativeDirectory = Join-Path $repo 'build\development-native\Release' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'build\winui-cpp' }
$NativeDirectory = [IO.Path]::GetFullPath($NativeDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer/vswhere nebyl nalezen.' }
if (!$MSBuildPath) {
    $MSBuildPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (!$MSBuildPath -or !(Test-Path -LiteralPath $MSBuildPath)) { throw 'MSBuild s nástroji C++ nebyl nalezen.' }
$crt = $null
$installations = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
foreach ($installation in $installations) {
    $candidates = Get-ChildItem -Path (Join-Path $installation 'VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT') -Directory -ErrorAction SilentlyContinue
    $crt = $candidates | Where-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'vcruntime140.dll')).VersionInfo.FileVersion -like '14.44.*' } | Select-Object -First 1
    if ($crt) { break }
}
if (!$crt) { throw 'Chybí distribuovatelné knihovny MSVC 14.44 x64 z instalace Visual Studia.' }
foreach ($name in @('nks-monitor.dll', 'nks-collector-worker.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $NativeDirectory $name))) { throw "Chybí nativní artefakt $name." }
}
$project = Join-Path $repo 'src\winui_cpp\NksTaskMgr.WinUI.Cpp.vcxproj'
$app = Join-Path $OutputDirectory 'app'
$obj = Join-Path $OutputDirectory 'obj'
[IO.Directory]::CreateDirectory($app) | Out-Null
[IO.Directory]::CreateDirectory($obj) | Out-Null
$properties = @('/p:Configuration=Release', '/p:Platform=x64', "/p:OutDir=$app\", "/p:IntDir=$obj\", "/p:MSBuildProjectExtensionsPath=$obj\")
& $MSBuildPath $project /t:Restore /nologo /v:minimal @properties
if ($LASTEXITCODE -ne 0) { throw "Obnovení balíčků skončilo kódem $LASTEXITCODE." }
& $MSBuildPath $project /t:Build /nologo /v:minimal /m:2 @properties
if ($LASTEXITCODE -ne 0) { throw "Sestavení C++/WinUI skončilo kódem $LASTEXITCODE." }
foreach ($name in @('nks-monitor.dll', 'nks-collector-worker.exe')) {
    $source = Join-Path $NativeDirectory $name
    $target = Join-Path $app $name
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $source -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash) {
        throw "Nativní artefakt $name se při kopírování změnil."
    }
    Write-Output "$name SHA256 $sourceHash"
}
Copy-Item -LiteralPath (Join-Path $repo 'assets\app.ico') -Destination (Join-Path $app 'app.ico') -Force
foreach ($library in (Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll')) {
    $target = Join-Path $app $library.Name
    $sourceHash = (Get-FileHash -LiteralPath $library.FullName -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $library.FullName -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $sourceHash) { throw "Neshodná kopie CRT $($library.Name)." }
}
Write-Output "Přibalen MSVC CRT $((Get-Item -LiteralPath (Join-Path $crt.FullName 'vcruntime140.dll')).VersionInfo.FileVersion)."
$required = @('NksTaskMgr.WinUI.Cpp.exe', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.UI.Xaml.dll', 'NksTaskMgr.WinUI.Cpp.pri', 'Microsoft.UI.Xaml.Controls.pri')
$required += @('msvcp140.dll', 'msvcp140_atomic_wait.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $app $name))) { throw "Neúplný výstup self-contained: $name." }
}
Write-Output "Sestaveno: $(Join-Path $app 'NksTaskMgr.WinUI.Cpp.exe'). Aplikace nebyla spuštěna."
