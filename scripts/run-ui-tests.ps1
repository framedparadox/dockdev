<#
.SYNOPSIS
    Builds DevDX and runs the FlaUI UI tests against it.

.DESCRIPTION
    Design doc §24. These tests drive a live window through UI Automation, so they need an
    interactive, logged-in desktop that is not locked — a locked session has no rendering surface,
    and every test would fail for a reason that says nothing about the code. That is why they are
    opt-in: DEVDX_UITESTS gates them, this script sets it, and running the suite any other way
    skips every case rather than failing it.

    The project is not in DevDX.slnx, so nothing here runs as part of a normal build or CI pass.

.PARAMETER Platform
    x64 (default) or ARM64. Must match the machine — these run the binary, not just compile it.

.PARAMETER Configuration
    Release by default: it is what ships, and what the tests should therefore be exercising.

.PARAMETER Filter
    Passed to `dotnet test --filter`. e.g. -Filter "FullyQualifiedName~Transforms".

.PARAMETER SkipBuild
    Run against whatever is already built.

.EXAMPLE
    ./scripts/run-ui-tests.ps1

.EXAMPLE
    ./scripts/run-ui-tests.ps1 -Filter "DisplayName~Json"
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [string] $Configuration = 'Release',

    [string] $Filter,

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $RepoRoot 'src/DevDX/DevDX.csproj'
$TestProject = Join-Path $RepoRoot 'tests/DevDX.UITests/DevDX.UITests.csproj'

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    Write-Host "  dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

# A locked or disconnected session renders nothing, so every window these tests look for would be
# missing. Better to say so up front than to hand back nineteen failures that mean "the screen was
# off". Not a hard stop: a remote session can be legitimately interactive.
if (-not [Environment]::UserInteractive) {
    throw 'This session is not interactive. The UI tests need a real, unlocked desktop.'
}

if (-not $SkipBuild) {
    Write-Host 'Building DevDX…' -ForegroundColor Cyan
    Invoke-Dotnet @('build', $AppProject, "-p:Platform=$Platform", '-c', $Configuration, '-warnaserror')
}

$tfm = 'net10.0-windows10.0.26100.0'
$runtime = "win-$($Platform.ToLowerInvariant())"
$executable = Join-Path $RepoRoot "src/DevDX/bin/$Platform/$Configuration/$tfm/$runtime/DevDX.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    # -p:Platform=x64 puts the output under bin/x64; a plain build puts it under bin/. Accept both
    # so -SkipBuild works against whatever the developer last built.
    $executable = Join-Path $RepoRoot "src/DevDX/bin/$Configuration/$tfm/$runtime/DevDX.exe"
}
if (-not (Test-Path -LiteralPath $executable)) {
    throw "No DevDX.exe to test. Build it, or drop -SkipBuild. Looked for: $executable"
}

Write-Host "Testing $executable" -ForegroundColor Cyan

# DEVDX_UITESTS opts the suite in; DEVDX_EXE pins exactly which binary it drives, so the tests never
# have to guess and never silently exercise a stale build. The throwaway DEVDX_DATA_DIR is created
# per session by the fixture itself — see DevDxSession — so the signed-in user's real dock is never
# touched and the single-instance mutex never collides with a DevDX already running.
$env:DEVDX_UITESTS = '1'
$env:DEVDX_EXE = $executable

try {
    $arguments = @(
        'test', $TestProject,
        "-p:Platform=$Platform",
        '-c', $Configuration,
        '--logger', 'console;verbosity=normal'
    )
    if ($Filter) { $arguments += @('--filter', $Filter) }

    Invoke-Dotnet $arguments
    Write-Host 'UI tests passed.' -ForegroundColor Green
}
finally {
    Remove-Item Env:DEVDX_UITESTS -ErrorAction SilentlyContinue
    Remove-Item Env:DEVDX_EXE -ErrorAction SilentlyContinue

    # A test that failed mid-case can leave a window up; nothing else on the machine is called
    # DevDX, and leaving one behind would make the next run collide on the data directory.
    Get-Process DevDX -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
