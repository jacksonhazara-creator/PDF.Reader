# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Toolbar with grouped tools (icon + name), movable: dock left / right / top or floating.
- Comment tools: add comment (square, circle, cloud, speech bubble, star; first or last word), replace selected text, insert text, text comment, attach file.
- Marker: highlight, underline, strikethrough; color, transparency and color strength; freehand on images.
- Draw: freehand, line, line arrow, arrow, rectangle, circle, text callout, polygon, cloud, connected line, stamp palette.
- Text: type text, crossmark, checkmark, dot, circle around, cross out.
- Signature and initials (draw or type), saved locally for reuse.
- Right panel: pages with page box, bookmarks (go to, add, delete), comments (page, date, go to, delete); collapsible.
- File menu and Print button; Save, Save as, unsaved-changes prompt, undo.

### Changed
- All tools panel is docked (pushes the document aside), dark grey, with a Hide button.
- App logo is red.

### Fixed
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
