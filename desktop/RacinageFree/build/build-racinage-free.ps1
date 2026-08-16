param(
  [switch]$Development
)

$ErrorActionPreference = 'Stop'

$version = '0.17.0'
$appName = 'racinage-free'
$scriptRoot = $PSScriptRoot
$projectRoot = Resolve-Path (Join-Path $scriptRoot '..\..\..')
$desktopRoot = Resolve-Path (Join-Path $scriptRoot '..')
$nativeRoot = Join-Path $desktopRoot 'native-host'
$iconFile = Join-Path $desktopRoot 'assets\racinage.ico'
$fontRoot = Join-Path $desktopRoot 'assets\fonts\inter'
$aiAssetRoot = Join-Path $desktopRoot 'assets'
$portablePluginRoot = Join-Path $desktopRoot 'plugins\finance-manager'
$shareTargetBuildRoot = Join-Path $desktopRoot 'share-target\artifacts'
$buildRoot = Join-Path $desktopRoot 'dist'
$releaseRoot = if ($Development) { Join-Path $buildRoot 'development' } else { Join-Path $projectRoot "releases\desktop\$appName-v$version" }
$stagingRoot = Join-Path $buildRoot 'staging'
$payloadZip = Join-Path $buildRoot 'app.zip'
$hostExe = Join-Path $stagingRoot 'RacinageFreeHost.exe'
$outputFile = if ($Development) { "RacinageFree-v$version-dev.exe" } else { "RacinageFree-v$version.exe" }
$outputExe = Join-Path $releaseRoot $outputFile
$signTool = $env:RACINAGE_WINDOWS_SIGNTOOL
$signingCertificate = $env:RACINAGE_WINDOWS_SIGNING_CERT_PATH
$signingPassword = $env:RACINAGE_WINDOWS_SIGNING_CERT_PASSWORD
$signingThumbprint = $env:RACINAGE_WINDOWS_SIGNING_CERT_THUMBPRINT
$timestampUrl = if ([string]::IsNullOrWhiteSpace($env:RACINAGE_WINDOWS_TIMESTAMP_URL)) { 'https://timestamp.digicert.com' } else { $env:RACINAGE_WINDOWS_TIMESTAMP_URL }

if (!$Development) {
  if ([string]::IsNullOrWhiteSpace($signTool) -or !(Test-Path -LiteralPath $signTool)) {
    throw 'Public release builds require RACINAGE_WINDOWS_SIGNTOOL.'
  }
  if ([string]::IsNullOrWhiteSpace($signingCertificate) -and [string]::IsNullOrWhiteSpace($signingThumbprint)) {
    throw 'Public release builds require a protected Windows code-signing certificate path or thumbprint.'
  }
  if (![string]::IsNullOrWhiteSpace($signingCertificate) -and !(Test-Path -LiteralPath $signingCertificate)) {
    throw 'The protected Windows code-signing certificate could not be found.'
  }
}

function Invoke-RacinageSignature {
  param([Parameter(Mandatory = $true)][string]$File)
  if ($Development) { return }
  $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $timestampUrl)
  if (![string]::IsNullOrWhiteSpace($signingCertificate)) {
    $arguments += @('/f', $signingCertificate)
    if (![string]::IsNullOrWhiteSpace($signingPassword)) { $arguments += @('/p', $signingPassword) }
  } else {
    $arguments += @('/sha1', $signingThumbprint)
  }
  $arguments += $File
  & $signTool @arguments
  if ($LASTEXITCODE -ne 0) { throw 'Windows code signing failed.' }
  & $signTool verify /pa /all $File
  if ($LASTEXITCODE -ne 0) { throw 'Windows signature verification failed.' }
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) {
  $csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (!(Test-Path -LiteralPath $csc)) {
  throw 'No C# compiler was found.'
}

$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$webViewRoot = Join-Path $nugetRoot 'microsoft.web.webview2\1.0.4022.49'
$coreDll = Join-Path $webViewRoot 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$formsDll = Join-Path $webViewRoot 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
$loaderDll = Join-Path $webViewRoot 'runtimes\win-x64\native\WebView2Loader.dll'
$sqliteDll = Join-Path $nugetRoot 'sqlitepclraw.lib.e_sqlite3\2.1.6\runtimes\win-x64\native\e_sqlite3.dll'

foreach ($required in @($coreDll, $formsDll, $loaderDll, $sqliteDll, $iconFile, (Join-Path $fontRoot 'InterVariable.woff2'), (Join-Path $fontRoot 'InterVariable-Italic.woff2'), (Join-Path $aiAssetRoot 'ai-assistant.css'), (Join-Path $aiAssetRoot 'ai-assistant.js'), (Join-Path $portablePluginRoot 'index.html'), (Join-Path $portablePluginRoot 'app.css'), (Join-Path $portablePluginRoot 'app.js'))) {
  if (!(Test-Path -LiteralPath $required)) {
    throw "Missing build dependency: $required"
  }
}

if (Test-Path -LiteralPath $buildRoot) {
  $resolvedBuild = (Resolve-Path -LiteralPath $buildRoot).Path
  $resolvedDesktop = (Resolve-Path -LiteralPath $desktopRoot).Path
  if (!$resolvedBuild.StartsWith($resolvedDesktop, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clear a build folder outside the desktop project.'
  }
  Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:$hostExe `
  /win32icon:$iconFile `
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Security.dll `
  /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
  /reference:$coreDll /reference:$formsDll (Join-Path $nativeRoot 'Program.cs') (Join-Path $nativeRoot 'ShareCore.cs') (Join-Path $nativeRoot 'AiCompanion.cs') (Join-Path $nativeRoot 'ConnectedMessaging.cs')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $hostExe)) {
  throw 'The Racinage Free native host did not compile.'
}
Invoke-RacinageSignature -File $hostExe

Copy-Item -LiteralPath $coreDll -Destination $stagingRoot -Force
Copy-Item -LiteralPath $formsDll -Destination $stagingRoot -Force
Copy-Item -LiteralPath $loaderDll -Destination $stagingRoot -Force
Copy-Item -LiteralPath $sqliteDll -Destination $stagingRoot -Force
New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $aiAssetRoot 'ai-assistant.css') -Destination (Join-Path $stagingRoot 'assets') -Force
Copy-Item -LiteralPath (Join-Path $aiAssetRoot 'ai-assistant.js') -Destination (Join-Path $stagingRoot 'assets') -Force
New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'fonts\inter') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $fontRoot 'InterVariable.woff2') -Destination (Join-Path $stagingRoot 'fonts\inter') -Force
Copy-Item -LiteralPath (Join-Path $fontRoot 'InterVariable-Italic.woff2') -Destination (Join-Path $stagingRoot 'fonts\inter') -Force
New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'plugins\finance-manager') -Force | Out-Null
foreach ($file in @('index.html', 'app.css', 'app.js')) {
  Copy-Item -LiteralPath (Join-Path $portablePluginRoot $file) -Destination (Join-Path $stagingRoot 'plugins\finance-manager') -Force
}
if (Test-Path -LiteralPath (Join-Path $shareTargetBuildRoot 'external\share-target\RacinageFreeShareTarget.exe')) {
  New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'share-target') -Force | Out-Null
  Copy-Item -Path (Join-Path $shareTargetBuildRoot 'external\share-target\*') -Destination (Join-Path $stagingRoot 'share-target') -Recurse -Force
  if (Test-Path -LiteralPath (Join-Path $shareTargetBuildRoot 'RacinageFree.ShareTarget.msix')) {
    Copy-Item -LiteralPath (Join-Path $shareTargetBuildRoot 'RacinageFree.ShareTarget.msix') -Destination (Join-Path $stagingRoot 'share-target') -Force
  }
  Copy-Item -LiteralPath (Join-Path $desktopRoot 'sparse-package\Register-RacinageFreeShareTarget.ps1') -Destination (Join-Path $stagingRoot 'share-target') -Force
}
Set-Content -LiteralPath (Join-Path $stagingRoot 'config.sample.json') -Encoding UTF8 -Value @"
{
  "app": "Racinage Free",
  "version": "$version",
  "mode": "local-lite-free",
  "server": "https://racinage.com",
  "database": "%LOCALAPPDATA%\\Racinage Free\\data\\racinage-free.sqlite",
  "media": "%LOCALAPPDATA%\\Racinage Free\\media"
}
"@

foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'concrt140.dll')) {
  $candidate = Join-Path $env:SystemRoot "System32\$dll"
  if (Test-Path -LiteralPath $candidate) {
    Copy-Item -LiteralPath $candidate -Destination (Join-Path $stagingRoot $dll) -Force
  }
}

if (Test-Path -LiteralPath $payloadZip) {
  Remove-Item -LiteralPath $payloadZip -Force
}
tar.exe -a -c -f $payloadZip -C $stagingRoot .
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $payloadZip)) {
  throw 'Unable to create the Racinage Free payload zip.'
}

if (Test-Path -LiteralPath $outputExe) {
  Remove-Item -LiteralPath $outputExe -Force
}
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:$outputExe `
  /win32icon:$iconFile `
  /reference:System.dll /reference:System.Core.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll `
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
  /resource:"$payloadZip,RacinageFree.Payload.zip" (Join-Path $nativeRoot 'Bootstrap.cs')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $outputExe)) {
  throw 'The Racinage Free bootstrap executable did not compile.'
}
Invoke-RacinageSignature -File $outputExe

$hash = (Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $releaseRoot 'checksums.txt') -Encoding Ascii -Value "$hash  $outputFile"
Write-Host ($(if ($Development) { 'Unsigned local development executable created at' } else { 'Signed Racinage Free portable executable created at' })) $outputExe
