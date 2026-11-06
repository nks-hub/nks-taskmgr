#Requires -Version 7.2
<#
.SYNOPSIS
Fails when a Loc.T/Loc.F literal in the WinUI sources has no translation in one of the languages.
#>
[CmdletBinding()]
param([string]$Root = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$winui = Join-Path $Root 'src/winui'
$languages = 'en', 'de', 'sk', 'pl'

function Unescape-Tsv([string]$value) {
    [regex]::Replace($value, '\\(.)', { param($m) switch ($m.Groups[1].Value) { 't' { "`t" } 'n' { "`n" } 'r' { "`r" } default { $m.Groups[1].Value } } })
}
function Unescape-CSharp([string]$value) {
    [regex]::Replace($value, '\\(u[0-9a-fA-F]{4}|.)', {
        param($m) $e = $m.Groups[1].Value
        if ($e.Length -eq 5) { return [string][char][Convert]::ToInt32($e.Substring(1), 16) }
        switch -CaseSensitive ($e) { 't' { "`t" } 'n' { "`n" } 'r' { "`r" } '0' { "`0" } default { $e } }
    })
}

$keys = @{}
foreach ($language in $languages) {
    $keys[$language] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem (Join-Path $winui "Strings/$language") -Filter *.tsv -ErrorAction SilentlyContinue) {
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
            $tab = $line.IndexOf("`t"); if ($tab -le 0) { throw "Malformed line in $($file.FullName): $line" }
            [void]$keys[$language].Add((Unescape-Tsv $line.Substring(0, $tab)))
        }
    }
}

$problems = [Collections.Generic.List[string]]::new()
$call = [regex]'Loc\.(?<kind>[TF])\(\s*(?<prefix>[$@]*)"(?<text>(?:[^"\\]|\\.)*)"'
foreach ($file in Get-ChildItem $winui -Filter *.cs) {
    $source = [IO.File]::ReadAllText($file.FullName)
    foreach ($match in $call.Matches($source)) {
        $line = ($source.Substring(0, $match.Index) -split "`n").Count
        if ($match.Groups['prefix'].Value) { $problems.Add("$($file.Name):$line interpolated or verbatim key; use Loc.F with placeholders"); continue }
        $text = Unescape-CSharp $match.Groups['text'].Value
        foreach ($language in $languages) {
            if (!$keys[$language].Contains($text)) { $problems.Add("$($file.Name):$line missing $language translation: $text") }
        }
    }
}
if ($problems.Count) { $problems | Select-Object -First 50 | ForEach-Object { Write-Host $_ }; throw "$($problems.Count) localization problems." }
Write-Host "Localization keys complete for $($languages -join ', ')."
