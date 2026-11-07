<#
.SYNOPSIS
  Benchmarks the managed decoder at a baseline git ref and at the working tree, then compares them.
.DESCRIPTION
  The baseline is checked out in a temporary git worktree. The current benchmark project
  is copied over it, so both runs use the same benchmark code and only the library differs. Only public API
  is used, which keeps older refs runnable.
.EXAMPLE
  ./benchmarks/compare-versions.ps1 -Baseline master
  ./benchmarks/compare-versions.ps1 -Baseline v0.1.16.1 -Filter '*DecodeBenchmarks*' -Job short
#>
param(
    [Parameter(Mandatory)] [string]$Baseline,
    [string]$Filter = '*DecodeBenchmarks*',
    [string]$Job = 'short',
    [string]$Framework = 'net8.0',
    [double]$Threshold = 5
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$bench = 'UglyToad.PdfPig.Filters.Jpx.OpenJpeg.Benchmarks'
$out = Join-Path $root '.reference/bench'
# Outside the repo: BenchmarkDotNet finds the project by name under the repo root and rejects duplicates.
$worktree = Join-Path ([System.IO.Path]::GetTempPath()) 'pdfpig-jpx-bench-baseline'

if (Test-Path $worktree) { git -C $root worktree remove --force $worktree }
git -C $root worktree add --detach $worktree $Baseline
if ($LASTEXITCODE) { throw "Cannot check out $Baseline" }
try {
    # Refs from before the OpenJPEG sources were copied in still use the submodule.
    if (Test-Path (Join-Path $worktree '.gitmodules')) { git -C $worktree submodule update --init --quiet }
    Copy-Item -Recurse -Force (Join-Path $root $bench) $worktree
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $worktree "$bench/bin"), (Join-Path $worktree "$bench/obj")

    foreach ($run in @(@{ Name = 'baseline'; Dir = $worktree }, @{ Name = 'current'; Dir = $root })) {
        $artifacts = Join-Path $out $run.Name
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $artifacts
        Write-Host "== Benchmarking $($run.Name)"
        # BenchmarkDotNet locates the project from the working directory, so run from that checkout.
        Push-Location $run.Dir
        try {
            dotnet run -c Release --project (Join-Path $run.Dir $bench) -f $Framework -- --filter $Filter --job $Job --artifacts $artifacts
            if ($LASTEXITCODE) { throw "Benchmark run failed for $($run.Name)" }
        }
        finally { Pop-Location }
    }
}
finally {
    git -C $root worktree remove --force $worktree
}
dotnet run (Join-Path $PSScriptRoot 'compare.cs') -- (Join-Path $out 'baseline') (Join-Path $out 'current') $Threshold
exit $LASTEXITCODE
