# Changelog

All notable changes to PlayGif will be documented in this file.

## [1.0.1] - 2026-07-26

### Fixed
- **Descriptions stopped animating after a view change.** Injection was one-shot: `IsInjected` latched on the first success and `TryInject` early-returns while the WebView already has a parent, so the renderer stayed bound to whichever panel it first attached to. Switching between Grid and Details tears that view down, leaving the renderer drawing into a panel that is no longer on screen. It now detects a stale target, detaches, and re-attaches to the live view.
- **Grid view showed a static description.** Both `GridViewGameOverview` and `DetailsViewGameOverview` declare `PART_HtmlDescription`, and the search took the first match — often the hidden Details copy. Selection now uses `IMainViewAPI.ActiveDesktopView` and picks the candidate under the matching view host.
  - `IsVisible` is not usable for this: themes that wrap the description in a collapsed `Expander` (Harmony, Stardust) make every candidate report `IsVisible=false`, including the one actually on screen. Laid-out size is the fallback.

### Added
- **Video scale is a dropdown.** 100 / 90 / 75 / 50 / 35 / 25%, defaulting to 100%. Lowering it decodes fewer pixels per frame, which is the cheapest way to cut playback cost on slower hardware. Previously a free-text box that accepted values the shell silently clamped away. Saved values that don't match an option snap to the nearest one. The per-game menu now draws from the same `Constants.VideoScaleSteps` list so the two can't drift apart.

- **Theme Support settings tab.** Runs a layout report describing what PlayGif found in the theme's visual tree, reports whether the renderer is attached and which view is active, and links to the log folder. Per-theme compatibility patches will live here if a theme ever needs one.
- **Open debug log folder** menu item and settings button, which selects `extension.log` in Explorer.
- **Known Issues settings tab.** Playnite has no official support for animated descriptions, so the renderer is injected into whatever panel a theme builds; themes vary and other extensions modify the same panel. The tab names the symptoms, lists what a useful bug report needs, and links to the issue tracker and project page. Mirrored as a Known Issues section in the README.

### Changed
- Renderer event handlers (`HeightReported`, `ScrollOverflow`) are wired once instead of per injection, since re-attachment reuses the same renderer and would otherwise stack duplicate subscriptions.
- The scroll handler resolves the parent `ScrollViewer` per frame so it follows re-attachment.
- Verbose injection logging is off by default; Grid view always has two description candidates, so it fired during ordinary browsing.

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
