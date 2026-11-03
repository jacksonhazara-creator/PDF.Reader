# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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
