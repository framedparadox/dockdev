<#
.SYNOPSIS
    Builds dockdev and runs the FlaUI UI tests against it.

.DESCRIPTION
    Design doc §24. These tests drive a live window through UI Automation, so they need an
    interactive, logged-in desktop that is not locked — a locked session has no rendering surface,
    and every test would fail for a reason that says nothing about the code. That is why they are
    opt-in: DOCKDEV_UITESTS gates them, this script sets it, and running the suite any other way
    skips every case rather than failing it.

    The project is not in dockdev.slnx, so nothing here runs as part of a normal build or CI pass.

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
$AppProject = Join-Path $RepoRoot 'src/dockdev/dockdev.csproj'
$TestProject = Join-Path $RepoRoot 'tests/dockdev.UITests/dockdev.UITests.csproj'

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
    Write-Host 'Building dockdev…' -ForegroundColor Cyan
    Invoke-Dotnet @('build', $AppProject, "-p:Platform=$Platform", '-c', $Configuration, '-warnaserror')
}

$tfm = 'net10.0-windows10.0.26100.0'
$runtime = "win-$($Platform.ToLowerInvariant())"
$executable = Join-Path $RepoRoot "src/dockdev/bin/$Platform/$Configuration/$tfm/$runtime/dockdev.exe"
if (-not (Test-Path -LiteralPath $executable)) {
    # -p:Platform=x64 puts the output under bin/x64; a plain build puts it under bin/. Accept both
    # so -SkipBuild works against whatever the developer last built.
    $executable = Join-Path $RepoRoot "src/dockdev/bin/$Configuration/$tfm/$runtime/dockdev.exe"
}
if (-not (Test-Path -LiteralPath $executable)) {
    throw "No dockdev.exe to test. Build it, or drop -SkipBuild. Looked for: $executable"
}

Write-Host "Testing $executable" -ForegroundColor Cyan

# DOCKDEV_UITESTS opts the suite in; DOCKDEV_EXE pins exactly which binary it drives, so the tests never
# have to guess and never silently exercise a stale build. The throwaway DOCKDEV_DATA_DIR is created
# per session by the fixture itself — see dockdevSession — so the signed-in user's real dock is never
# touched and the single-instance mutex never collides with a dockdev already running.
$env:DOCKDEV_UITESTS = '1'
$env:DOCKDEV_EXE = $executable

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
    Remove-Item Env:DOCKDEV_UITESTS -ErrorAction SilentlyContinue
    Remove-Item Env:DOCKDEV_EXE -ErrorAction SilentlyContinue

    # A test that failed mid-case can leave a window up; nothing else on the machine is called
    # dockdev, and leaving one behind would make the next run collide on the data directory.
    Get-Process dockdev -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
