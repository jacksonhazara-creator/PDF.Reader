# Generates a deterministic multi-page sample PDF (text, shapes, colors, bookmarks, links)
# for manual testing and the CI startup benchmark. No binary fixtures live in the repo.
param(
    [string]$Path = "sample.pdf",
    [int]$Pages = 60,
    # Adds a document JavaScript and a 'launch program' link on page 2, to exercise the security banner.
    [switch]$ActiveContent
)

$ErrorActionPreference = 'Stop'
$lorem = @(
    'PDF.Reader renders every page with PDFium on a background thread.',
    'Scrolling stays smooth because only visible tiles are drawn.',
    'Dark, sepia and custom page colors never change the file itself.',
    'Links are never opened automatically; you are always asked first.',
    'Embedded scripts are never executed, and attachments stay blocked.',
    'Search starts on the current page and streams results as it goes.',
    'The quick brown fox jumps over the lazy dog. 0123456789'
)

$objects = New-Object System.Collections.Generic.List[string]
function Add-Obj([string]$body) { $objects.Add($body); return $objects.Count }
function Set-Obj([int]$id, [string]$body) { $objects[$id - 1] = $body }
function Esc([string]$s) { $s.Replace('\', '\\').Replace('(', '\(').Replace(')', '\)') }

$catalog = Add-Obj ''
$pagesRoot = Add-Obj ''
$font = Add-Obj '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
$fontBold = Add-Obj '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>'

$pageIds = @()
for ($p = 1; $p -le $Pages; $p++) {
    $sb = New-Object System.Text.StringBuilder
    # colored header band (vector fill) + photo-like gradient block
    [void]$sb.Append("0.18 0.43 0.92 rg 0 742 612 50 re f`n")
    [void]$sb.Append("BT /F2 22 Tf 1 1 1 rg 40 758 Td (" + (Esc "Chapter $p") + ") Tj ET`n")
    for ($i = 0; $i -lt 10; $i++) {
        $g = 0.2 + 0.07 * $i
        [void]$sb.Append("$g 0.6 $(1 - $g) rg $(380 + $i * 18) 560 18 120 re f`n")
    }
    [void]$sb.Append("0 0 0 rg BT /F1 11 Tf 40 700 Td 15 TL`n")
    for ($line = 0; $line -lt 38; $line++) {
        $text = "$p.$($line + 1)  " + $lorem[($p + $line) % $lorem.Count]
        if ($line -lt 10) { $text = $text.Substring(0, [Math]::Min(48, $text.Length)) }
        [void]$sb.Append("(" + (Esc $text) + ") Tj T*`n")
    }
    [void]$sb.Append("ET`n0.6 g 40 40 532 0.8 re f`nBT /F1 9 Tf 0.4 g 290 26 Td ($p) Tj ET`n")
    $stream = $sb.ToString()
    $content = Add-Obj ("<< /Length $([Text.Encoding]::ASCII.GetByteCount($stream)) >>`nstream`n$stream" + "endstream")
    $annots = ''
    if ($p -eq 1) {
        $link = Add-Obj '<< /Type /Annot /Subtype /Link /Rect [40 40 300 60] /Border [0 0 0] /A << /S /URI /URI (https://github.com/) >> >>'
        $annots = " /Annots [$link 0 R]"
    }
    if ($p -eq 2 -and $ActiveContent) {
        $launch = Add-Obj '<< /Type /Annot /Subtype /Link /Rect [40 40 300 60] /Border [0 0 0] /A << /S /Launch /F (calc.exe) >> >>'
        $annots = " /Annots [$launch 0 R]"
    }
    $pageIds += Add-Obj "<< /Type /Page /Parent $pagesRoot 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 $font 0 R /F2 $fontBold 0 R >> >> /Contents $content 0 R$annots >>"
}
Set-Obj $pagesRoot ("<< /Type /Pages /Kids [" + (($pageIds | ForEach-Object { "$_ 0 R" }) -join ' ') + "] /Count $($pageIds.Count) >>")

$outlines = Add-Obj ''
$items = @(); for ($i = 0; $i -lt $Pages; $i++) { $items += Add-Obj '' }
for ($i = 0; $i -lt $Pages; $i++) {
    $prev = if ($i -gt 0) { " /Prev $($items[$i - 1]) 0 R" } else { '' }
    $next = if ($i -lt $Pages - 1) { " /Next $($items[$i + 1]) 0 R" } else { '' }
    Set-Obj $items[$i] "<< /Title (Chapter $($i + 1)) /Parent $outlines 0 R /Dest [$($pageIds[$i]) 0 R /XYZ 0 792 0]$prev$next >>"
}
Set-Obj $outlines "<< /Type /Outlines /First $($items[0]) 0 R /Last $($items[-1]) 0 R /Count $Pages >>"
$names = ''
if ($ActiveContent) {
    $js = Add-Obj '<< /S /JavaScript /JS (app.alert\(1\);) >>'
    $names = " /Names << /JavaScript << /Names [(test) $js 0 R] >> >>"
}
Set-Obj $catalog "<< /Type /Catalog /Pages $pagesRoot 0 R /Outlines $outlines 0 R$names >>"

$out = New-Object System.Text.StringBuilder "%PDF-1.7`n"
$offsets = @()
for ($i = 0; $i -lt $objects.Count; $i++) {
    $offsets += [Text.Encoding]::ASCII.GetByteCount($out.ToString())
    [void]$out.Append("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n")
}
$xref = [Text.Encoding]::ASCII.GetByteCount($out.ToString())
[void]$out.Append("xref`n0 $($objects.Count + 1)`n0000000000 65535 f `n")
foreach ($o in $offsets) { [void]$out.Append(('{0:D10} 00000 n ' -f $o) + "`n") }
[void]$out.Append("trailer`n<< /Size $($objects.Count + 1) /Root $catalog 0 R >>`nstartxref`n$xref`n%%EOF`n")
[IO.File]::WriteAllBytes((Join-Path (Get-Location) $Path), [Text.Encoding]::ASCII.GetBytes($out.ToString()))
Write-Host "Wrote $Path ($Pages pages)"
