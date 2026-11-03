# Extracts the "## [x.y.z]" section of CHANGELOG.md for the release / in-app "What's new".
param([Parameter(Mandatory)] [string]$Version, [string]$Out = 'release-notes.md')

$lines = Get-Content (Join-Path $PSScriptRoot '..\CHANGELOG.md')
$start = ($lines | Select-String -Pattern "^## \[$([regex]::Escape($Version))\]" | Select-Object -First 1).LineNumber
if (-not $start) {
    "Release $Version" | Set-Content $Out -Encoding utf8
    return
}
$section = @()
for ($i = $start; $i -lt $lines.Count -and $lines[$i] -notmatch '^## \['; $i++) { $section += $lines[$i] }
($section -join "`n").Trim() | Set-Content $Out -Encoding utf8
