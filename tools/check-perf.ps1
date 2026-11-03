# Runs the app's built-in startup benchmark several times and fails if the median exceeds the budget.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$Budget,
    [int]$Runs = 5
)

$ErrorActionPreference = 'Stop'
$limits = Get-Content $Budget -Raw | ConvertFrom-Json
$work = Join-Path ([IO.Path]::GetTempPath()) "pdfreader-perf"
New-Item -ItemType Directory -Force $work | Out-Null
Push-Location $work
& (Join-Path $PSScriptRoot 'make-sample-pdf.ps1') -Path sample.pdf -Pages 200 | Out-Null
Pop-Location
$pdf = Join-Path $work 'sample.pdf'

$results = @()
for ($i = 0; $i -lt $Runs; $i++) {
    $out = Join-Path $work "run$i.json"
    Remove-Item $out -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath (Resolve-Path $Exe) -ArgumentList "--benchmark `"$out`" `"$pdf`"" -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "Benchmark run $i timed out" }
    if (-not (Test-Path $out)) { throw "Benchmark run $i produced no output (exit code $($p.ExitCode))" }
    $results += Get-Content $out -Raw | ConvertFrom-Json
}

function Median($values) { $s = $values | Sort-Object; $s[[int][Math]::Floor($s.Count / 2)] }

$failed = $false
foreach ($metric in 'windowShownMs', 'firstPageMs', 'workingSetMb') {
    # Skip the first (cold) run for timings; disk cache state on CI runners is unpredictable.
    $values = $results | Select-Object -Skip 1 | ForEach-Object { $_.$metric }
    $median = Median $values
    $limit = $limits.$metric
    $status = if ($median -lt 0 -or $median -gt $limit) { $failed = $true; 'FAIL' } else { 'ok' }
    "{0,-14} median {1,8} (budget {2,6})  {3}" -f $metric, $median, $limit, $status
}
if ($failed) { throw 'Performance budget exceeded' }
