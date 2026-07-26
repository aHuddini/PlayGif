# Changelog

All notable changes to PlayGif will be documented in this file.

## [1.0.0] - 2026-07-25

First public release.

### Added
- Animated description rendering via WebView2 — video, GIF, animated WebP, APNG and AVIF play inline.
- Steam and GOG rich-description fetching, per game or in bulk across the library.
- Custom media insertion: local file, direct URL, or web image search, at the top or bottom of a description.
- Media removal that also strips the corresponding tags from the stored and cached descriptions.
- Per-game video scale overrides (25/50/75%) alongside global scale and max-height settings.
- Per-game offline media cache with a configurable size limit.
- Poster-only mode for videos, which keeps GIFs animated.

### Performance
- **MP4 preferred over WebM.** Steam serves each description video as both VP9 WebM and H.264 MP4. The renderer now selects the MP4 and drops the WebM before download, so playback is hardware-decoded on far more hardware and cache size roughly halves.
- Stale WebM cached by earlier builds is reclaimed automatically as games are viewed, once its MP4 replacement is confirmed on disk.
- Orphan WebM (no MP4 published) converts in the background when FFmpeg is available, and plays as-is otherwise.
- Cache size is tracked incrementally instead of walking the game directory once per downloaded file.
- Playback pauses and memory drops to a low target while a game runs or Playnite is unfocused.

### Fixed
- Description text no longer appears clipped at the bottom. Height is measured from the painted box rather than `scrollHeight` alone, and re-measured on layout changes and web-font load instead of on a fixed timer.
- Content no longer shows blank regions while scrolling. The HWND clip region is compared in window-local device pixels, so scroll offsets are never mistaken for a no-op.
- Newly added media appears immediately instead of requiring a game switch.
- Removing media no longer leaves broken-image placeholders behind.
- Removing media from a description no longer silently reverts by re-fetching the original from the store.

### Dependencies
- Newtonsoft.Json 13.0.1 → 13.0.4.
- WebView2 SDK held at 1.0.3124.44 for Windows 10 compatibility; the rendering engine is the system Evergreen Runtime and updates independently.

## [0.1.0] - 2026-04-11

### Added
- Initial project scaffold with Playnite SDK 6.16.0 integration.
- Basic plugin entry point, settings system, and packaging pipeline.
