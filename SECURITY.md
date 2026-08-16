# Security

Report security issues privately to Fallax Vision before opening a public issue.

Please include:

- A short description of the issue.
- Steps to reproduce.
- The affected version.
- Whether local files, local SQLite data, or the Windows host are involved.

Do not include private family data, credentials, or tokens in reports.

Racinage Free accepts plugin metadata only from the HTTPS Racinage catalog after verifying its embedded RSA public key signature. It installs only entries marked local-compatible, verifies the exact ZIP checksum, and requires a production-only portable artifact. Packages containing files outside the declared portable root, source or development file types, source maps, duplicate paths, traversal, or excessive expanded data are rejected before installation. Plugin UI opens in a sandboxed frame without family-record access.

Browser-rendered HTML, CSS, JavaScript, images, and WebAssembly can always be inspected by a determined user after delivery. Proprietary logic that must remain secret must run on Racinage servers; obfuscation or client-side encryption is not treated as source-code protection.

## Share with Racinage Free

Local share actions are accepted only from the already RSA-verified catalog contract saved for an installed and enabled plugin. Contracts are validated again when listed and executed. Browser input supplies only stable identifiers, expected revisions, authorized target identifiers, CSRF tokens, and idempotency keys; it cannot select native callbacks. Kitchen source fetching rejects credentials, private or mixed DNS results, redirects, unsupported MIME types, compressed responses, oversized content, and non-standard HTTPS ports.

Windows Share Target registration is optional and requires a signed sparse MSIX identity. The registration helper verifies Authenticode status, certificate dates, manifest publisher matching, the exact approved development thumbprint when applicable, and an external location under `%LOCALAPPDATA%\Racinage Free`. It never creates or trusts a certificate automatically.

## Connected Messaging

Racinage Free uses browser-based device authorization for optional hosted messaging. Account passwords and two-factor codes are handled only by `https://racinage.com`. The app stores scoped refresh tokens with Windows DPAPI and encrypts cached message content and offline queued files for the current Windows user.

The client accepts only the reviewed HTTPS API origin, rejects redirects, and uses Windows certificate validation. Disconnecting revokes the current hosted refresh-token family when reachable and removes local token material. The local family database is never exposed through connected messaging.
