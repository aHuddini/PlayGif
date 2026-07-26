# PlayGif Playnite Extension

![License](https://img.shields.io/badge/license-MIT-green) ![Playnite SDK](https://img.shields.io/badge/Playnite%20SDK-6.16.0-purple) ![Total Downloads](https://img.shields.io/github/downloads/aHuddini/PlayGif/total?label=downloads&color=brightgreen) ![Latest Release Downloads](https://img.shields.io/github/downloads/aHuddini/PlayGif/latest/total?label=latest%20release&color=blue)

<p align="center">
  <img src="docs/assets/logo-animated.gif" alt="PlayGif" width="180">
</p>

<p align="center">
  <img src="docs/assets/GHdisplay.png" alt="PlayGif" width="420">
</p>

<p align="center">
  <a href="https://ko-fi.com/huddini">
    <img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="ko-fi">
  </a>
</p>

A Playnite extension that brings game descriptions to life. Instead of the static text panel, descriptions render through a real browser engine — so Steam store trailers, GIFs, animated WebP and AVIF all play inline, exactly as they look on the store page.

Designed for Desktop mode, with experimental Fullscreen support.

Built with the help of Claude Code

---

https://github.com/user-attachments/assets/90acd659-e195-446f-aeab-e5349ae84fd7

## What's New - v1.0.1

### Fixed
- **Descriptions keep animating when you switch views.** Moving between Grid and Details used to leave the description static until Playnite was restarted.
- **Grid view animates properly.** In Grid view the description could stay static while the animated version rendered out of sight.

### Added
- **Video scale dropdown** (100/90/75/50/35/25%). Lowering it is the simplest way to reduce playback cost on slower machines.
- **Theme Support settings tab.** If descriptions do not animate correctly in your theme, run the layout report and attach it to a bug report.
- **Open debug log folder** — finds `extension.log` for you.

### Previous Version
- **v1.0.0**: first release — animated descriptions, Steam/GOG description fetching, custom media, per-game video scale, and offline caching.

---

## Features

- **Animated inline content** — video, GIF, animated WebP and AVIF, rendered by the WebView2 engine
- **Store descriptions** — pulls rich media descriptions from Steam and GOG when local metadata is plain text
- **Custom media** — add your own clips or images to the top or bottom of any description
- **Video sizing** — global scale and height cap, with per-game overrides
- **Offline cache** — media is cached locally with a per-game size limit
- **Resource-aware** — pauses playback when a game launches or Playnite loses focus
- **Theme compatible** — injects into the existing description panel, so themes keep their layout

---

## Requirements

- **Playnite 10** (SDK 6.16.0)
- **Windows 10 (2004 / May 2020 update) or newer**, or Windows 11
- **WebView2 Evergreen Runtime** — pre-installed on Windows 11 and most Windows 10 machines. If missing, get it from [Microsoft](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)
- **FFmpeg** *(optional)* — only used to convert the rare video that a store publishes exclusively as WebM. Without it, those clips still play.

---

## Installation

1. Download the latest `.pext` from [Releases](https://github.com/aHuddini/PlayGif/releases)
2. Open Playnite → Add-ons → Extensions
3. Click **Add extension** and select the `.pext`
4. Restart Playnite

---

## Usage

Right-click any game for the **PlayGif** menu:

| Action | What it does |
|---|---|
| **Add media** | Insert a local file, a URL, or a web image search result at the top or bottom of the description |
| **Remove media** | Pick a media file to remove; it is stripped from the description too |
| **Refresh description** | Clears the cache and re-fetches, for when a description looks wrong |
| **Video scale** | Set 25/50/75% for this game, or reset to the global default |
| **Fetch description** | Pull a rich description from Steam or GOG |

Library-wide options live in **Settings → PlayGif**, including the bulk Steam fetch.

If descriptions do not render correctly in your theme, open **Settings → PlayGif → Theme Support** and run the layout report — it records what PlayGif found in your theme's panel, which is what a bug report needs.

---

## Known Issues

**Theme compatibility is the rough edge.** Playnite has no official support for animated descriptions, so PlayGif injects a renderer into whatever description panel your theme happens to build. Themes lay that out very differently, and other extensions modify it too.

What that can look like:

- Visual glitches — clipped text, blank gaps, content in the wrong place, or flicker while scrolling
- Descriptions that don't animate at all in a particular theme
- Problems that appear only when another extension is installed, because several also modify the details view (HowLongToBeat, achievements plugins, screenshot viewers, and others)

If you hit any of this, please report it. I'll look into what I can, though some theme layouts may be out of reach:

1. **Settings → PlayGif → Theme Support → Run layout report**
2. Open an [issue](https://github.com/aHuddini/PlayGif/issues) with the report, your theme name, and any other extensions that touch the details view

The long-term fix is official Playnite support for animated content in descriptions, which would remove the need to inject anything. Until then, per-theme patches go in the Theme Support tab as they're identified.

---

## Licensing and Dependencies

PlayGif is MIT licensed. All third-party dependencies use permissive licenses (MIT or BSD 3-Clause). See [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES) for details.

### Codec and Media Licensing

**PlayGif ships no media codecs.** All decoding (H.264, VP8/VP9, AVIF, animated WebP, GIF) is handled by the WebView2 Evergreen Runtime — a system component installed and maintained by Microsoft. PlayGif bundles only the managed WebView2 API wrapper and native loader: no Chromium binaries, no codec libraries, no patent-encumbered components. Codec licensing (MPEG LA for H.264, etc.) is the runtime distributor's responsibility, not this extension's.

### Store APIs

PlayGif fetches descriptions from Steam's public store API (`store.steampowered.com/api/appdetails`) and GOG's product API — the same endpoints used by Playnite's own metadata plugins. No API key is required, and requests are rate-limited with retry logic.

---

## Support

- **Issues**: https://github.com/aHuddini/PlayGif/issues
