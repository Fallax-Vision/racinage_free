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
