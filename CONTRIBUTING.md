# Contributing

Thanks for helping Racinage Free. Keep changes focused on the local Windows portable app in `desktop/RacinageFree`.

## Rules

- Do not add production credentials, hosted database access, private media, or secrets.
- Keep mutable user data in `%LOCALAPPDATA%\Racinage Free`.
- Prefer small, readable C# changes over new frameworks.
- Use `-Development` for local `v0.16.0` builds. Do not publish an unsigned executable or replace the signed `v0.15.0` release.
- Keep local AI providers loopback-only, reject redirects, protect optional tokens with Windows DPAPI, and never add cloud fallback.
- Keep bundled Finance Manager source under `desktop/RacinageFree/plugins/finance-manager` offline-only and isolated behind its authenticated local bridge.
- Keep plugin installation limited to signed catalog entries whose exact ZIP checksum and reviewed portable entrypoint validate locally.
- Keep local bridge operations limited to the per-plugin allowlist delivered by the reviewed manifest. Plugin lifecycle forms must update asynchronously without replacing `#header`.
- Update `README.md` and `release-manifest.json` when release behavior changes.

## Before Opening A Pull Request

```powershell
powershell -ExecutionPolicy Bypass -File desktop\RacinageFree\build\build-racinage-free.ps1 -Development
git diff --check
```

Please include the Windows version used for testing and whether first run, relaunch, local data persistence, local provider discovery, and disconnect behavior were checked. Public release builds must be signed and verified by the protected build process.
