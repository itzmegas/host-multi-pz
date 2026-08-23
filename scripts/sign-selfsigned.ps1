#requires -Version 5.1
<#
.SYNOPSIS
Signs the published MultiHostPz executable with a reusable self-signed code-signing
certificate and regenerates the distribution ZIP from the signed executable.

.DESCRIPTION
Creates a self-signed code-signing certificate once, exports a backup PFX plus a
public CER for distribution, signs the single-file EXE with SHA-256 and an RFC3161
timestamp, verifies the signature, and rebuilds the ZIP so it contains only the
signed executable.

The certificate is stored in the current user's certificate store and backed up as
PFX under ./signing (git-ignored). Re-running this script reuses the same
certificate so installed certificates on friend machines keep validating.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File scripts\sign-selfsigned.ps1
#>
[CmdletBinding()]
param(
    [string]$ProjectPath = "src\MultiHostPz.App\MultiHostPz.App.csproj",
    [string]$ExePath = "",
    [string]$ZipPath = "",
    [string]$CerPath = "",
    [string]$CertSubject = "MultiHostPz",
    [int]$CertValidityYears = 10,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$fullProject = Join-Path $repoRoot $ProjectPath
if (-not (Test-Path -LiteralPath $fullProject)) { throw "Project file not found: $fullProject" }

[xml]$project = Get-Content -LiteralPath $fullProject -Raw
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
if ($version.Count -eq 0) { throw "Version was not found in: $fullProject" }
$artifactRoot = "artifacts\MultiHostPz-$($version[0])-win-x64"
if ([string]::IsNullOrWhiteSpace($ExePath)) { $ExePath = Join-Path $artifactRoot "publish\MultiHostPz.exe" }
if ([string]::IsNullOrWhiteSpace($ZipPath)) { $ZipPath = "$artifactRoot.zip" }
if ([string]::IsNullOrWhiteSpace($CerPath)) { $CerPath = "$artifactRoot.cer" }

$fullExe = Join-Path $repoRoot $ExePath
$fullZip = Join-Path $repoRoot $ZipPath
$fullCer = Join-Path $repoRoot $CerPath
$signingDir = Join-Path $repoRoot "signing"
$pfxPath = Join-Path $signingDir "MultiHostPz.pfx"
$passwordPath = Join-Path $signingDir "MultiHostPz.pfx.password.txt"

if (-not (Test-Path -LiteralPath $fullExe)) { throw "Executable not found: $fullExe" }
Write-Host "Release version: $($version[0])"

# 1. Locate signtool.exe from the Windows SDK.
$kitRoots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")
$signtool = $null
foreach ($root in $kitRoots)
{
    if (Test-Path -LiteralPath $root)
    {
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidate) { $signtool = $candidate.FullName; break }
    }
}
if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK and retry." }
Write-Host "Using signtool: $signtool"

# 2. Reuse or create the self-signed code-signing certificate.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq "CN=$CertSubject" -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert)
{
    Write-Host "Creating self-signed code-signing certificate for $CertSubject..."
    $cert = New-SelfSignedCertificate -Subject "CN=$CertSubject" -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable `
        -KeyAlgorithm RSA -KeyLength 3072 -NotAfter (Get-Date).AddYears($CertValidityYears)
}
Write-Host "Certificate: $($cert.Thumbprint) expires $($cert.NotAfter)"

# 3. Back up the PFX with a generated password and export the public CER.
New-Item -ItemType Directory -Force -Path $signingDir | Out-Null
if (-not (Test-Path -LiteralPath $passwordPath))
{
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes)
    Set-Content -LiteralPath $passwordPath -Value $password -NoNewline -Encoding Ascii
}
$password = Get-Content -LiteralPath $passwordPath -Raw

if (-not (Test-Path -LiteralPath $pfxPath))
{
    $securePassword = ConvertTo-SecureString $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null
    Write-Host "Backed up PFX: $pfxPath"
}
Export-Certificate -Cert $cert -FilePath $fullCer -Type CERT | Out-Null
Write-Host "Exported public certificate: $fullCer"

# 4. Sign the executable with SHA-256 and an RFC3161 timestamp.
& $signtool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $pfxPath /p $password /v $fullExe
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE" }

# 5. Verify the signature. A self-signed root is not yet trusted on this machine,
#    so temporarily trust it in the current-user Root store, verify, then remove it.
$storeAdded = $false
try
{
    & certutil -addstore -user Root $fullCer | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "certutil -addstore failed with exit code $LASTEXITCODE" }
    $storeAdded = $true

    $signature = Get-AuthenticodeSignature -FilePath $fullExe
    if ($signature.Status -ne "Valid") { throw "Signature verification failed: $($signature.Status)" }
    Write-Host "Signature verified: $($signature.Status) ($($signature.SignerCertificate.Subject))"
}
finally
{
    if ($storeAdded)
    {
        & certutil -delstore -user Root $CertSubject | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "certutil -delstore failed; remove $CertSubject from Root manually." }
    }
}

# 6. Rebuild the ZIP from the signed executable only.
if (Test-Path -LiteralPath $fullZip) { Remove-Item -LiteralPath $fullZip -Force }
Compress-Archive -LiteralPath $fullExe -DestinationPath $fullZip
Write-Host "Regenerated ZIP: $fullZip"

# 7. Report hashes.
$exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fullExe).Hash
$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fullZip).Hash
$cerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $fullCer).Hash
Write-Host "EXE SHA-256: $exeHash"
Write-Host "ZIP SHA-256: $zipHash"
Write-Host "CER SHA-256: $cerHash"
