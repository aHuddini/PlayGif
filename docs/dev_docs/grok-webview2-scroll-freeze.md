# WebView2 WPF Scroll Freeze Investigation

## Context

Building a Playnite (WPF .NET 4.6.2) extension that embeds a WebView2 control to render animated game descriptions (HTML with `<video>` tags). The WebView2 replaces a static HtmlTextView in the visual tree.

## The Setup

- WebView2 is viewport-height (bound to parent ScrollViewer's ViewportHeight)
- Content is HTML with multiple `<video autoplay muted>` elements (WebM/MP4 from Steam)
- Shell page has `overflow-y: auto` for internal scrolling
- `SetWindowRgn` clips the HWND to the parent ScrollViewer's viewport to solve the WPF airspace overflow issue
- `SetWindowRgn` only fires when the parent ScrollViewer scrolls (not during internal WebView2 scrolling)
- Boundary scroll forwarding: when WebView2 scroll hits top/bottom, JS sends `postMessage` to C# which calls `ScrollViewer.ScrollToVerticalOffset` via `BeginInvoke`

## The Problem

WebView2 internal scrolling intermittently freezes/stalls. Diagnostic data shows:

- `Scroll gap 4741ms after 147 events` — 4.7 second freeze
- `Scroll gap 9364ms after 65 events` — 9.3 second freeze
- `Scroll gap 2995ms after 325 events` — 3 second freeze

After reducing `SetWindowRgn` to only fire on parent scroll changes (not every frame), freezes reduced to ~594ms gaps. But they still occur, especially near the bottom of the page.

## What I've Tried

1. `overscroll-behavior: none` CSS — didn't help
2. Throttled `postMessage` boundary forwarding (400ms) — didn't help
3. `SetWindowRgn(hwnd, rgn, false)` (no forced redraw) — helped somewhat
4. Removing all `postMessage`/cross-thread communication — freezes still occur
5. JS-based video looping instead of HTML `loop` attribute — implemented
6. Bottom padding (200px) to avoid true scroll boundary — didn't help
7. `Focusable = false` on WebView2 — implemented

## Key Observations

- Freezes happen during **internal** WebView2 scrolling (no C# involvement)
- Worse with more `<video>` elements on the page
- The freeze appears to be in Chromium's scroll/render pipeline, not in WPF
- Known WebView2 issues: [#4291](https://github.com/MicrosoftEdge/WebView2Feedback/issues/4291), [#3769](https://github.com/MicrosoftEdge/WebView2Feedback/issues/3769)

## Questions

1. Is there a way to disable or limit Chromium's scroll compositor to prevent these stalls?
2. Would disabling GPU acceleration for the WebView2 help (`--disable-gpu` environment flag)?
3. Is there a better approach than internal scrolling for this use case?
4. Would using `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` with specific Chromium flags help?
5. Any other workarounds for WebView2 scroll stalls in WPF with video-heavy content?
