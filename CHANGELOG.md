# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-11-04

### Added
- Toolbar with grouped tools (icon + name), movable: dock left / right / top or floating.
- Comment tools: add comment (square, circle, cloud, speech bubble, star; first or last word), replace selected text, insert text, text comment, attach file.
- Marker: highlight, underline, strikethrough; color, transparency and color strength; freehand on images.
- Draw: freehand, line, line arrow, arrow, rectangle, circle, text callout, polygon, cloud, connected line, stamp palette.
- Text: type text, crossmark, checkmark, dot, circle around, cross out.
- Signature and initials (draw or type), saved locally for reuse.
- Right panel: pages with page box, bookmarks (go to, add, delete), comments (page, date, go to, delete); collapsible.
- File menu and Print button; Save, Save as, unsaved-changes prompt, undo.
- Developer mode (Settings → General): "Run self-diagnosis" tests the engine, every annotation type, all tools and shortcuts, and live scroll/zoom/panel smoothness on this PC, then shows a copyable report (also saved under %LOCALAPPDATA%PDF.ReaderDiagnostics). Uses a generated sample; no document content, nothing sent.

- `PDF.Reader.exe --diagnose report.txt` runs the same self-diagnosis unattended, writes the report and exits (exit code 0 = no failed checks, 1 = failures). Uses a throwaway settings folder.

### Changed
- All tools panel is docked (pushes the document aside), dark grey, with a Hide button.
- App logo is red.

### Fixed
- Self-diagnosis no longer reports smooth 30 fps on a 60 Hz screen as lag (frame-timer jitter tolerance).
- Installer and update packages no longer include ~200 MB of debug symbol (.pdb) files.
- Esc always leaves reading mode first.
- Window title and fullscreen button no longer overlap the top bar.

## [0.1.0]

First preview: Phase 1, a fast and secure reader.

### Added
- PDFium rendering on a dedicated thread with tile-based, progressive rendering and a memory-bounded tile cache.
- Document tabs, start screen with recent files and thumbnails, drag & drop, session restore.
- Search (match case, whole words), thumbnails and bookmarks sidebar, go to page.
- Text selection with Copy and Read aloud popup.
- Page colors: Normal, Dark, Sepia, Custom, with "Keep images natural". Per-document override.
- Reading mode, command palette (Ctrl+K), All tools panel, keyboard shortcuts.
- Read aloud with offline Windows voices.
- Printing through the Windows print dialog.
- Settings (General, Appearance, Updates); English and German.
- Security: no JavaScript engine, links always confirmed, launch actions blocked, banner for active content.
- Velopack installer, portable build and updates from GitHub Releases (Stable and Beta channels).
