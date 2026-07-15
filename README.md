# Racinage Free

Racinage Free is the open-source Windows portable edition of Racinage for the Lite Free plan. It runs locally on one Windows device and stores family data under `%LOCALAPPDATA%\Racinage Free`. The optional Plugins tab connects only to the signed public Racinage plugin catalog and hosted purchase pages; it does not connect the local family database to the hosted Racinage database.

![Racinage Free screenshot](docs/images/racinage-free-screenshot.webp)

## Download

- Latest bundled release: [`RacinageFree-v0.13.2.exe`](releases/desktop/racinage-free-v0.13.2/RacinageFree-v0.13.2.exe)
- Version: `racinage-free-v0.13.2`
- SHA-256: see [`checksums.txt`](releases/desktop/racinage-free-v0.13.2/checksums.txt)

## What Is Included

- Native C# WinForms/WebView2 host.
- Local loopback server and embedded SQLite storage.
- Hosted-style Manage sections without collaboration controls.
- Bundled Inter variable fonts for consistent offline typography.
- Signed online catalog for reviewed local-compatible plugins, including monthly/yearly pricing and active hosted reductions, with checksum and archive-path verification.
- Sandboxed portable plugin pages and hosted links for optional Pro purchases and entitlements.
- Single-file bootstrap executable with payload refresh.
- Racinage icon, screenshot, build script, release manifest, and checksums.

This repository intentionally does not include the hosted Racinage PHP/MySQL web app, production credentials, private uploads, or paid-plan server features.

## Build From Source

Requirements:

- Windows 10 or newer.
- .NET Framework C# compiler, usually available at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
- NuGet packages in the standard global package folder:
  - `Microsoft.Web.WebView2` `1.0.4022.49`
  - `SQLitePCLRaw.lib.e_sqlite3` `2.1.6`

Build:

```powershell
powershell -ExecutionPolicy Bypass -File desktop\RacinageFree\build\build-racinage-free.ps1
```

Output:

```text
releases\desktop\racinage-free-v0.13.2\RacinageFree-v0.13.2.exe
```

## Local Data

Racinage Free keeps mutable data outside the executable:

```text
%LOCALAPPDATA%\Racinage Free\data
%LOCALAPPDATA%\Racinage Free\media
%LOCALAPPDATA%\Racinage Free\logs
%LOCALAPPDATA%\Racinage Free\webview
%LOCALAPPDATA%\Racinage Free\plugins
%LOCALAPPDATA%\Racinage Free\plugin-cache
```

Refreshing or rebuilding the same version preserves local data.

## Repository

Public repo: <https://github.com/Fallax-Vision/racinage_free>

Hosted Racinage and paid-plan features live at <https://racinage.com>.
