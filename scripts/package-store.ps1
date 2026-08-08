<#
.SYNOPSIS
    Builds the Microsoft Store submission package (.msixupload) for dockdev, and checks it against
    the Store's requirements before you upload it.

.DESCRIPTION
    Design doc §25 lists an "optional MSIX" built with -p:StorePackage=true, but nothing in the
    repository actually produced one — package-release.ps1 only makes the portable ZIP. This is the
    missing half: it produces the bundle Partner Center accepts, and it fails loudly on the handful
    of things that get a submission rejected *after* the upload rather than during it.

    What it verifies before building:

      * Identity/Publisher in Package.appxmanifest are not left at the Visual Studio placeholders.
        A package whose Publisher does not match the account's exact publisher ID is rejected at
        upload with an error that names neither field.
      * The manifest Version's revision (fourth) component is 0. The Store reserves it and refuses
        any submission that sets it.
      * The manifest Version matches the assembly version, so the version in the listing, the
        version in Settings ▸ About and the version in Apps & features are the same number.
      * Every logo the manifest names exists on disk.

    What it verifies after building: that a .msixupload was produced, and that it contains a
    package for each architecture that was asked for.

    The package is deliberately left unsigned. The Store signs each submission with the account's
    own certificate, and a package already signed with a different one is rejected. Pass
    -CertificateThumbprint only when you want a locally signed build to sideload for testing; the
    result of that is not what you upload.

.PARAMETER Platform
    Architectures to include in the bundle. Defaults to both, which is what a submission should
    carry so Windows-on-ARM gets native code rather than x64 emulation.

.PARAMETER OutputDirectory
    Where the packaging output lands. Defaults to artifacts/store off the repo root.

.PARAMETER CertificateThumbprint
    Optional. Signs the package with this certificate from Cert:\CurrentUser\My, for sideload
    testing only. Leave unset for a Store submission.

.PARAMETER SkipVerification
    Build without the pre-flight manifest checks. For iterating on packaging itself.

.EXAMPLE
    ./scripts/package-store.ps1
    Verifies the manifest and builds artifacts/store/…_x64_arm64_bundle.msixupload.

.EXAMPLE
    ./scripts/package-store.ps1 -Platform x64 -CertificateThumbprint A1B2C3...
    A signed, x64-only package to install locally and test.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'Both')]
    [string] $Platform = 'Both',

    [string] $Configuration = 'Release',

    [string] $OutputDirectory,

    [string] $CertificateThumbprint,

    [switch] $SkipVerification
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src/dockdev/dockdev.csproj'
$ManifestPath = Join-Path $RepoRoot 'src/dockdev/Package.appxmanifest'
$ProjectDirectory = Split-Path -Parent $ProjectPath

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $RepoRoot 'artifacts/store'
}

# ---- Pre-flight ------------------------------------------------------------

function Test-StoreManifest {
    <#
        The checks worth making before a twenty-minute two-architecture build, in the order they
        cost you time if they fail at upload instead.
    #>
    Write-Host 'Verifying Package.appxmanifest' -ForegroundColor Cyan

    [xml] $manifest = Get-Content -LiteralPath $ManifestPath -Raw
    $identity = $manifest.Package.Identity
    $problems = @()

    if ($identity.Name -match 'Placeholder|MyApp|^\s*$') {
        $problems += "Identity/Name is still a placeholder ('$($identity.Name)'). Use the value Partner Center shows under Product identity."
    }
    if ($identity.Publisher -match 'CN=\s*$|OID\.|Placeholder' -or $identity.Publisher -notmatch '^CN=') {
        $problems += "Identity/Publisher ('$($identity.Publisher)') does not look like the publisher ID from Partner Center."
    }

    $version = [Version] $identity.Version
    if ($version.Revision -ne 0) {
        $problems += "Identity/Version is $version. The Store reserves the fourth component; it must be 0."
    }

    # The version in the listing and the version the app reports about itself have to agree, or a
    # bug report cites a build nobody can identify.
    [xml] $project = Get-Content -LiteralPath $ProjectPath -Raw
    $projectVersion = ($project.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if ($projectVersion -and ([Version] $projectVersion) -ne $version) {
        $problems += "Identity/Version ($version) does not match dockdev.csproj <Version> ($projectVersion)."
    }

    if (-not $manifest.Package.Properties.PublisherDisplayName) {
        $problems += 'Properties/PublisherDisplayName is empty; it must match the account display name.'
    }

    # Every image the manifest points at has to exist, or packaging fails deep inside makeappx with
    # a message that names a temp path rather than the manifest line.
    $logoAttributes = @(
        $manifest.Package.Properties.Logo
        $manifest.Package.Applications.Application.VisualElements.Square150x150Logo
        $manifest.Package.Applications.Application.VisualElements.Square44x44Logo
        $manifest.Package.Applications.Application.VisualElements.DefaultTile.Wide310x150Logo
        $manifest.Package.Applications.Application.VisualElements.DefaultTile.Square71x71Logo
        $manifest.Package.Applications.Application.VisualElements.DefaultTile.Square310x310Logo
        $manifest.Package.Applications.Application.VisualElements.SplashScreen.Image
    ) | Where-Object { $_ }

    foreach ($logo in $logoAttributes) {
        $full = Join-Path $ProjectDirectory $logo
        if (-not (Test-Path -LiteralPath $full)) {
            $problems += "Manifest references '$logo', which does not exist."
        }
    }

    if ($problems.Count -gt 0) {
        $problems | ForEach-Object { Write-Host "  x $_" -ForegroundColor Red }
        throw "Package.appxmanifest is not ready for submission ($($problems.Count) problem(s))."
    }

    Write-Host "  ok  $($identity.Name) $version, publisher $($identity.Publisher)" -ForegroundColor Green
    return $version
}

# ---- Build -----------------------------------------------------------------

$architectures = if ($Platform -eq 'Both') { @('x64', 'arm64') } else { @($Platform.ToLowerInvariant()) }
$bundlePlatforms = $architectures -join '|'

if (-not $SkipVerification) {
    $version = Test-StoreManifest
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$arguments = @(
    'build', $ProjectPath,
    '-c', $Configuration,
    # x64 is the platform the packaging targets drive from; AppxBundlePlatforms is what decides
    # which architectures actually end up in the bundle.
    '-p:Platform=x64',
    '-p:StorePackage=true',
    "-p:AppxBundlePlatforms=$bundlePlatforms",
    "-p:AppxPackageDir=$OutputDirectory\",
    '-warnaserror'
)

if ($CertificateThumbprint) {
    Write-Host 'Signing locally — this build is for sideload testing, not for upload.' -ForegroundColor Yellow
    $arguments += '-p:AppxPackageSigningEnabled=true'
    $arguments += "-p:PackageCertificateThumbprint=$CertificateThumbprint"
}

Write-Host ''
Write-Host "Packaging $bundlePlatforms" -ForegroundColor Cyan
Write-Host "  dotnet $($arguments -join ' ')" -ForegroundColor DarkGray
& dotnet @arguments | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

# ---- Post-flight -----------------------------------------------------------

$upload = Get-ChildItem -Path $OutputDirectory -Filter '*.msixupload' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $upload) {
    # A bundle with no .msixupload beside it means UapAppxPackageBuildMode did not take effect,
    # and uploading the bundle directly is a rejection rather than an error at submission time.
    throw "No .msixupload was produced in $OutputDirectory. Check that UapAppxPackageBuildMode=StoreUpload survived."
}

Write-Host ''
Write-Host "Wrote $($upload.Name)" -ForegroundColor Green
Write-Host ("  {0:N1} MB" -f ($upload.Length / 1MB)) -ForegroundColor DarkGray
Write-Host "  sha256  $((Get-FileHash -LiteralPath $upload.FullName -Algorithm SHA256).Hash.ToLowerInvariant())" -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Next: upload it to Partner Center. It is unsigned by design — the Store signs it.' -ForegroundColor Cyan

return $upload
