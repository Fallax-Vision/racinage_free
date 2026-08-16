[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$PackagePath,
  [Parameter(Mandatory = $true)][string]$ExternalLocation,
  [string]$DevelopmentCertificateThumbprint = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$external = (Resolve-Path -LiteralPath $ExternalLocation).Path
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Racinage Free')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (![IO.Path]::GetFullPath($external).TrimEnd([IO.Path]::DirectorySeparatorChar).StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The sparse package external location must remain under %LOCALAPPDATA%\Racinage Free.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) {
  throw 'The Share Target identity package must have a valid Windows signature.'
}
if ($signature.SignerCertificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or $signature.SignerCertificate.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow) {
  throw 'The Share Target signing certificate is outside its validity period.'
}

$development = -not [String]::IsNullOrWhiteSpace($DevelopmentCertificateThumbprint)
if ($development) {
  $expected = $DevelopmentCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
  if ($signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expected) {
    throw 'The signed package does not match the explicitly approved development certificate.'
  }
  if (!(Test-Path -LiteralPath (Join-Path 'Cert:\CurrentUser\TrustedPeople' $expected))) {
    throw 'Install the approved development certificate in CurrentUser\TrustedPeople before registration.'
  }
} elseif ($signature.SignerCertificate.Subject -eq $signature.SignerCertificate.Issuer) {
  throw 'A self-signed package may be registered only with an explicit development certificate thumbprint.'
}

$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
  $entry = $archive.GetEntry('AppxManifest.xml')
  if ($null -eq $entry) { throw 'The sparse package has no AppxManifest.xml.' }
  $reader = New-Object IO.StreamReader($entry.Open())
  try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
$identity = $manifest.Package.Identity
if ($identity.Name -ne 'RacinageFree.ShareTarget' -or $identity.Publisher -ne $signature.SignerCertificate.Subject) {
  throw 'The sparse package identity or publisher does not match the signing certificate.'
}

Add-AppxPackage -Path $package -ExternalLocation $external -ForceApplicationShutdown
Write-Host 'Racinage Free is registered as a Windows URI and text Share Target.'
