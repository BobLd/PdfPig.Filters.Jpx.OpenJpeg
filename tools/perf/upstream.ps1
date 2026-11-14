<#
.SYNOPSIS
  Times the same stages in upstream C, to compare with ab.ps1 -NoBaseline in the same session.
.DESCRIPTION
  Builds a copy of .reference/openjpeg (a git worktree, at its checked-out commit) with upstream-timers.patch
  applied and the SSE2/AVX2 flags of tools/reference/build.ps1 -Simd. Each file is decoded by opjdump in its own
  process (single-threaded); per-stage totals are summed over the files of a run, and the minimum of -Runs is
  printed. Stages: 53 horizontal, 53 vertical, the six 97 stages, sigpass, refpass, clnpass.
.EXAMPLE
  ./tools/perf/upstream.ps1 -Files MOZILLA-10719-0_3
  ./tools/perf/upstream.ps1      # the T1 three-file round (10719, 3181, 3167)
.NOTES
  Run tools/reference/build.ps1 first (it clones .reference/openjpeg). The build is reused until the upstream
  commit or the patch changes, or with -Rebuild. If the patch no longer applies, update it from a worktree:
  git -C <worktree> diff > tools/perf/upstream-timers.patch.
#>
param(
    # Sample names from the benchmark Data folder, or paths
    [string[]]$Files = @('MOZILLA-10719-0_3', 'MOZILLA-LINK-3181-0_5', 'MOZILLA-LINK-3167-0_10'),
    [int]$Runs = 7,
    [switch]$Rebuild,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) 'jpx-perf')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$ref = Join-Path $repo '.reference/openjpeg'
if (-not (Test-Path (Join-Path $ref '.git'))) { throw 'No .reference/openjpeg: run tools/reference/build.ps1 first' }
$patch = Join-Path $PSScriptRoot 'upstream-timers.patch'
$src = Join-Path $WorkDir 'upstream-src'
$build = Join-Path $WorkDir 'upstream-build'
$exe = Join-Path $build ($IsWindows ? 'bin/opjdump.exe' : 'bin/opjdump')
$stamp = "$(git -C $ref rev-parse HEAD) $((Get-FileHash $patch).Hash)"
$stampFile = Join-Path $build 'jpx-perf.stamp'

if ($Rebuild -or -not (Test-Path $exe) -or -not (Test-Path $stampFile) -or (Get-Content $stampFile) -ne $stamp) {
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    if (Test-Path $src) { git -C $ref worktree remove --force $src }
    git -C $ref worktree add --quiet --detach $src HEAD
    if ($LASTEXITCODE) { throw 'Cannot create the upstream worktree' }
    git -C $src apply $patch
    if ($LASTEXITCODE) { throw "upstream-timers.patch does not apply to $(git -C $ref rev-parse --short HEAD)" }
    $cflags = $IsWindows ? '/arch:AVX2 /D__SSE__ /D__SSE2__' : '-mavx2'
    cmake -S (Join-Path $repo 'tools/reference') -B $build "-DOPENJPEG_SOURCE_DIR=$src" -DCMAKE_BUILD_TYPE=Release "-DCMAKE_C_FLAGS=$cflags" | Out-Null
    if ($LASTEXITCODE) { throw 'cmake configure failed' }
    cmake --build $build --config Release --target opjdump | Out-Null
    if ($LASTEXITCODE) { throw 'cmake build failed (run the cmake --build command by hand to see why)' }
    Set-Content $stampFile $stamp
}

$paths = @(foreach ($f in $Files) {
    if (Test-Path $f) { (Resolve-Path $f).Path }
    else { Join-Path $repo "UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks/Data/$f.jpx" }
})
$out = Join-Path $WorkDir 'upstream-dump.bin'
$min = @{}
$order = [Collections.Generic.List[string]]::new()
for ($r = 1; $r -le $Runs; $r++) {
    $sum = @{}
    foreach ($p in $paths) {
        # stderr carries the timers; do not let it count as an error
        $lines = & { $ErrorActionPreference = 'Continue'; & $exe $p $out 2>&1 | ForEach-Object { "$_" } }
        foreach ($line in $lines) {
            if ($line -notmatch '^perf (.+) ([0-9.]+)$') { continue }
            if (-not $order.Contains($Matches[1])) { $order.Add($Matches[1]) }
            $sum[$Matches[1]] = $sum[$Matches[1]] + [double]$Matches[2]
        }
    }
    foreach ($k in $sum.Keys) { if (-not $min.ContainsKey($k) -or $sum[$k] -lt $min[$k]) { $min[$k] = $sum[$k] } }
}
Remove-Item -ErrorAction SilentlyContinue $out
Write-Host "upstream $(git -C $ref rev-parse --short HEAD), ms per run ($($Files -join ', ')), minimum of $Runs runs"
foreach ($stage in $order) { '{0,-16} {1,9:F2}' -f $stage, $min[$stage] }
