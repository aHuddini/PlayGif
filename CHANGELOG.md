# Changelog

All notable changes to PlayGif will be documented in this file.

## [1.0.3] - 2026-08-15

### Added
- **Edit description** (right-click → PlayGif). Opens the description in a `contenteditable` WebView2 surface that maps the same `playgif.local` virtual host as the renderer, so media plays while being edited.
  - **Insert at the cursor** from web image search, a direct URL, or a local file. These reuse `PickWebImageUrl` and `DownloadMediaTagAsync`, split out of the existing menu paths, so format detection and caching behave identically — only the insertion point differs.
  - A dialog takes focus and destroys the selection, so the caret is captured before one opens and restored before inserting.
  - Paragraph styles (heading, subheading, quote), text colour, alignment, ordered and unordered lists, horizontal rule, clear formatting, and a raw HTML view.
  - Clicking media shows a floating bar to scale it to 25/50/75/100% or delete it. `getContent` strips the selection class so editor-only state is never saved.
  - Edits are written to the cached description, which is what the renderer reads, creating it when the game has none.

### Fixed
- **Media added from web search rendered as an empty gap.** `DownloadAndInsertMedia` guessed the extension from the URL and defaulted to `.mp4` when the path had none. Image hosts routinely serve GIFs from extensionless CDN paths, so a GIF was saved as `media.mp4` and wrapped in a `<video>` the browser could not decode. The format now comes from the response: magic bytes first, since servers mislabel `Content-Type` and some return `application/octet-stream` for everything, then `Content-Type`, then the URL.
- **`.gifv` links produced nothing.** It is not a format — Imgur serves an HTML page at that URL wrapping an MP4 — so the page was saved as media. `.gifv` is now rewritten to `.mp4` before fetching, and a response that is actually HTML is refused with a message explaining how to get a direct link.
- **Playnite stayed dimmed after closing a PlayGif dialog.** Themes dim the main window while it owns a child, through a `DataTrigger` bound to `HasChildWindow` that sets `Opacity` to `0.4`. That property is only re-evaluated when `WindowManager.NotifyChildOwnershipChanges` is called; WPF does not raise it. Our dialogs opened and closed without it, so the dimming persisted until another dialog happened to refresh it. Both call sites now refresh on close, including when cancelled, via Playnite's notifier with a fallback that raises the bound property directly.
  - The description looked unaffected because it is a WebView2 HWND composited above the dimmed WPF content.

### Changed
- **"Refresh description" is now "Reset description."** It discards the cached description and every cached media file, including media added by hand and any editor changes, so it is named accordingly and confirms first.
- Add media is ordered Search web images, From URL, Local file.
- Web search results show a format badge, highlighted for animated formats.
- The editor shares the renderer's `CoreWebView2Environment` instead of creating a second one with different GPU options, and does not open dialogs from inside the WebView2 message callback.

## [1.0.2] - 2026-08-03

### Fixed
- **Store descriptions are fetched in the user's language** ([#1](https://github.com/aHuddini/PlayGif/issues/1)). The Steam API returns English unless given `&l=<language>`, so PlayGif was overwriting localized descriptions with English text. Playnite's locale is now mapped to Steam's store API names via `Common/SteamLanguage.cs`.
  - Steam's naming is irregular — `koreana` not `korean`, `brazilian`, `schinese`, `tchinese`, `latam` — and an unrecognised value silently falls back to English, which is the failure being fixed. Every code in the table was verified against the live API.
  - GOG takes a BCP-47 locale (`de-DE`) instead, so `FetchGogDescriptionAsync` sends that.
  - Cached descriptions are language-specific. English keeps the original `_description.html` name so existing caches stay valid and are not re-downloaded; other languages get `_description.<lang>.html`.
  - `ClearAllCachedDescriptions` removes every language, so "Refresh description" genuinely refetches after a language change, and `MediaLibraryHandler` strips removed media from every cached language.
  - Unknown or English locales omit the parameter entirely, preserving existing behaviour.

### Fix attempts
- **Descriptions reverting to static text after a restart** ([#2](https://github.com/aHuddini/PlayGif/issues/2)). Injection attempts were capped at 5 on the assumption that retries are spread across user actions. They are not — Playnite fires several `OnGameSelected` events during startup, so in the reporter's log all attempts burned inside 27ms, before Grid view's details panel had been built (`[PART_HtmlDescription] found 0`). Across that log, 18 of 48 sessions never injected and 5 exhausted the cap.
  - The cap is removed, and a `Loaded` class handler raises `DescriptionAppeared` when a description element enters the tree while not injected, so injection happens as soon as the panel is built. This mirrors the existing `Unloaded` handler, making both directions event-driven.
  - Marked a fix attempt because it depends on live startup timing and cannot be reproduced or confirmed outside a real Playnite session.

### Added
- **Convert cached media to MP4** (Settings → General → Cache). Re-encodes cached GIF, WebM and animated WebP/APNG with FFmpeg. A GIF stores every frame as a full image, so the saving is large — 35 GIFs totalling 116 MB converted to roughly 8 MB on the test library — and H.264 is hardware-decoded where GIF is not.
  - Two encoder arguments are load-bearing and must stay together: `yuv420p`, because libx264 otherwise picks `yuv444p` for GIF input which most browsers and hardware decoders will not play; and a scale filter rounding to even dimensions, because `yuv420p` requires them and GIFs are frequently odd-sized. Supplying only one produces a file that looks converted but does not render.
  - Originals are deleted only after the MP4 is confirmed on disk.
- **Repair description links**, alongside the convert button. Runs automatically after a conversion, and separately for anyone who converted before it existed.

### Fixed
- **A pipe deadlock in the FFmpeg conversion path.** `stderr` was redirected but never drained, so FFmpeg blocked once the buffer filled and `WaitForExit` reported a timeout for a conversion that had actually succeeded — the good MP4 was then deleted. Reproduced on a 17 MB GIF. `stderr` is now read asynchronously. This affected the existing WebM path too; it only escaped notice because WebM sources are small enough not to fill the buffer.
- **Descriptions broke after converting media.** `Map()` only resolves remote URLs, so it never saw media added through "Add media" — that is written into the description as a `playgif.local` URL at insert time. After conversion the original was gone and the reference dangled.
- **`<img>` tags were left pointing at MP4 files.** Both repair paths rewrote the raw HTML with a string replace, swapping the extension while leaving the element an `<img>`, which can never play an MP4. Repair then found nothing to do on later runs because the reference already named an existing file. Both paths now parse the HTML and replace the element, and recognise an `<img>` already naming an `.mp4` so descriptions damaged by the earlier repair are recovered. The original `style` attribute is carried over.
- **Links to media that no longer exists are removed.** Clearing the cache deletes the media but leaves the description pointing at it, and there is no MP4 to re-point to, so the dead tag is dropped rather than rendering a broken image. A dead `<source>` takes its parent `<video>` with it.

### Changed
- Injection attempt logging is throttled. It now runs on every game selection until the panel appears, and `extension.log` is shared with every extension.
- The `Loaded` handler sees every element in the application, so its guards are ordered cheapest-first, duplicate events for the same element are ignored (WPF re-raises `Loaded` on re-parenting), and the guard resets in `Detach` so re-injection after a view switch still works.

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
