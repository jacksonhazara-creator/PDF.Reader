# Security

## Reporting a vulnerability

Please use GitHub's **private vulnerability reporting** (Security tab → "Report a vulnerability").
Do not open a public issue for security problems.

## How PDF.Reader protects you

| Threat | Mitigation |
|---|---|
| JavaScript in PDFs | The bundled PDFium build has **no JavaScript engine** (no V8), and no form-fill environment is created, so scripts cannot run. Documents with scripts show a banner. |
| Malicious links | Links are never followed automatically. Only `http`, `https` and `mailto` can be opened, after a dialog that shows the full address. |
| Launch actions / embedded files | Never executed or extracted. Detected (document- and page-level) and reported in a banner. |
| Malformed or hostile files | Every file is treated as untrusted. Native callbacks never let exceptions cross into PDFium; outlines are cycle-guarded; failures become friendly errors. A deterministic mutation fuzzer runs in CI on every push (300 cases) and nightly (20,000). |
| Network | No telemetry. The only network request is the optional update check against public GitHub Releases over HTTPS, without tokens. |
| Updates | Velopack verifies package hashes before applying. Release binaries are code-signed when a signing certificate is configured in CI. |
| Secrets | The app needs no API keys or tokens. Signing credentials exist only as GitHub Actions secrets. |
| Privileges | Per-user install, `asInvoker` manifest, never requires admin. |
| Dependencies | `dotnet list package --vulnerable` and NuGet audit run in CI; Dependabot keeps packages current. |
