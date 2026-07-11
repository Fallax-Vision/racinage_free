$ErrorActionPreference = 'Stop'

$version = '0.13.1'
$appName = 'racinage-free'
$scriptRoot = $PSScriptRoot
$projectRoot = Resolve-Path (Join-Path $scriptRoot '..\..\..')
$desktopRoot = Resolve-Path (Join-Path $scriptRoot '..')
$nativeRoot = Join-Path $desktopRoot 'native-host'
$iconFile = Join-Path $desktopRoot 'assets\racinage.ico'
$fontRoot = Join-Path $desktopRoot 'assets\fonts\inter'
$releaseRoot = Join-Path $projectRoot "releases\desktop\$appName-v$version"
$buildRoot = Join-Path $desktopRoot 'dist'
$stagingRoot = Join-Path $buildRoot 'staging'
$payloadZip = Join-Path $buildRoot 'app.zip'
$hostExe = Join-Path $stagingRoot 'RacinageFreeHost.exe'
$outputFile = "RacinageFree-v$version.exe"
$outputExe = Join-Path $releaseRoot $outputFile

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

foreach ($required in @($coreDll, $formsDll, $loaderDll, $sqliteDll, $iconFile, (Join-Path $fontRoot 'InterVariable.woff2'), (Join-Path $fontRoot 'InterVariable-Italic.woff2'))) {
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
  /reference:$coreDll /reference:$formsDll (Join-Path $nativeRoot 'Program.cs')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $hostExe)) {
  throw 'The Racinage Free native host did not compile.'
}

Copy-Item -LiteralPath $coreDll -Destination $stagingRoot -Force
Copy-Item -LiteralPath $formsDll -Destination $stagingRoot -Force
Copy-Item -LiteralPath $loaderDll -Destination $stagingRoot -Force
Copy-Item -LiteralPath $sqliteDll -Destination $stagingRoot -Force
New-Item -ItemType Directory -Path (Join-Path $stagingRoot 'fonts\inter') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $fontRoot 'InterVariable.woff2') -Destination (Join-Path $stagingRoot 'fonts\inter') -Force
Copy-Item -LiteralPath (Join-Path $fontRoot 'InterVariable-Italic.woff2') -Destination (Join-Path $stagingRoot 'fonts\inter') -Force
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

$hash = (Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $releaseRoot 'checksums.txt') -Encoding Ascii -Value "$hash  $outputFile"
Write-Host "Racinage Free portable executable created at $outputExe"
