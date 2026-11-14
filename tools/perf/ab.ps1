<#
.SYNOPSIS
  Stage timings of a baseline commit vs the working tree, in alternating runs (minimum per side).
.DESCRIPTION
  Checks the baseline out as a git worktree outside the repository, patches the same stage timers into both
  copies (instrument.cs), builds probe.cs against each, then runs base, new, base, new, ... and prints the minimum
  per stage. A stage the change does not touch is the control: if it moves, the run is noise. Both source trees
  are restored afterwards, also on failure. Run nothing else meanwhile.
.EXAMPLE
  ./tools/perf/ab.ps1 -Probe dwt53 -Files MOZILLA-10719-0_3                 # HEAD vs working tree
  ./tools/perf/ab.ps1 -Probe t1 -Baseline e7ba98d                            # default T1 three-file round
  ./tools/perf/ab.ps1 -Probe dwt97 -Files MOZILLA-LINK-3167-0_10 -NoBaseline # working tree only (vs upstream.ps1)
#>
param(
    [Parameter(Mandatory)][ValidateSet('tile', 't1', 'dwt53', 'dwt97')][string]$Probe,
    [string]$Baseline = 'HEAD',
    # Sample names from the benchmark Data folder, or paths
    [string[]]$Files,
    [int]$Runs = 6,
    [int]$Warm = 5,
    [int]$Rounds = 10,
    [switch]$NoBaseline,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'jpx-perf')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Files) {
    $Files = switch ($Probe) {
        't1' { 'MOZILLA-10719-0_3', 'MOZILLA-LINK-3181-0_5', 'MOZILLA-LINK-3167-0_10' }
        'dwt97' { 'MOZILLA-LINK-3167-0_10' }
        default { 'MOZILLA-10719-0_3' }
    }
}
$paths = @(foreach ($f in $Files) {
    if (Test-Path $f) { (Resolve-Path $f).Path }
    else { Join-Path $repo "UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks/Data/$f.jpx" }
})
$instrument = Join-Path $repo 'tools/perf/instrument.cs'
New-Item -ItemType Directory -Force $WorkDir | Out-Null

$sides = [ordered]@{ new = $repo }
if (-not $NoBaseline) {
    $wt = Join-Path $WorkDir 'base'
    if (Test-Path $wt) { git -C $repo worktree remove --force $wt }
    git -C $repo worktree add --quiet --detach $wt $Baseline
    if ($LASTEXITCODE) { throw "Cannot check out $Baseline" }
    $sides = [ordered]@{ base = $wt; new = $repo }
}

$harness = @{}
try {
    foreach ($k in $sides.Keys) {
        dotnet run $instrument -- apply $sides[$k] $Probe
        if ($LASTEXITCODE) { throw "instrument failed for $k" }
        # Own copy of the harness per side, pointing at that side's project
        $dir = Join-Path $WorkDir "probe-$k"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $proj = (Join-Path $sides[$k] 'UglyToad.PdfPig.Filters.Jpx.OpenJpeg/UglyToad.PdfPig.Filters.Jpx.OpenJpeg.csproj') -replace '\\', '/'
        $src = Get-Content (Join-Path $repo 'tools/perf/probe.cs')
        $src[0] = "#:project $proj"
        $harness[$k] = Join-Path $dir 'probe.cs'
        Set-Content $harness[$k] $src
        dotnet build -c Release $harness[$k] | Out-Null
        if ($LASTEXITCODE) { throw "probe build failed for $k (run dotnet build on $($harness[$k]) to see why)" }
    }
    $min = @{}
    $order = [Collections.Generic.List[string]]::new()
    for ($r = 1; $r -le $Runs; $r++) {
        foreach ($k in $sides.Keys) {
            $out = dotnet run --no-build -c Release $harness[$k] -- --warm $Warm --rounds $Rounds @paths
            if ($LASTEXITCODE) { throw "probe run failed for $k" }
            foreach ($line in $out) {
                if ($line -notmatch '^perf (.+) ([0-9.]+)$') { continue }
                $stage = $Matches[1]; $v = [double]$Matches[2]
                if (-not $order.Contains($stage)) { $order.Add($stage) }
                $key = "$k|$stage"
                if (-not $min.ContainsKey($key) -or $v -lt $min[$key]) { $min[$key] = $v }
            }
        }
        Write-Host "run $r/$Runs done"
    }
}
finally {
    foreach ($k in $sides.Keys) { dotnet run $instrument -- restore $sides[$k] }
}

Write-Host "`n$Probe, ms per round ($($Files -join ', ')), minimum of $Runs runs, $Rounds rounds after $Warm warm-up"
foreach ($stage in $order) {
    $n = $min["new|$stage"]
    if ($NoBaseline) { '{0,-16} {1,9:F2}' -f $stage, $n }
    else {
        $b = $min["base|$stage"]
        '{0,-16} base {1,9:F2}  new {2,9:F2}  {3,6:+0.0;-0.0}%' -f $stage, $b, $n, (100 * ($n - $b) / $b)
    }
}
if (-not $NoBaseline) { Write-Host "Baseline worktree kept in $wt (git worktree remove --force `"$wt`" to delete)" }
