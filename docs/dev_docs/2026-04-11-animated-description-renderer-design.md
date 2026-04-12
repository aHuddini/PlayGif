# PlayGif: Animated Description Renderer — Design Spec

**Date:** 2026-04-11
**Status:** Draft
**Author:** Huddini + Claude

## Problem

Playnite renders game descriptions using `HtmlTextView` (TheArtOfDev.HtmlRenderer.WPF), a static HTML renderer that cannot animate GIFs, play `<video>` tags, or handle modern image formats like animated WebP/AVIF. Steam store pages embed animated promotional content directly in the `about_the_game` HTML as `<video autoplay muted loop>` and animated `<img>` tags. This content reaches Playnite via metadata plugins but displays as static first-frame images.

## Solution

Replace the `HtmlTextView` control at runtime with a WebView2 control that renders the same description HTML with full HTML5 media support. All animated content plays inline, exactly as the store page intended.

## Architecture

### Injection Strategy (Hybrid)

Two injection paths, tried in order:

**1. Custom Element (Primary)**
Register via `AddCustomElementSupport` with element name `AnimatedDescription`. Themes that include `<ContentControl x:Name="PlayGif_AnimatedDescription"/>` get the WebView2 control placed by the theme author. Plugin returns the control from `GetGameViewControl()`. When this path is active, the `DescriptionRendererService` manages the WebView2 control within the theme-provided container.

**2. Visual Tree Walking (Fallback)**
If `GetGameViewControl()` is never called (theme doesn't include the custom element), the `DescriptionViewMonitor` activates on the first `Window.LoadedEvent`:

1. Hook `Window.LoadedEvent` via `EventManager.RegisterClassHandler` (one-time, class-wide)
2. Find `PART_HtmlDescription` (HtmlTextView) in the visual tree using recursive `VisualTreeHelper` descent
3. Get its parent container:
   - Desktop: `PART_ElemDescription` (StackPanel)
   - Fullscreen: `PART_ScrollHtmlDescription` (ScrollViewerEx)
4. Set `HtmlTextView.Visibility = Collapsed` (don't remove — preserves bindings for rollback)
5. Insert the WebView2 control at the same position in the parent
6. Read description content from the HtmlTextView's `HtmlText` dependency property

**Rollback:** If the plugin is disabled, the HtmlTextView becomes visible again. No permanent changes to the visual tree.

### WebView2 Renderer

**Single persistent instance** for the plugin lifetime:

- One shared `CoreWebView2Environment` created in `OnApplicationStarted`
- One `WebView2` control loads a minimal HTML shell page once
- Content updates via `ExecuteScriptAsync("setContent(html)")` — no page navigation on game switch
- Virtual host mapping: `SetVirtualHostNameToFolderMapping("playgif.local", cacheBasePath, DenyCors)` maps local cache to a clean URL scheme

**HTML Shell Page:**

A static HTML page loaded once. Contains:
- `<div id="content">` — receives description HTML via JS
- `setContent(html)` — swaps description content
- `setTheme(vars)` — updates CSS variables to match Playnite theme
- `pauseAll()` / `resumeAll()` — media lifecycle control
- All `<video>` elements render with `muted autoplay loop playsinline` attributes

**Lockdown:**
- `AreBrowserAcceleratorKeysEnabled = false`
- `IsStatusBarEnabled = false`
- `AreDefaultContextMenusEnabled = false`
- `IsZoomControlEnabled = false`
- `AreDevToolsEnabled = false` (unless `EnableDebugMode` setting is on)
- Intercept `NavigationStarting` to block external navigation (links open in system browser)

**Theme Matching:**
Read Playnite's dynamic resources (`TextColor`, `GlyphColor`, `FontSize`, `FontFamily`) and inject as CSS variables into the shell page. Update on theme change.

### Media Caching

**Cache location:** `{GetPluginUserDataPath()}/Games/{gameId}/`

**Flow:**
1. On game selection, read `game.Description` HTML
2. Parse with HtmlAgilityPack — extract all `<img src>`, `<video><source src>` URLs pointing to remote hosts (Steam CDN, etc.)
3. For each remote URL:
   - Check local cache (filename derived from URL hash or original filename)
   - If cached: rewrite URL to `https://playgif.local/{gameId}/{filename}`
   - If not cached: keep remote URL for immediate display, queue background download
4. Feed the (possibly rewritten) HTML to WebView2
5. Background downloads complete → update cache → next game selection uses local files

**First-time experience:** Content renders immediately from remote URLs (WebView2 fetches them). Caching is transparent and progressive.

### Cross-Matching (Non-Steam Games)

For games that don't have animated content in their description (e.g., GOG imports with plain-text descriptions), optionally fetch the Steam store description:

1. Normalize game name using UniversalSteamMetadata's proven matching algorithm:
   - Alphanumeric lowering, trademark removal, "The" prefix handling, Roman numeral conversion, subtitle splitting
2. Search `https://store.steampowered.com/search/?term={name}&ignore_preferences=1&category1=998&ndl=1`
3. Parse results with HtmlAgilityPack, extract `data-ds-appid`
4. Fetch `https://store.steampowered.com/api/appdetails?appids={appId}` for the matched game
5. Use the `about_the_game` HTML field — this contains the inline `<video>` and `<img>` tags
6. Cache the enriched description separately (don't overwrite the game's actual Description field)

**Rate limiting:** Retry up to 10 times on HTTP 429 with 2.5s backoff (matching UniversalSteamMetadata's pattern).

This feature is an enhancement — the core plugin works with whatever Description content already exists on the game.

## Desktop & Fullscreen

### Desktop Mode
- WebView2 injected into `PART_ElemDescription` (StackPanel), replacing the collapsed HtmlTextView
- Body CSS: `overflow: hidden` — scrolling delegated to the parent WPF ScrollViewer
- WebView2 height sized to content (communicate content height from JS to WPF via `WebMessageReceived`)

### Fullscreen Mode
- WebView2 injected into `PART_ScrollHtmlDescription` (ScrollViewerEx)
- Body CSS: `overflow-y: auto` — WebView2 handles its own scrolling inside the fullscreen ScrollViewerEx, since the fullscreen view gives the description its own dedicated scroll container
- Must not steal focus from Playnite's fullscreen controller input handling
- `IsTabStop = false`, `Focusable = false` on the WebView2 control to prevent focus capture

### Resource Management
- Playnite minimized → `MemoryUsageTargetLevel = Low`, call `pauseAll()` JS
- Game launched → `pauseAll()` JS, `MemoryUsageTargetLevel = Low`
- Playnite restored / game exited → `resumeAll()` JS, `MemoryUsageTargetLevel = Normal`
- Detection via Playnite SDK events: `OnGameStarted`, `OnGameStopped`, and WPF `Window.Deactivated`/`Activated`

## Settings

Properties in `PlayGifSettings.cs` with `OnPropertyChanged()`, UI in `PlayGifSettingsView.xaml`:

| Setting | Type | Default | Purpose |
|---------|------|---------|---------|
| `EnableAnimatedDescriptions` | bool | true | Master toggle — when off, HtmlTextView is restored |
| `AutoCacheMedia` | bool | true | Download remote media to local cache |
| `MaxCachePerGame` | int (MB) | 100 | Per-game cache size limit |
| `EnableDebugMode` | bool | false | Enables WebView2 DevTools |

## Game Menu Items

Via `GetGameMenuItems()`:

**Single game selected:**
- "Clear cached media" — deletes cached files for this game
- "Re-download media" — clears cache and re-fetches

**Multi-game selected:**
- "Clear all cached media" — deletes cache for all selected games

## Dependencies

| Package | Version | Size | Purpose |
|---------|---------|------|---------|
| Microsoft.Web.WebView2 | latest | ~8.6 MB NuGet | HTML5 renderer (GIF, WebM, MP4, WebP, AVIF) |
| HtmlAgilityPack | latest | ~200 KB | Parse description HTML for media URL extraction |

**Runtime requirement:** WebView2 Evergreen Runtime — pre-installed on Windows 11, nearly universal on Windows 10. If missing, plugin gracefully falls back to the original HtmlTextView with a settings notification.

## Project Structure

```
src/
├── PlayGif.cs                      // Plugin entry point, lifecycle, injection
├── PlayGifSettings.cs              // Settings properties
├── PlayGifSettingsView.xaml/.cs    // Settings UI
├── PlayGifSettingsViewModel.cs     // Settings ViewModel
├── Common/
│   └── Constants.cs                // Plugin constants, supported extensions, cache paths
├── Services/
│   ├── DescriptionRendererService.cs   // WebView2 lifecycle, content updates, theme sync
│   ├── MediaCacheService.cs            // Download, cache, URL rewriting
│   └── SteamMatchingService.cs         // Cross-match non-Steam games to Steam AppIds
├── Monitors/
│   └── DescriptionViewMonitor.cs       // Visual tree walking, HtmlTextView replacement
├── Models/
│   └── CachedMedia.cs                  // Per-game cache metadata
└── Views/
    └── (reserved for future dialogs)
```

## Test Plan

**Primary test game:** Deep Rock Galactic (Steam, has animated description content)

**Verification steps:**
1. Install plugin, launch Playnite, select Deep Rock Galactic
2. Verify animated content plays inline in the description area
3. Verify videos are muted and loop
4. Verify text content renders with correct theme styling
5. Verify scrolling works naturally (desktop and fullscreen)
6. Verify cache folder populates under plugin data path
7. Verify disabling the plugin restores original HtmlTextView
8. Verify resource management: minimize Playnite, check video pauses
9. Verify game launch pauses media, game exit resumes
10. Test a non-Steam game with plain description (graceful fallback)
