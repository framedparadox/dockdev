<#
.SYNOPSIS
    Publishes dockdev as a portable ZIP per architecture, with SHA-256 checksums and optional
    Authenticode signing.

.DESCRIPTION
    Design doc §25: the primary distribution channel is a portable ZIP on GitHub Releases, built
    self-contained and unpackaged so it runs like a normal desktop utility with no Windows App
    Runtime install.

    The order of operations here is the part that matters: publish, then sign, then zip, then hash.
    Signing after zipping would leave an unsigned executable inside a signed-looking archive, and
    hashing before signing would publish a checksum for a file nobody will ever download.

.PARAMETER Platform
    x64, ARM64, or Both (the default). WinUI cannot build AnyCPU, so every artifact names its
    architecture — see the Platforms property in dockdev.csproj.

.PARAMETER OutputDirectory
    Where the .zip and checksum files land. Defaults to artifacts/release off the repo root.

.PARAMETER CertificateThumbprint
    Optional. Thumbprint of a code-signing certificate in Cert:\CurrentUser\My. Mutually exclusive
    with -CertificatePath.

.PARAMETER CertificatePath
    Optional. Path to a .pfx. Prompts for its password unless -CertificatePassword is supplied.

.PARAMETER CertificatePassword
    Optional. SecureString password for -CertificatePath.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Signing without a timestamp produces a signature that stops
    validating the day the certificate expires, so this defaults to DigiCert's rather than being
    left off.

.PARAMETER SkipBuild
    Reuse whatever is already in the publish directory. For iterating on the packaging itself.

.EXAMPLE
    ./scripts/package-release.ps1
    Publishes both architectures unsigned and writes the ZIPs and SHA256SUMS.txt.

.EXAMPLE
    ./scripts/package-release.ps1 -Platform x64 -CertificateThumbprint A1B2C3...
    Publishes x64, signs dockdev.exe with that certificate, then packages it.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'Both')]
    [string] $Platform = 'Both',

    [string] $Configuration = 'Release',

    [string] $OutputDirectory,

    [string] $CertificateThumbprint,

    [string] $CertificatePath,

    [SecureString] $CertificatePassword,

    [string] $TimestampUrl = 'http://timestamp.digicert.com',

    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src/dockdev/dockdev.csproj'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $RepoRoot 'artifacts/release'
}

# ---- Helpers ---------------------------------------------------------------

function Invoke-Dotnet {
    <#
        dotnet writes its own diagnostics; what this adds is stopping. A failed publish that scrolls
        past and lets the script go on to zip a stale directory is the one outcome worth ruling out.
    #>
    param([Parameter(Mandatory)][string[]] $Arguments)

    Write-Host "  dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray

    # Out-Host, not a bare call: dotnet's stdout is pipeline output, and a bare call inside a
    # function that also returns a value quietly prepends every build line to that value. Sending
    # it to the host keeps the build log visible and the function's output to what it returns.
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Resolve-SigningCertificate {
    <#
        Returns the certificate to sign with, or $null when the caller asked for no signing.
        Validated here rather than at the point of use so an unusable certificate fails before a
        twenty-second publish rather than after it.
    #>
    if ($CertificateThumbprint -and $CertificatePath) {
        throw 'Specify -CertificateThumbprint or -CertificatePath, not both.'
    }

    if ($CertificateThumbprint) {
        $certificate = Get-ChildItem -Path Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint -eq $CertificateThumbprint }
        if (-not $certificate) {
            throw "No certificate with thumbprint '$CertificateThumbprint' in Cert:\CurrentUser\My."
        }
    }
    elseif ($CertificatePath) {
        if (-not (Test-Path -LiteralPath $CertificatePath)) {
            throw "Certificate file not found: $CertificatePath"
        }
        $password = $CertificatePassword
        if (-not $password) {
            $password = Read-Host -AsSecureString "Password for $(Split-Path -Leaf $CertificatePath)"
        }
        $certificate = Get-PfxCertificate -FilePath $CertificatePath -Password $password
    }
    else {
        return $null
    }

    if (-not $certificate.HasPrivateKey) {
        throw 'That certificate has no private key, so it cannot sign.'
    }
    return $certificate
}

function Get-ProductVersion {
    <#
        Read from the built executable rather than parsed out of the csproj: the binary is what
        ships, so its own version is the one the archive should be named after. If the two ever
        disagree, the file wins.
    #>
    param([Parameter(Mandatory)][string] $ExecutablePath)

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
    $version = $info.FileVersion
    if (-not $version) {
        throw "Could not read a version from $ExecutablePath."
    }
    # 1.0.0.0 -> 1.0.0. The fourth component is build metadata nobody puts in a release name.
    return ($version -replace '^(\d+\.\d+\.\d+)\.\d+$', '$1')
}

function New-ReleasePackage {
    param([Parameter(Mandatory)][string] $Architecture)

    $runtimeIdentifier = "win-$($Architecture.ToLowerInvariant())"
    $publishDirectory = Join-Path $RepoRoot "artifacts/publish/$runtimeIdentifier"

    Write-Host ""
    Write-Host "== $Architecture ==" -ForegroundColor Cyan

    if (-not $SkipBuild) {
        # Wiped rather than published over: a file dropped from the project stays behind forever
        # otherwise, and shipping a stale assembly is exactly the failure a release script exists
        # to prevent.
        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force
        }

        Invoke-Dotnet @(
            'publish', $ProjectPath,
            "-p:Platform=$Architecture",
            '-c', $Configuration,
            '-o', $publishDirectory,
            # Spelled out rather than relied on from the csproj so this script keeps producing a
            # portable build even if those defaults are ever changed for local development.
            '-p:SelfContained=true',
            '-p:WindowsAppSDKSelfContained=true',
            '-p:WindowsPackageType=None',
            '-warnaserror'
        )
    }

    $executable = Join-Path $publishDirectory 'dockdev.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Publish produced no dockdev.exe in $publishDirectory."
    }

    if ($certificate) {
        Write-Host "  Signing dockdev.exe" -ForegroundColor DarkGray
        $signature = Set-AuthenticodeSignature -FilePath $executable -Certificate $certificate `
            -TimestampServer $TimestampUrl -HashAlgorithm SHA256
        if ($signature.Status -ne 'Valid') {
            throw "Signing failed: $($signature.Status) - $($signature.StatusMessage)"
        }
    }

    $version = Get-ProductVersion -ExecutablePath $executable
    $archiveName = "dockdev-$version-$runtimeIdentifier.zip"
    $archivePath = Join-Path $OutputDirectory $archiveName

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Write-Host "  Packing $archiveName" -ForegroundColor DarkGray
    # The wildcard puts the files at the archive root: a ZIP that unpacks into the folder you are
    # standing in, rather than one nested inside a directory named after a runtime identifier.
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath `
        -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sizeMb = (Get-Item -LiteralPath $archivePath).Length / 1MB

    Write-Host ("  {0}  {1:N1} MB" -f $archiveName, $sizeMb) -ForegroundColor Green
    Write-Host "  sha256  $hash" -ForegroundColor DarkGray

    return [pscustomobject]@{
        Architecture = $Architecture
        Version      = $version
        Path         = $archivePath
        Name         = $archiveName
        Sha256       = $hash
        SizeBytes    = (Get-Item -LiteralPath $archivePath).Length
        Signed       = [bool] $certificate
    }
}

# ---- Run -------------------------------------------------------------------

$certificate = Resolve-SigningCertificate
if (-not $certificate) {
    Write-Host 'No certificate supplied — the executable will be unsigned.' -ForegroundColor Yellow
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$architectures = if ($Platform -eq 'Both') { @('x64', 'ARM64') } else { @($Platform) }
$packages = foreach ($architecture in $architectures) { New-ReleasePackage -Architecture $architecture }

# One checksum file for the whole release, in the format `sha256sum -c` reads, so a user can verify
# a download with the tool they already have rather than eyeballing a hex string on a web page.
$checksumPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
$lines = $packages | ForEach-Object { "$($_.Sha256)  $($_.Name)" }
Set-Content -LiteralPath $checksumPath -Value $lines -Encoding ascii

Write-Host ""
Write-Host "Wrote $($packages.Count) archive(s) to $OutputDirectory" -ForegroundColor Cyan
Write-Host "  SHA256SUMS.txt" -ForegroundColor DarkGray
$packages | Format-Table Architecture, Version, Name, Signed -AutoSize

return $packages
