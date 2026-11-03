# Third-party notices

PDF.Reader is MIT licensed and only uses permissively licensed components. AGPL/GPL
libraries (for example MuPDF or Ghostscript) are deliberately not used.

| Component | Use | License |
|---|---|---|
| [PDFium](https://pdfium.googlesource.com/pdfium/) via [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) | PDF parsing and rendering | BSD-3-Clause / Apache-2.0 (binaries packaging: MIT) |
| [Avalonia UI](https://avaloniaui.net/) | UI framework, Fluent theme | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM source generators | MIT |
| [Velopack](https://velopack.io/) | Installer and updates | MIT |
| [PDFsharp](https://www.pdfsharp.net/) **6.2.4** | Editing bookmarks on save | MIT (pinned: PDFsharp 6.5+ is AGPL-3.0 and must not be used) |
| [.NET](https://github.com/dotnet/runtime) | Runtime, Native AOT | MIT |
| [xUnit](https://xunit.net/) (tests only) | Test framework | Apache-2.0 |

Planned for later phases: Windows.Media.Ocr (built into Windows) for OCR. Typed signatures embed the Windows "Segoe Script" / "Ink Free" font into the PDF when available.
