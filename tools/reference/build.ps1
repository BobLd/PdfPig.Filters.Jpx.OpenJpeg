<#
.SYNOPSIS
  Clones upstream OpenJPEG at the commit recorded in OpenJPEG/UPSTREAM.md (or -Commit) and builds
  opjdump.exe (reference sample dumper) and openjp2.dll (for the native benchmarks).
.EXAMPLE
  ./tools/reference/build.ps1                  # commit from UPSTREAM.md
  ./tools/reference/build.ps1 -Commit master   # latest upstream, e.g. to see what an update would change
  ./tools/reference/build.ps1 -Simd            # SSE2/AVX2 build of upstream, for the native benchmarks
.NOTES
  Without -Simd the C code takes its scalar paths: MSVC never defines __SSE__/__SSE2__, and gcc/clang
  only define __AVX2__ with -mavx2. Output goes to .reference/build (scalar) or .reference/build-simd.
#>
param(
    [string]$Commit,
    [switch]$Simd,
    [string]$WorkDir = (Join-Path $PSScriptRoot '../../.reference')
)
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
if (-not $Commit) {
    $upstream = Get-Content (Join-Path $repoRoot 'UglyToad.PdfPig.Filters.Jpx.OpenJpeg/OpenJPEG/UPSTREAM.md') -Raw
    if ($upstream -notmatch 'Synced to commit \| `([0-9a-f]{40})`') { throw 'No synced commit found in UPSTREAM.md' }
    $Commit = $Matches[1]
}
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$src = Join-Path $WorkDir 'openjpeg'
if (-not (Test-Path (Join-Path $src '.git'))) {
    git clone --quiet https://github.com/uclouvain/openjpeg.git $src
}
git -C $src fetch --quiet origin
git -C $src checkout --quiet $Commit
if ($LASTEXITCODE) { throw "Cannot check out $Commit" }

$build = Join-Path $WorkDir ($Simd ? 'build-simd' : 'build')
$flags = @()
if ($Simd) {
    $cflags = $IsWindows ? '/arch:AVX2 /D__SSE__ /D__SSE2__' : '-mavx2'
    $flags += "-DCMAKE_C_FLAGS=$cflags"
}
cmake -S $PSScriptRoot -B $build "-DOPENJPEG_SOURCE_DIR=$src" -DCMAKE_BUILD_TYPE=Release @flags | Out-Null
if ($LASTEXITCODE) { throw 'cmake configure failed' }
cmake --build $build --config Release --target opjdump
if ($LASTEXITCODE) { throw 'cmake build failed' }

$bin = Join-Path $build 'bin'
Write-Host "Reference build of $(git -C $src rev-parse --short HEAD) ready in $bin"
Get-ChildItem $bin | Select-Object Name, Length | Format-Table | Out-String | Write-Host
