# PlayGif Playnite Extension

![Version](https://img.shields.io/badge/version-0.1.0-blue) ![License](https://img.shields.io/badge/license-MIT-green) ![Playnite SDK](https://img.shields.io/badge/Playnite%20SDK-6.16.0-purple)

A Playnite extension that replaces the static game description view with an animated renderer, bringing Steam store page content to life with inline video, animated WebP, GIF, and AVIF support.

---

## What's New - v0.1.0

- Initial release
- Animated description rendering via WebView2 (GIF, WebM, MP4, animated WebP, AVIF)
- Automatic Steam description fetching for games with media-rich store pages
- Bulk fetch for entire library from settings
- Desktop and fullscreen mode support
- Media caching for offline use
- Scroll chaining between WebView2 and parent Playnite scroll

---

## Features

- Replaces Playnite's static HtmlTextView with a WebView2-based renderer
- Animated inline content plays exactly as intended on Steam store pages
- Fetches rich descriptions directly from Steam's store API when local metadata lacks media
- Per-game media caching with configurable size limits
- Bulk description fetch for entire library
- Theme-compatible: works with desktop themes via visual tree injection
- Fullscreen mode support (experimental, disabled by default)
- Resource management: pauses media when Playnite is minimized or a game is running

## Requirements

- **Playnite 10** (SDK 6.16.0)
- **WebView2 Evergreen Runtime** - pre-installed on Windows 11 and nearly all Windows 10 machines. If missing, download from [Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

## Installation

1. Download the latest `.pext` file from [Releases](https://github.com/aHuddini/PlayGif/releases)
2. Open Playnite -> Add-ons -> Extensions
3. Click "Add extension" and select the `.pext` file
4. Restart Playnite

## Settings

- **Enable animated descriptions** - Master toggle
- **Enable in fullscreen mode** - Experimental, disabled by default
- **Auto-cache media** - Downloads remote media for offline use
- **Max cache per game (MB)** - Per-game cache size limit
- **Fetch Steam descriptions for all games** - Bulk fetch button
- **Enable debug mode** - Shows WebView2 DevTools

## Licensing and Dependencies

PlayGif is licensed under MIT. All third-party dependencies use permissive licenses (MIT or BSD 3-Clause). See [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES) for full details.

### Codec and Media Licensing

**PlayGif does not ship any media codecs.** All media decoding (VP8/VP9 for WebM, H.264 for MP4, AVIF, animated WebP, GIF) is handled entirely by the WebView2 Evergreen Runtime, which is a system component installed and maintained by Microsoft on the user's machine. PlayGif only ships the managed WebView2 API wrapper and native loader — no Chromium binaries, no codec libraries, and no patent-encumbered components are included in the extension package. Codec licensing (MPEG LA for H.264, etc.) is the responsibility of the runtime distributor (Microsoft), not this extension.

### Steam Store API

PlayGif fetches game descriptions from Steam's undocumented store API (`store.steampowered.com/api/appdetails`). This is the same endpoint used by Playnite's built-in UniversalSteamMetadata plugin and many community tools. No API key is required. Requests are rate-limited with retry logic to avoid overloading Steam's servers.

## Support

- **GitHub Issues**: https://github.com/aHuddini/PlayGif/issues
