param(
  # Re-export golden fixtures from the hosted repo first (read-only on its local MySQL database).
  [switch]$Export,
  # Optional copy of a local Racinage Free database to migrate. Never pass the live database.
  [string]$DatabaseCopy = '',
  # Serve an isolated test instance (temporary data folder) until stopped, for browser checks.
  [switch]$Serve,
  [string]$ServeRoot = '',
  [string]$ServeLang = ''
)

$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$desktopRoot = Resolve-Path (Join-Path $testRoot '..\..')
$nativeRoot = Join-Path $desktopRoot 'native-host'
$fixtures = Join-Path $testRoot 'fixtures'
$php = 'C:\wamp64\bin\php\php8.5.0\php.exe'

if ($Export) {
  & $php (Join-Path $testRoot 'export_hosted_fixtures.php') 'C:\wamp64\www\fallax_projects\racinage' $fixtures
  if ($LASTEXITCODE -ne 0) { throw 'Fixture export failed.' }
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$webViewRoot = Join-Path $nugetRoot 'microsoft.web.webview2\1.0.4022.49'
$coreDll = Join-Path $webViewRoot 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$formsDll = Join-Path $webViewRoot 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
$sqliteDll = Join-Path $nugetRoot 'sqlitepclraw.lib.e_sqlite3\2.1.6\runtimes\win-x64\native\e_sqlite3.dll'

$work = Join-Path ([IO.Path]::GetTempPath()) ('racinage-free-tree-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $exe = Join-Path $work 'FamilyTreeSelfCheck.exe'
  $sources = @('Program.cs', 'FamilyTree.cs', 'FamilyTreeApi.cs', 'FamilyTreePage.cs', 'ShareCore.cs', 'FloatyCore.cs', 'AiCompanion.cs', 'ConnectedMessaging.cs') | ForEach-Object { Join-Path $nativeRoot $_ }
  & $csc /nologo /nowarn:0649 /target:exe /platform:x64 /out:$exe /main:RacinageFreeDesktop.FamilyTreeSelfCheck `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Security.dll `
    /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
    /resource:"$(Join-Path $desktopRoot 'database\2026_11_06_local_family_tree.sql'),RacinageFree.LocalFamilyTree.sql" `
    /reference:$coreDll /reference:$formsDll @sources (Join-Path $testRoot 'FamilyTreeSelfCheck.cs') (Join-Path $testRoot 'TreeApiCheck.cs')
  if ($LASTEXITCODE -ne 0) { throw 'The family tree self-check did not compile.' }
  Copy-Item -LiteralPath $sqliteDll, $coreDll, $formsDll -Destination $work
  $treeAssets = Join-Path $work 'tree'
  New-Item -ItemType Directory -Path $treeAssets | Out-Null
  Copy-Item -Path (Join-Path $desktopRoot 'tree\assets\*') -Destination $treeAssets
  Copy-Item -LiteralPath (Join-Path $desktopRoot 'tree\tree_page.html'), (Join-Path $desktopRoot 'tree\tree_adapter.js'), (Join-Path $desktopRoot 'tree\tree_adapter.css') -Destination $treeAssets
  if ($Serve) { & $exe --serve $ServeRoot $ServeLang; return }
  $arguments = @($fixtures)
  if ($DatabaseCopy -ne '') { $arguments += (Resolve-Path -LiteralPath $DatabaseCopy).Path }
  & $exe @arguments
  if ($LASTEXITCODE -ne 0) { throw 'Family tree checks failed.' }
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
