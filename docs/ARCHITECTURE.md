# Architecture

## Projects

```
PdfReader.App (WinExe, Avalonia, Native AOT)
  ├── PdfReader.Core       settings/state JSON, search, command matching
  └── PdfReader.Rendering  PDFium P/Invoke + render engine (no UI dependency)
```

`Rendering` and `Core` target plain `net10.0` and are AOT-compatible. `App` targets
`net10.0-windows10.0.19041.0` for the built-in WinRT APIs (speech now, OCR in phase 3).

## Key decisions

**One PDFium thread (`PdfEngine`).** PDFium is not thread-safe. Every call goes through a
single worker thread with a priority queue (Immediate > Visible > Interactive > Prefetch >
Background). The UI thread never touches PDFium and never waits on it. Cancelled work is
dropped before it starts, and in-progress renders stop early via PDFium's progressive-render
pause callback, so fast scrolling doesn't build a backlog.

**Thin `LibraryImport` bindings instead of a wrapper package.** Source-generated P/Invoke is
trimming/AOT friendly, and every signature is checked against the headers shipped in the
`bblanchon.PDFium.Win32` package. Documents load through `FPDF_LoadCustomDocument` with a
read callback, so huge files open lazily (PDFium reads only what it needs) and the file
stays shareable by other apps.

**Layout from page sizes, then progressive rendering.** On open we read every page's size
(cheap), so the full scroll extent exists immediately. Each page then gets a ~360px preview
(fast), followed by 512px tiles at the exact screen scale for the viewport plus a prefetch
margin. While zooming, the previous scale's tiles are stretched as a placeholder. Tiles live
in a 384 MB LRU cache shared by all tabs; visible tiles are touched every frame, so only
off-screen tiles are evicted.

**Page colors in the rasterizer.** With *Keep images natural* on, PDFium's color-scheme
rendering recolors text and vector paths and leaves raster images untouched. With it off, a
luminance map takes every pixel onto the foreground→background gradient. Both happen on the
render thread and are part of the tile cache key, so scrolling cost doesn't change. Printing
always uses normal colors, and nothing is ever written to the file.

**In-window overlays instead of extra windows.** Dialogs, the command palette, All tools and
Settings are overlays in the main window: no window creation cost, consistent look, and
AOT-friendly.

**Commands as data.** Every action is an `AppCommand` (title, icon, shortcuts, can-execute).
Shortcuts, the command palette and the All tools panel are all generated from one list, and
the customizable toolbar (phase 2) will use it too.

**Single instance.** A second launch forwards its files over a current-user-only named pipe
and exits, so double-clicking PDFs opens tabs in the existing window.

## Security model

See [SECURITY.md](../SECURITY.md).

## Performance checks

`PDF.Reader.exe --benchmark out.json file.pdf` opens the file, waits until the first screen is
sharp, writes `windowShownMs`, `firstPageMs` and `workingSetMb`, and exits. CI runs it on the
Native AOT build against `perf/budget.json` and fails on regressions.
