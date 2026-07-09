# Contributing

Thanks for helping Racinage Free. Keep changes focused on the local Windows portable app in `desktop/RacinageFree`.

## Rules

- Do not add production credentials, hosted database access, private media, or secrets.
- Keep mutable user data in `%LOCALAPPDATA%\Racinage Free`.
- Prefer small, readable C# changes over new frameworks.
- Rebuild `RacinageFree-v0.12.2.exe` and update `checksums.txt` when release code changes.
- Update `README.md` and `release-manifest.json` when release behavior changes.

## Before Opening A Pull Request

```powershell
powershell -ExecutionPolicy Bypass -File desktop\RacinageFree\build\build-racinage-free.ps1
git diff --check
```

Please include the Windows version used for testing and whether first run, relaunch, and local data persistence were checked.
