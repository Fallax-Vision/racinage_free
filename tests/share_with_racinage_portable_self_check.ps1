$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$desktop = Join-Path $repo 'desktop\RacinageFree'
$program = Join-Path $desktop 'native-host\Program.cs'
$share = Join-Path $desktop 'native-host\ShareCore.cs'
$floaty = Join-Path $desktop 'native-host\FloatyCore.cs'
$floatyMigration = Join-Path $desktop 'database\2026_08_16_local_floaty_quick_expenses.sql'
$manifestTemplate = Join-Path $desktop 'sparse-package\AppxManifest.xml.template'
$migration = Join-Path $desktop 'database\2026_08_15_local_share_actions.sql'

foreach ($required in @($program, $share, $floaty, $floatyMigration, $manifestTemplate, $migration, (Join-Path $desktop 'share-target\Program.cs'))) {
  if (!(Test-Path -LiteralPath $required)) { throw "Missing Share with Racinage source: $required" }
}

$floatySql = Get-Content -Raw -LiteralPath $floatyMigration
foreach ($table in @('local_floaty_windows', 'local_floaty_items')) {
  if ($floatySql -notmatch [Regex]::Escape($table)) { throw "Floaty migration is missing $table." }
}
$programSource = Get-Content -Raw -LiteralPath $program
foreach ($recordType in @('quick_expenses', 'quick_expense_entries', 'quick_expense_postings')) {
  if ($programSource -notmatch $recordType) { throw "Finance Manager is missing $recordType support." }
}
if ($programSource -notmatch 'post_quick_expense' -or $programSource -notmatch 'BEGIN IMMEDIATE') { throw 'Quick Expense posting is not routed through the typed transactional operation.' }

[xml]$manifest = (Get-Content -Raw -LiteralPath $manifestTemplate).Replace('__PUBLISHER__', 'CN=Self Check')
$ns = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
$ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
$shareTarget = $manifest.SelectSingleNode('//uap:Extension[@Category="windows.shareTarget"]/uap:ShareTarget', $ns)
if ($null -eq $shareTarget) { throw 'The sparse package has no windows.shareTarget extension.' }
$formats = @($shareTarget.SelectNodes('uap:DataFormat', $ns) | ForEach-Object { $_.InnerText })
if ($formats -notcontains 'Uri' -or $formats -notcontains 'Text' -or $formats -contains 'StorageItems') { throw 'The Share Target formats are not limited to URI/text v1.' }

$sql = Get-Content -Raw -LiteralPath $migration
foreach ($table in @('local_share_receipts', 'local_share_deliveries', 'local_kitchen_imports', 'local_kitchen_extraction_runs')) {
  if ($sql -notmatch [Regex]::Escape($table)) { throw "Migration is missing $table." }
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw 'The .NET Framework C# compiler is unavailable.' }
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$webView = Join-Path $nuget 'microsoft.web.webview2\1.0.4022.49'
$core = Join-Path $webView 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$forms = Join-Path $webView 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('racinage-share-self-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
  $output = Join-Path $temp 'ShareCoreSelfCheck.exe'
  & $csc /nologo /target:exe /platform:x64 /main:ShareCoreSelfCheck /out:$output `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Security.dll `
    /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
    /reference:$core /reference:$forms $program $share $floaty (Join-Path $desktop 'native-host\AiCompanion.cs') (Join-Path $desktop 'native-host\ConnectedMessaging.cs') (Join-Path $PSScriptRoot 'ShareCoreSelfCheck.cs')
  if ($LASTEXITCODE -ne 0) { throw 'The portable Share Core sources did not compile.' }
  Copy-Item -LiteralPath $core -Destination $temp
  Copy-Item -LiteralPath $forms -Destination $temp
  & $output
  if ($LASTEXITCODE -ne 0) { throw 'The portable Share Core behavioral self-check failed.' }
} finally {
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host 'Portable Share with Racinage self-check passed.'
