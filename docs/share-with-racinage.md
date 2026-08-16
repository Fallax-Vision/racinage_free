# Share with Racinage Free

Racinage Free stores URI and text receipts in the local SQLite database under `%LOCALAPPDATA%\Racinage Free`. The chooser is available at the local `/share` route after login. It groups compatible actions by enabled plugin and always retains open, copy, dismiss, paste, and clipboard fallbacks.

## Portable plugin contract

Share actions arrive through the RSA-signed Racinage Free catalog and are persisted only after strict validation. The local host accepts contract version 1, up to 12 actions, `url` or `text` payloads, localized English and French plain-text labels, and `none` or `plugin_workspace` targets. Browser code cannot select native callbacks. Each action is mapped to a fixed reviewed native-host handler.

Kitchen Planner declares `import_recipe_source`. Its handler validates the active Kitchen workspace again, creates the receipt delivery and import records idempotently, then runs the traditional extractor. JSON-LD Recipe data is preferred. Microdata, common recipe ingredient/instruction classes, semantic headings, and ordered list items are conservative fallbacks. A source without both ingredients and ordered cooking steps stays Pending. Local Ollama, LM Studio, or another explicitly configured loopback provider remains optional and never falls back to hosted credits.

## Windows Share Target companion

Windows requires package identity for an unpackaged Win32 application to appear in the Share Sheet. The source therefore includes a sparse MSIX identity manifest and a small Share Target companion for `Uri`, `WebLink`, and `Text` only. See [Microsoft's receive-share guidance](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/integrate-sharesheet-receive).

The ordinary portable executable and all `%LOCALAPPDATA%\Racinage Free` paths remain unchanged. The main development build works without package identity through paste, clipboard, and the `--share-url` or `--share-text` development arguments.

To produce companion source output, use `desktop\RacinageFree\build\build-share-target-source.ps1` with an explicit publisher and protected certificate. The script requires trusted Windows SDK `MakeAppx` and `SignTool` paths. It does not create or trust certificates.

Registration uses `desktop\RacinageFree\sparse-package\Register-RacinageFreeShareTarget.ps1`. The script:

- accepts only a valid Authenticode-signed MSIX;
- verifies certificate validity and manifest publisher matching;
- rejects a self-signed certificate unless its exact thumbprint is explicitly supplied for development and installed in `CurrentUser\TrustedPeople`;
- restricts the external location to `%LOCALAPPDATA%\Racinage Free`;
- registers the signed sparse package with `Add-AppxPackage -ExternalLocation`.

No MSIX is built, signed, registered, packaged, or released automatically. Public distribution remains blocked until the project owner supplies a trusted signing certificate and explicitly authorizes a release.
