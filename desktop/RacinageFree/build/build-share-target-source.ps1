[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Publisher,
  [Parameter(Mandatory = $true)][string]$CertificatePath,
  [string]$CertificatePassword = ''
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Join-Path $root 'share-target'
$template = Join-Path $root 'sparse-package\AppxManifest.xml.template'
$certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
$signatureTool = $env:RACINAGE_WINDOWS_SIGNTOOL
$makeAppx = $env:RACINAGE_WINDOWS_MAKEAPPX
if ([String]::IsNullOrWhiteSpace($signatureTool) -or !(Test-Path -LiteralPath $signatureTool) -or [String]::IsNullOrWhiteSpace($makeAppx) -or !(Test-Path -LiteralPath $makeAppx)) {
  throw 'Set RACINAGE_WINDOWS_SIGNTOOL and RACINAGE_WINDOWS_MAKEAPPX to trusted Windows SDK tools.'
}

$output = Join-Path $root 'share-target\artifacts'
$packageRoot = Join-Path $output 'package'
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Assets') -Force | Out-Null
$exeManifest = (Get-Content -Raw -LiteralPath (Join-Path $source 'RacinageFreeShareTarget.exe.manifest')).Replace('__PUBLISHER__', $Publisher)
$generatedExeManifest = Join-Path $output 'RacinageFreeShareTarget.exe.manifest'
Set-Content -LiteralPath $generatedExeManifest -Encoding UTF8 -Value $exeManifest
dotnet publish (Join-Path $source 'RacinageFree.ShareTarget.csproj') -c Release -o (Join-Path $output 'external\share-target') -p:ApplicationManifest=$generatedExeManifest
if ($LASTEXITCODE -ne 0) { throw 'The Share Target companion did not compile.' }

$manifest = (Get-Content -Raw -LiteralPath $template).Replace('__PUBLISHER__', $Publisher)
Set-Content -LiteralPath (Join-Path $packageRoot 'AppxManifest.xml') -Encoding UTF8 -Value $manifest
Copy-Item -LiteralPath (Join-Path $root 'assets\icon-512.png') -Destination (Join-Path $packageRoot 'Assets\StoreLogo.png')
Copy-Item -LiteralPath (Join-Path $root 'assets\icon-512.png') -Destination (Join-Path $packageRoot 'Assets\Square44x44Logo.png')
Copy-Item -LiteralPath (Join-Path $root 'assets\icon-512.png') -Destination (Join-Path $packageRoot 'Assets\Square150x150Logo.png')

$msix = Join-Path $output 'RacinageFree.ShareTarget.msix'
& $makeAppx pack /d $packageRoot /p $msix /nv
if ($LASTEXITCODE -ne 0) { throw 'The sparse identity package could not be created.' }
$signArgs = @('sign', '/fd', 'SHA256', '/f', $certificate)
if (![String]::IsNullOrWhiteSpace($CertificatePassword)) { $signArgs += @('/p', $CertificatePassword) }
$signArgs += $msix
& $signatureTool @signArgs
if ($LASTEXITCODE -ne 0) { throw 'The sparse identity package could not be signed.' }
& $signatureTool verify /pa /all $msix
if ($LASTEXITCODE -ne 0) { throw 'The sparse identity package signature could not be verified.' }
Write-Host "Signed Share Target source output: $output"
