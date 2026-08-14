using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayGif.Common;
using PlayGif.Monitors;
using PlayGif.Services;

namespace PlayGif
{
    public class PlayGif : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IPlayniteAPI _api;
        private PlayGifSettingsViewModel _settingsViewModel;

        private DescriptionRendererService _renderer;
        private MediaCacheService _cacheService;
        private SteamDescriptionService _steamService;
        private DescriptionViewMonitor _viewMonitor;
        private Handlers.MediaLibraryHandler _mediaLibrary;
        private Game _lastSelectedGame;
        private int _injectionAttempts;
        private bool _rendererEventsHooked;

        // Menu handlers are built on first use — the menu can be opened before services initialize
        private Handlers.MediaLibraryHandler MediaLibrary => _mediaLibrary ??
            (_mediaLibrary = new Handlers.MediaLibraryHandler(
                _api,
                (id) => System.IO.Path.Combine(
                    GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, id.ToString()),
                (game) =>
                {
                    if (_lastSelectedGame?.Id == game.Id) TryInjectAndRender(game);
                }));

        public override Guid Id { get; } = Guid.Parse("2e196d25-24d1-4db3-b732-9766c994a496");

        public PlayGif(IPlayniteAPI api) : base(api)
        {
            _api = api;
            Properties = new GenericPluginProperties { HasSettings = true };

            _settingsViewModel = new PlayGifSettingsViewModel(this);

            AddCustomElementSupport(new AddCustomElementSupportArgs
            {
                SourceName = Constants.CustomElementSource,
                ElementList = new List<string> { Constants.CustomElementName }
            });

            Logger.Info($"PlayGif v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} loaded");
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            InitializeServices();
        }

        private async void InitializeServices()
        {
            try
            {
                var basePath = GetPluginUserDataPath();
                Logger.Info($"Plugin data path: {basePath}");

                _cacheService = new MediaCacheService(Settings, basePath);
                _steamService = new SteamDescriptionService(basePath, () => _api.ApplicationSettings.Language);
                _renderer = new DescriptionRendererService(Settings, () => basePath);

                // Create the environment (doesn't need visual tree)
                await _renderer.PrepareEnvironmentAsync();

                // Create the WebView2 control on the UI thread
                _renderer.CreateWebViewControl();

                // Set up the visual tree monitor
                _viewMonitor = new DescriptionViewMonitor(
                    () => _renderer.WebViewControl,
                    () => Settings.EnableAnimatedDescriptions,
                    () => _api.MainView.ActiveDesktopView);

                // Switching Grid <-> Details tears down the view we injected into.
                // Re-inject against whatever is on screen now, without waiting for
                // the user to select a different game.
                _viewMonitor.InjectionLost += () =>
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(() =>
                        {
                            if (_lastSelectedGame == null) return;
                            if (!_viewMonitor.IsStale()) return;
                            _viewMonitor.Detach();
                            _injectionAttempts = 0;
                            TryInjectAndRender(_lastSelectedGame);
                        }));
                };

                // Playnite builds Grid view's details panel on demand, so at
                // startup there is often nothing to inject into. Inject as soon
                // as the element actually appears (issue #2).
                _viewMonitor.DescriptionAppeared += () =>
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(() =>
                        {
                            if (_viewMonitor.IsInjected) return;
                            if (_lastSelectedGame == null) return;
                            TryInjectAndRender(_lastSelectedGame);
                        }));
                };

                _viewMonitor.StartMonitoring();

                // Subscribe to window events
                if (Application.Current?.MainWindow != null)
                {
                    Application.Current.MainWindow.Activated += OnWindowActivated;
                    Application.Current.MainWindow.Deactivated += OnWindowDeactivated;
                }

                var isFullscreen = _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
                Logger.Info($"Mode: {(isFullscreen ? "Fullscreen" : "Desktop")}. EnableInFullscreen: {Settings.EnableInFullscreen}. Services prepared.");

                if (isFullscreen && !Settings.EnableInFullscreen)
                {
                    Logger.Info("Fullscreen mode disabled in settings. Plugin inactive.");
                    return;
                }

                Logger.Info("Waiting for visual tree injection.");

                // If a game was already selected, trigger injection + init
                // But in fullscreen, wait for OnFullscreenViewChanged(Details) instead
                if (_lastSelectedGame != null && !isFullscreen)
                {
                    Application.Current.Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(() => TryInjectAndRender(_lastSelectedGame)));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize PlayGif services.");
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            // Theme custom element path — not used in fallback mode
            return null;
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            if (!Settings.EnableAnimatedDescriptions) return;

            var games = args.NewValue;
            if (games == null || games.Count != 1) return;

            _lastSelectedGame = games[0];

            if (_renderer?.WebViewControl == null)
            {
                Logger.Info($"Renderer not ready, remembering game: {_lastSelectedGame.Name}");
                return;
            }

            // In fullscreen, don't try injection on game select — wait for detail view
            if (_api.ApplicationInfo.Mode == ApplicationMode.Fullscreen && !_viewMonitor.IsInjected)
                return;

            Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => TryInjectAndRender(_lastSelectedGame)));
        }

        public override void OnFullscreenViewChanged(OnFullscreenViewChangedArgs args)
        {
            if (!Settings.EnableAnimatedDescriptions) return;
            if (!Settings.EnableInFullscreen) return;
            if (_renderer?.WebViewControl == null) return;

            if (args.NewView == FullscreenView.Details && _lastSelectedGame != null)
            {
                Logger.Info($"Fullscreen detail view opened for: {_lastSelectedGame.Name}");
                // Reset — the detail view template just expanded, tree has new elements
                _injectionAttempts = 0;
                _viewMonitor.ResetSearchState();
                Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() => TryInjectAndRender(_lastSelectedGame)));
            }
            else if (args.NewView == FullscreenView.List)
            {
                // Leaving detail view — pause videos
                _renderer?.PauseAll();
            }
        }

        private async void TryInjectAndRender(Game game)
        {
            try
            {
                if (_renderer?.WebViewControl == null) return;

                // The view we injected into is torn down when the user switches
                // between Grid and Details, or moves to another tab. Detach so the
                // WebView can be re-parented into whatever is on screen now.
                if (_viewMonitor.IsInjected && _viewMonitor.IsStale())
                {
                    Logger.Info("Injection target is stale (view changed) — re-injecting.");
                    _viewMonitor.Detach();
                    _injectionAttempts = 0;
                }

                // Step 1: Inject into visual tree if not already done.
                // No attempt cap: Playnite creates Grid view's details panel on
                // demand, so at startup the element may not exist for a while.
                // A capped retry burns its whole budget in milliseconds and then
                // blocks injection for the rest of the session (issue #2).
                // DescriptionAppeared re-drives this the moment the element shows up.
                if (!_viewMonitor.IsInjected)
                {
                    _injectionAttempts++;
                    if (_injectionAttempts <= 3 || _injectionAttempts % 25 == 0)
                        Logger.Info($"Attempting visual tree injection (attempt {_injectionAttempts})...");
                    _viewMonitor.TryInject(Application.Current.MainWindow, _renderer.WebViewControl);

                    if (_viewMonitor.IsInjected)
                    {
                        Logger.Info("Injection succeeded. Now initializing WebView2 core...");
                        await _renderer.InitializeAsync();

                        if (!_renderer.IsInitialized)
                        {
                            Logger.Error("WebView2 failed to initialize after injection.");
                            return;
                        }

                        // Re-injection reuses the same renderer, so these handlers
                        // must only ever be wired once
                        if (!_rendererEventsHooked)
                        {
                            _rendererEventsHooked = true;

                            // Full content height — no internal WebView2 scrolling
                            // SetWindowRgn clips the HWND, parent ScrollViewer scrolls it
                            _renderer.HeightReported += (height) =>
                            {
                                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    _renderer.WebViewControl.Height = height;
                                    // Re-clip after the new height lays out, otherwise the
                                    // HWND region still masks part of the description
                                    Application.Current.Dispatcher.BeginInvoke(
                                        DispatcherPriority.Loaded,
                                        new Action(() => _viewMonitor?.RefreshClip()));
                                }));
                            };

                            // Forward wheel events from WebView2 to parent ScrollViewer
                            // Batch at render frame rate for smoothness
                            double pendingDelta = 0;
                            bool renderHooked = false;

                            void OnRenderFrame(object s2, EventArgs e2)
                            {
                                if (pendingDelta != 0)
                                {
                                    // Resolved per frame so it follows re-injection
                                    var sv = _viewMonitor.ParentScrollViewer;
                                    if (sv != null)
                                        sv.ScrollToVerticalOffset(sv.VerticalOffset + pendingDelta);
                                    pendingDelta = 0;
                                }
                                else
                                {
                                    renderHooked = false;
                                    CompositionTarget.Rendering -= OnRenderFrame;
                                }
                            }

                            _renderer.ScrollOverflow += (delta) =>
                            {
                                pendingDelta += delta;
                                if (!renderHooked)
                                {
                                    renderHooked = true;
                                    CompositionTarget.Rendering += OnRenderFrame;
                                }
                            };
                        }

                        Logger.Info("WebView2 fully initialized.");
                    }
                    else
                    {
                        // Expected while the description panel does not exist yet.
                        // Throttled: this runs on every game selection until the
                        // panel appears, and the log is shared with every extension.
                        if (_injectionAttempts <= 3 || _injectionAttempts % 25 == 0)
                            Logger.Info("Injection failed — description element not in the tree yet.");
                        return;
                    }
                }

                // Step 2: Get the best description available
                if (!_renderer.IsInitialized) return;

                var html = game.Description;

                // Always check for a cached rich description first (from any store fetch)
                if (_steamService != null && _steamService.HasCachedDescription(game.Id))
                {
                    var cachedHtml = await _steamService.GetRichDescriptionAsync(game);
                    if (!string.IsNullOrEmpty(cachedHtml))
                    {
                        html = cachedHtml;
                        Logger.Info($"Using cached rich description for: {game.Name} ({html.Length} chars)");
                    }
                }
                else
                {
                    // No cache — check if stored description has media
                    var hasMedia = !string.IsNullOrEmpty(html) &&
                        (html.Contains("<video") || html.Contains(".webm") || html.Contains(".mp4"));

                    // If no media, try to auto-fetch from Steam
                    if (!hasMedia && _steamService != null)
                    {
                        Logger.Info($"Description for {game.Name} has no media ({html?.Length ?? 0} chars). Fetching from Steam...");
                        var richHtml = await _steamService.GetRichDescriptionAsync(game);
                        if (!string.IsNullOrEmpty(richHtml))
                        {
                            html = richHtml;
                            Logger.Info($"Got rich description: {html.Length} chars, has video: {html.Contains("<video")}");
                        }
                        else
                        {
                            Logger.Info("Steam fetch returned nothing. Using original description.");
                        }
                    }
                }

                if (string.IsNullOrEmpty(html))
                {
                    Logger.Info($"No description for: {game.Name}");
                    _ = _renderer.SetDescriptionAsync(null);
                    return;
                }

                Logger.Info($"Rendering: {game.Name} ({html.Length} chars)");

                // Always reset poster-only flag, then set if enabled
                if (_renderer.IsInitialized)
                    _ = _renderer.WebViewControl.CoreWebView2.ExecuteScriptAsync(
                        $"setPosterOnly({(Settings.UseVideoPosterOnly ? "true" : "false")})");

                if (Settings.UseVideoPosterOnly)
                    html = ReplaceVideosWithPosters(html);

                html = _cacheService.RewriteDescriptionHtml(html, game.Id);

                _ = _renderer.SetDescriptionAsync(html);

                // New content may occupy the same height, so no SizeChanged fires
                Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() => _viewMonitor?.RefreshClip()));
                UpdateThemeColors();

                // Apply video scale — per-game override or global default
                var scale = _cacheService.GetGameVideoScale(game.Id) ?? Settings.VideoScale;
                if (scale != 100 && _renderer.IsInitialized)
                {
                    _ = _renderer.WebViewControl.CoreWebView2.ExecuteScriptAsync(
                        $"setVideoScale({scale})");
                }

                if (Settings.MaxVideoHeight > 0 && _renderer.IsInitialized)
                {
                    _ = _renderer.WebViewControl.CoreWebView2.ExecuteScriptAsync(
                        $"setMaxVideoHeight({Settings.MaxVideoHeight})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Error in TryInjectAndRender for: {game?.Name}");
            }
        }

        private string ReplaceVideosWithPosters(string html)
        {
            try
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var videos = doc.DocumentNode.SelectNodes("//video");
                if (videos == null) return html;

                foreach (var video in videos.ToList())
                {
                    var poster = video.GetAttributeValue("poster", "");

                    // If no poster, try to use the MP4 source as a still frame
                    if (string.IsNullOrEmpty(poster))
                    {
                        var source = video.SelectSingleNode(".//source[@type='video/mp4']")
                            ?? video.SelectSingleNode(".//source");
                        if (source != null)
                        {
                            var srcUrl = source.GetAttributeValue("src", "");
                            if (!string.IsNullOrEmpty(srcUrl))
                            {
                                // Keep video but disable playback — shows first frame.
                                // Source selection is handled by RewriteDescriptionHtml.
                                video.SetAttributeValue("preload", "metadata");
                                video.Attributes.Remove("autoplay");
                                video.Attributes.Remove("loop");
                                continue;
                            }
                        }
                        // No poster and no source — remove entirely
                        video.ParentNode.RemoveChild(video);
                        continue;
                    }

                    var w = video.GetAttributeValue("width", "");
                    var h = video.GetAttributeValue("height", "");
                    var cls = video.GetAttributeValue("class", "");

                    var imgHtml = $"<img src=\"{poster}\" class=\"{cls}\"" +
                        (!string.IsNullOrEmpty(w) ? $" width=\"{w}\"" : "") +
                        (!string.IsNullOrEmpty(h) ? $" height=\"{h}\"" : "") +
                        " style=\"max-width:100%;height:auto;display:block;\" />";

                    var imgNode = HtmlAgilityPack.HtmlNode.CreateNode(imgHtml);
                    video.ParentNode.ReplaceChild(imgNode, video);
                }

                return doc.DocumentNode.OuterHtml;
            }
            catch
            {
                return html;
            }
        }

        private string CopyAndBuildMediaTag(Game game, string filePath)
        {
            try
            {
                var gameDir = System.IO.Path.Combine(
                    GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, game.Id.ToString());
                System.IO.Directory.CreateDirectory(gameDir);
                var destName = System.IO.Path.GetFileName(filePath);
                var destPath = System.IO.Path.Combine(gameDir, destName);
                System.IO.File.Copy(filePath, destPath, true);

                return BuildMediaTag(
                    $"https://{Common.Constants.VirtualHostName}/{game.Id}/{destName}",
                    System.IO.Path.GetExtension(filePath).ToLowerInvariant());
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to copy media file");
                _api.Dialogs.ShowMessage($"Failed to copy file: {ex.Message}", Constants.PluginName);
                return null;
            }
        }

        // Called from the Performance settings tab. Re-encodes cached GIF/WebM/
        // animated WebP to H.264 MP4, which decodes far more cheaply and is
        // dramatically smaller — a GIF carries every frame as a full image.
        public void RunBulkMp4Conversion()
        {
            if (_cacheService == null)
            {
                _api.Dialogs.ShowMessage(
                    "PlayGif is still starting up. Try again in a moment.",
                    Constants.PluginName);
                return;
            }

            if (!_cacheService.IsFfmpegAvailable())
            {
                _api.Dialogs.ShowMessage(
                    "FFmpeg was not found.\n\n" +
                    "Install FFmpeg and make sure it is on your PATH, or set its location " +
                    "in Settings -> Advanced -> FFmpeg path.",
                    Constants.PluginName);
                return;
            }

            var files = _cacheService.FindConvertibleMedia();
            if (files.Count == 0)
            {
                _api.Dialogs.ShowMessage(
                    "Nothing to convert — all cached media is already MP4.",
                    Constants.PluginName);
                return;
            }

            var totalMb = files.Sum(f => new System.IO.FileInfo(f).Length) / 1048576.0;
            var confirm = _api.Dialogs.ShowMessage(
                $"Convert {files.Count} cached file(s) to MP4?\n\n" +
                $"Current size: {totalMb:F0} MB\n\n" +
                "Originals are deleted once each conversion succeeds. Descriptions " +
                "keep working — they switch to the MP4 automatically.\n\n" +
                "This can take a while for large libraries.",
                Constants.PluginName,
                MessageBoxButton.YesNo);

            if (confirm != MessageBoxResult.Yes) return;

            var converted = 0;
            var failed = 0;
            long before = 0, after = 0;

            _api.Dialogs.ActivateGlobalProgress((progressArgs) =>
            {
                progressArgs.ProgressMaxValue = files.Count;

                for (int i = 0; i < files.Count; i++)
                {
                    if (progressArgs.CancelToken.IsCancellationRequested) break;

                    var file = files[i];
                    progressArgs.Text =
                        $"Converting {System.IO.Path.GetFileName(file)} ({i + 1}/{files.Count})";

                    var size = new System.IO.FileInfo(file).Length;

                    if (_cacheService.ConvertToMp4(file))
                    {
                        var mp4 = System.IO.Path.ChangeExtension(file, ".mp4");
                        before += size;
                        after += new System.IO.FileInfo(mp4).Length;
                        // Only remove the original once the MP4 is confirmed on disk
                        try { System.IO.File.Delete(file); } catch { }
                        converted++;
                    }
                    else
                    {
                        failed++;
                    }

                    progressArgs.CurrentProgressValue = i + 1;
                }
            }, new GlobalProgressOptions($"Converting media to MP4 (0/{files.Count})...", true)
            {
                IsIndeterminate = false
            });

            // Descriptions still point at the originals we just replaced, so repair
            // them rather than leaving broken references behind.
            var repairedCache = _cacheService.RepairDescriptionsOnDisk();
            var (repointed, removed) = RepairStoredDescriptions();
            var updated = repairedCache + repointed;

            var savedMb = (before - after) / 1048576.0;
            _api.Dialogs.ShowMessage(
                $"Converted: {converted}\n" +
                (failed > 0 ? $"Failed: {failed}\n" : "") +
                (converted > 0 ? $"\nDisk saved: {savedMb:F0} MB" : "") +
                (updated > 0 ? $"\nDescription links updated: {updated}" : "") +
                (removed > 0 ? $"\nDead links removed: {removed}" : ""),
                Constants.PluginName);

            if (_lastSelectedGame != null) TryInjectAndRender(_lastSelectedGame);
        }

        // Media the user added is written into the game's Playnite description as a
        // playgif.local URL. Two things can invalidate it: conversion replaces the
        // file with an MP4, or the cache is cleared and the file is gone entirely.
        // The first is re-pointed; the second has nothing to point at, so the dead
        // tag is removed rather than left rendering a broken image.
        private (int repointed, int removed) RepairStoredDescriptions()
        {
            var repointed = 0;
            var removed = 0;
            var basePath = System.IO.Path.Combine(
                GetPluginUserDataPath(), Common.Constants.GamesCacheFolder);

            foreach (var game in _api.Database.Games)
            {
                var desc = game.Description;
                if (string.IsNullOrEmpty(desc)) continue;
                if (desc.IndexOf(Constants.VirtualHostName, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var gameDir = System.IO.Path.Combine(basePath, game.Id.ToString());
                var updated = desc;

                // Re-point anything that was converted
                if (System.IO.Directory.Exists(gameDir))
                {
                    updated = RepointConvertedMedia(updated, game.Id, gameDir, out var fixedCount);
                    repointed += fixedCount;
                }

                // Drop tags whose file is gone with no replacement — clearing the
                // cache removes the media but leaves the description pointing at it
                var stripped = StripDeadLocalMedia(updated, game.Id, gameDir, out var n);
                if (n > 0) { updated = stripped; removed += n; }

                if (updated != desc)
                {
                    game.Description = updated;
                    _api.Database.Games.Update(game);
                }
            }

            if (repointed > 0) Logger.Info($"Re-pointed {repointed} converted media reference(s).");
            if (removed > 0) Logger.Info($"Removed {removed} dead media reference(s).");
            return (repointed, removed);
        }

        // Re-points converted media and, critically, turns the <img> into a <video>.
        // A plain string replace of the filename leaves <img src="...mp4">, which
        // never renders — the element type has to change with the extension.
        private static string RepointConvertedMedia(string html, Guid gameId, string gameDir, out int changed)
        {
            changed = 0;
            try
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var imgs = doc.DocumentNode.SelectNodes("//img[@src]")
                    ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>();

                foreach (var img in imgs.ToList())
                {
                    var src = img.GetAttributeValue("src", "");
                    if (src.IndexOf(Constants.VirtualHostName, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var fileName = src.Split('?')[0].Split('/').Last();
                    if (string.IsNullOrEmpty(fileName)) continue;

                    var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                    var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
                    var mp4Name = stem + ".mp4";
                    var mp4OnDisk = System.IO.File.Exists(System.IO.Path.Combine(gameDir, mp4Name));

                    // Two shapes land here: an <img> still naming the original
                    // (converted, file gone), and an <img> already naming the MP4
                    // (an earlier string-replace repair that only swapped the name).
                    var wasConverted = ext != ".mp4"
                        && !System.IO.File.Exists(System.IO.Path.Combine(gameDir, fileName));
                    if (!(mp4OnDisk && (wasConverted || ext == ".mp4"))) continue;

                    var video = doc.CreateElement("video");
                    video.SetAttributeValue("autoplay", "");
                    video.SetAttributeValue("muted", "");
                    video.SetAttributeValue("loop", "");
                    video.SetAttributeValue("playsinline", "");
                    video.SetAttributeValue("style", "max-width:100%;height:auto;display:block;");

                    var source = doc.CreateElement("source");
                    source.SetAttributeValue("src",
                        $"https://{Constants.VirtualHostName}/{gameId}/{mp4Name}");
                    source.SetAttributeValue("type", "video/mp4");
                    video.AppendChild(source);

                    img.ParentNode.ReplaceChild(video, img);
                    changed++;
                }

                // Non-img references only need the filename updated
                foreach (var node in doc.DocumentNode.SelectNodes("//source[@src] | //video[@src]")
                    ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
                {
                    var src = node.GetAttributeValue("src", "");
                    if (src.IndexOf(Constants.VirtualHostName, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var fileName = src.Split('?')[0].Split('/').Last();
                    var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                    if (ext == ".mp4") continue;
                    if (System.IO.File.Exists(System.IO.Path.Combine(gameDir, fileName))) continue;

                    var mp4Name = System.IO.Path.GetFileNameWithoutExtension(fileName) + ".mp4";
                    if (!System.IO.File.Exists(System.IO.Path.Combine(gameDir, mp4Name))) continue;

                    node.SetAttributeValue("src",
                        $"https://{Constants.VirtualHostName}/{gameId}/{mp4Name}");
                    if (node.Name == "source") node.SetAttributeValue("type", "video/mp4");
                    changed++;
                }

                return changed > 0 ? doc.DocumentNode.OuterHtml : html;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to re-point converted media for game {gameId}");
                changed = 0;
                return html;
            }
        }

        // Removes img/video tags whose playgif.local file no longer exists
        private static string StripDeadLocalMedia(string html, Guid gameId, string gameDir, out int removed)
        {
            removed = 0;
            try
            {
                var doc = new HtmlAgilityPack.HtmlDocument();
                doc.LoadHtml(html);

                var dead = new List<HtmlAgilityPack.HtmlNode>();

                foreach (var node in doc.DocumentNode
                    .SelectNodes("//img[@src] | //source[@src] | //video[@src]")
                    ?? Enumerable.Empty<HtmlAgilityPack.HtmlNode>())
                {
                    var src = node.GetAttributeValue("src", "");
                    if (src.IndexOf(Constants.VirtualHostName, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var fileName = src.Split('?')[0].Split('/').Last();
                    if (string.IsNullOrEmpty(fileName)) continue;
                    if (System.IO.File.Exists(System.IO.Path.Combine(gameDir, fileName))) continue;

                    // Remove the whole <video> rather than orphaning an empty shell
                    var target = node.ParentNode?.Name == "video" ? node.ParentNode : node;
                    if (!dead.Contains(target)) dead.Add(target);
                }

                if (dead.Count == 0) return html;

                foreach (var n in dead) n.ParentNode?.RemoveChild(n);
                removed = dead.Count;
                return doc.DocumentNode.OuterHtml;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to strip dead media for game {gameId}");
                removed = 0;
                return html;
            }
        }

        // Opens the description in a WYSIWYG editor. Edits are written to PlayGif's
        // cached description, which is what the renderer displays.
        private async void EditDescription(Game game)
        {
            try
            {
                if (_steamService == null)
                {
                    var basePath = GetPluginUserDataPath();
                    _steamService = new SteamDescriptionService(
                        basePath, () => _api.ApplicationSettings.Language);
                }

                // Load whatever is actually being displayed: the cached rich
                // description when one exists, otherwise Playnite's own text.
                string html;
                if (_steamService.HasCachedDescription(game.Id))
                    html = await _steamService.GetRichDescriptionAsync(game) ?? "";
                else
                    html = game.Description ?? "";

                var window = new Views.DescriptionEditorWindow(
                    html, game.Id, GetPluginUserDataPath(), game.Name)
                {
                    Owner = Application.Current.MainWindow
                };

                if (window.ShowDialog() != true) return;

                var edited = window.ResultHtml ?? "";

                // Saving always writes the cached copy, creating it when the game
                // had none — otherwise editing a plain-metadata game would appear
                // to do nothing, since the cache is what the renderer reads.
                _steamService.SaveCachedDescription(game.Id, edited);
                Logger.Info($"Saved edited description for {game.Name} ({edited.Length} chars)");

                if (_lastSelectedGame?.Id == game.Id) TryInjectAndRender(game);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to edit description for {game.Name}");
                _api.Dialogs.ShowMessage(
                    $"Could not edit the description: {ex.Message}", Constants.PluginName);
            }
        }

        // Standalone repair for users who converted before this existed, or whose
        // descriptions reference media that is no longer on disk.
        public void RunDescriptionRepair()
        {
            if (_cacheService == null)
            {
                _api.Dialogs.ShowMessage(
                    "PlayGif is still starting up. Try again in a moment.",
                    Constants.PluginName);
                return;
            }

            var repairedCache = _cacheService.RepairDescriptionsOnDisk();
            var (repointed, removed) = RepairStoredDescriptions();
            var updated = repairedCache + repointed;

            if (updated == 0 && removed == 0)
            {
                _api.Dialogs.ShowMessage(
                    "No broken media links found — descriptions already point at the right files.",
                    Constants.PluginName);
            }
            else
            {
                _api.Dialogs.ShowMessage(
                    (updated > 0 ? $"Re-pointed {updated} link(s) at converted MP4 files.\n" : "") +
                    (removed > 0
                        ? $"Removed {removed} link(s) whose media is no longer on disk.\n\n" +
                          "Those files were deleted from the cache, so there was nothing left to " +
                          "show. Re-add the media if you still want it."
                        : ""),
                    Constants.PluginName);
            }

            if (_lastSelectedGame != null) TryInjectAndRender(_lastSelectedGame);
        }

        // Called from the Theme Support settings tab
        public void RunLayoutReport()
        {
            if (_viewMonitor == null)
            {
                _api.Dialogs.ShowMessage(
                    "PlayGif has not started rendering yet. Open a game in your library first, then run the report.",
                    Constants.PluginName);
                return;
            }

            _viewMonitor.DumpDiagnostics(Application.Current.MainWindow);

            var view = _api.MainView.ActiveDesktopView.ToString();
            var attached = _viewMonitor.IsInjected
                ? "attached to the description panel"
                : "NOT attached — descriptions will not animate";

            var result = _api.Dialogs.ShowMessage(
                $"Layout report written to extension.log.\n\n" +
                $"Active view: {view}\n" +
                $"Renderer: {attached}\n\n" +
                "Open the log folder now?",
                Constants.PluginName,
                MessageBoxButton.YesNo);

            if (result == MessageBoxResult.Yes) OpenLogFolder();
        }

        public void OpenLogFolderPublic() => OpenLogFolder();

        // Opens Explorer with extension.log selected. Playnite writes logs next to
        // the config, which is the install dir in portable mode and %AppData% otherwise.
        private void OpenLogFolder()
        {
            try
            {
                var candidates = new[]
                {
                    _api.Paths.ConfigurationPath,
                    System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "")
                };

                foreach (var dir in candidates)
                {
                    if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;

                    var log = System.IO.Path.Combine(dir, "extension.log");
                    if (System.IO.File.Exists(log))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{log}\"");
                        return;
                    }
                }

                // No extension.log yet — just open the configuration folder
                if (System.IO.Directory.Exists(_api.Paths.ConfigurationPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{_api.Paths.ConfigurationPath}\"");
                    return;
                }

                _api.Dialogs.ShowMessage(
                    $"Could not locate the log folder.\nExpected it at:\n{_api.Paths.ConfigurationPath}",
                    Constants.PluginName);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to open the log folder");
                _api.Dialogs.ShowMessage($"Failed to open the log folder: {ex.Message}", Constants.PluginName);
            }
        }

        private async void DownloadAndInsertMedia(Game game, string url, string position)
        {
            try
            {
                var uri = new Uri(url);
                var baseName = SanitizeFileName(System.IO.Path.GetFileName(uri.AbsolutePath));
                if (string.IsNullOrEmpty(baseName)) baseName = "media";

                var gameDir = System.IO.Path.Combine(
                    GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, game.Id.ToString());
                System.IO.Directory.CreateDirectory(gameDir);

                // The real format comes from the response, not the URL. Image hosts
                // routinely serve a GIF from a path with no extension; guessing gave
                // .mp4 and wrapped it in a <video> that could not decode, which is
                // why added media showed as an empty gap.
                var fileName = await MediaCacheService.DownloadAndDetectAsync(url, gameDir, baseName);

                var localUrl = $"https://{Common.Constants.VirtualHostName}/{game.Id}/{fileName}";
                var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                var tag = BuildMediaTag(localUrl, ext);

                Application.Current.Dispatcher.Invoke(() =>
                    InsertMediaTag(game, tag, position));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to download media from: {url}");
                Application.Current.Dispatcher.Invoke(() =>
                    _api.Dialogs.ShowMessage($"Failed to download: {ex.Message}", Constants.PluginName));
            }
        }


        private void SearchWebImages(Game game, string position)
        {
            var input = _api.Dialogs.SelectString(
                "Search for images/GIFs:", Constants.PluginName, $"{game.Name} gif");
            if (input == null || !input.Result || string.IsNullOrWhiteSpace(input.SelectedString))
                return;

            var searchTerm = input.SelectedString.Trim();
            var imageOptions = new System.Collections.Generic.List<ImageFileOption>();
            var fullUrls = new System.Collections.Generic.Dictionary<string, string>();

            try
            {
                using (var webView = _api.WebViews.CreateOffscreenView())
                {
                    var searchUrl = $"https://www.google.com/search?tbm=isch&q={Uri.EscapeDataString(searchTerm)}&safe=on";
                    webView.NavigateAndWait(searchUrl);

                    // Handle Google consent form
                    var currentUrl = webView.GetCurrentAddress();
                    if (currentUrl.StartsWith("https://consent.google.com", StringComparison.OrdinalIgnoreCase))
                    {
                        webView.EvaluateScriptAsync(@"document.getElementsByTagName('form')[0].submit();").Wait();
                        System.Threading.Thread.Sleep(3000);
                        webView.NavigateAndWait(searchUrl);
                    }

                    var pageSource = webView.GetPageSource();

                    // Parse image results — same regex Playnite uses
                    var formatted = System.Text.RegularExpressions.Regex.Replace(
                        pageSource, @"\r\n?|\n", string.Empty);
                    var matches = System.Text.RegularExpressions.Regex.Matches(
                        formatted,
                        @"\[""(https:\/\/encrypted-[^,]+?)"",\d+,\d+\],\[""(http.+?)"",(\d+),(\d+)\]");

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        try
                        {
                            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<
                                System.Collections.Generic.List<System.Collections.Generic.List<object>>>(
                                $"[{match.Value}]");

                            var thumbUrl = data[0][0].ToString();
                            var imageUrl = data[1][0].ToString();
                            var height = data[1][1].ToString();
                            var width = data[1][2].ToString();

                            if (imageOptions.Any(o => o.Description?.Contains(imageUrl) == true))
                                continue;

                            var option = new ImageFileOption
                            {
                                Name = $"{width}x{height}",
                                Description = imageUrl,
                                Path = thumbUrl
                            };
                            imageOptions.Add(option);
                            fullUrls[thumbUrl] = imageUrl;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Web image search failed");
                _api.Dialogs.ShowMessage($"Search failed: {ex.Message}", Constants.PluginName);
                return;
            }

            if (imageOptions.Count == 0)
            {
                _api.Dialogs.ShowMessage("No images found.", Constants.PluginName);
                return;
            }

            var mediaItems = imageOptions.Select(o => new Views.MediaItem
            {
                ThumbUrl = o.Path,
                FullUrl = fullUrls.ContainsKey(o.Path) ? fullUrls[o.Path] : o.Description,
                Size = o.Name
            }).ToList();

            var picker = new Views.MediaPickerWindow(mediaItems);
            picker.Title = $"Pick media for {game.Name} ({mediaItems.Count} results)";

            if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedUrl))
                DownloadAndInsertMedia(game, picker.SelectedUrl, position);
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            // Also strip query strings
            var idx = name.IndexOf('?');
            if (idx > 0) name = name.Substring(0, idx);
            if (name.Length > 100) name = name.Substring(0, 100);
            return name;
        }

        private string BuildMediaTag(string url, string ext)
        {
            if (ext == ".gif" || ext == ".webp" || ext == ".apng" || ext == ".png" ||
                ext == ".jpg" || ext == ".jpeg" || ext == ".avif")
            {
                return $"<img src=\"{url}\" style=\"max-width:100%;height:auto;display:block;\" />";
            }

            var type = ext == ".webm" ? "video/webm" : "video/mp4";
            return $"<video autoplay muted loop playsinline style=\"max-width:100%;height:auto;display:block;\">" +
                $"<source src=\"{url}\" type=\"{type}\"></video>";
        }

        private void InsertMediaTag(Game game, string tag, string position)
        {
            // Update the Playnite DB description
            var desc = game.Description ?? "";
            if (position == "top")
                game.Description = tag + "\n" + desc;
            else
                game.Description = desc + "\n" + tag;
            _api.Database.Games.Update(game);

            // Also update the cached rich description if one exists
            if (_steamService?.HasCachedDescription(game.Id) == true)
            {
                _steamService.UpdateCachedDescription(game.Id, tag, position);
            }

            // Keep the selected-game snapshot in sync so the re-render sees the new tag
            if (_lastSelectedGame?.Id == game.Id)
            {
                _lastSelectedGame = game;
                TryInjectAndRender(game);
            }
        }

        private void FetchStoreDescription(System.Collections.Generic.List<Game> games, string store)
        {
            if (_steamService == null)
            {
                var basePath = GetPluginUserDataPath();
                _steamService = new SteamDescriptionService(basePath, () => _api.ApplicationSettings.Language);
            }

            foreach (var game in games)
            {
                string storeId = null;
                string storeName = store;

                if (store == "steam")
                {
                    storeId = _steamService.ResolveSteamAppId(game);
                    if (string.IsNullOrEmpty(storeId))
                    {
                        _api.Dialogs.ShowMessage(
                            $"Could not resolve Steam AppId for: {game.Name}\n\n" +
                            "Game must be from Steam library or have a Steam store link.",
                            Constants.PluginName);
                        continue;
                    }
                }
                else if (store == "gog")
                {
                    storeId = _steamService.ResolveGogProductId(game);
                    if (string.IsNullOrEmpty(storeId))
                    {
                        _api.Dialogs.ShowMessage(
                            $"Could not resolve GOG product ID for: {game.Name}\n\n" +
                            "Game must be from GOG library or have a GOG store link.",
                            Constants.PluginName);
                        continue;
                    }
                }

                _steamService.ClearCachedDescription(game.Id);

                System.Threading.Tasks.Task<string> task;
                if (store == "gog")
                    task = _steamService.FetchGogDescriptionAsync(game);
                else
                    task = _steamService.GetRichDescriptionAsync(game);

                var capturedGame = game;
                var capturedId = storeId;
                task.ContinueWith(t =>
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var html = t.Result;
                        if (string.IsNullOrEmpty(html))
                        {
                            _api.Dialogs.ShowMessage(
                                $"Failed to fetch from {storeName} for: {capturedGame.Name} (ID: {capturedId})",
                                Constants.PluginName);
                            return;
                        }

                        if (Settings.EnableDebugMode)
                        {
                            var videoCount = System.Text.RegularExpressions.Regex.Matches(html, "<video").Count;
                            var imgCount = System.Text.RegularExpressions.Regex.Matches(html, "<img").Count;
                            _api.Dialogs.ShowMessage(
                                $"Fetched from {storeName} for: {capturedGame.Name}\n" +
                                $"Store ID: {capturedId}\n" +
                                $"Size: {html.Length} chars\n" +
                                $"Videos: {videoCount}  |  Images: {imgCount}",
                                Constants.PluginName);
                        }

                        if (_lastSelectedGame?.Id == capturedGame.Id)
                        {
                            _lastSelectedGame = capturedGame;
                            TryInjectAndRender(capturedGame);
                        }
                    }));
                });
            }
        }

        private void UpdateThemeColors()
        {
            try
            {
                var textColor = GetResourceColor("TextColor", Colors.White);
                var linkColor = GetResourceColor("GlyphColor", Colors.CornflowerBlue);
                var fontSize = GetResourceDouble("FontSize", 14.0);
                var fontFamily = GetResourceString("FontFamily", "Segoe UI");
                _renderer.UpdateTheme(textColor, linkColor, fontSize, fontFamily);
            }
            catch { }
        }

        private Color GetResourceColor(string key, Color fallback)
        {
            var res = Application.Current.TryFindResource(key);
            if (res is SolidColorBrush brush) return brush.Color;
            if (res is Color color) return color;
            return fallback;
        }

        private double GetResourceDouble(string key, double fallback)
        {
            var res = Application.Current.TryFindResource(key);
            if (res is double d) return d;
            return fallback;
        }

        private string GetResourceString(string key, string fallback)
        {
            var res = Application.Current.TryFindResource(key);
            if (res is FontFamily ff) return ff.Source;
            if (res is string s) return s;
            return fallback;
        }

        #region Resource Management

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            _renderer?.PauseAll();
            _renderer?.SetMemoryLevel(true);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            _renderer?.SetMemoryLevel(false);
            _renderer?.ResumeAll();
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
            _renderer?.SetMemoryLevel(false);
            _renderer?.ResumeAll();
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            _renderer?.PauseAll();
            _renderer?.SetMemoryLevel(true);
        }

        #endregion

        #region Game Menu

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var items = new List<GameMenuItem>();

            // Video scale — first in menu. 100% is the "Reset to default" case,
            // so it is not offered as an explicit override here.
            foreach (var pct in Constants.VideoScaleSteps.Where(p => p < 100))
            {
                var scale = pct;
                items.Add(new GameMenuItem
                {
                    MenuSection = Constants.MenuSectionName + "|Video scale",
                    Description = $"{scale}%",
                    Action = (menuArgs) =>
                    {
                        foreach (var game in menuArgs.Games)
                            _cacheService?.SetGameVideoScale(game.Id, scale);
                        if (_renderer?.IsInitialized == true)
                            _ = _renderer.WebViewControl.CoreWebView2.ExecuteScriptAsync(
                                $"setVideoScale({scale})");
                    }
                });
            }

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName + "|Video scale",
                Description = "Reset to default",
                Action = (menuArgs) =>
                {
                    foreach (var game in menuArgs.Games)
                        _cacheService?.SetGameVideoScale(game.Id, null);
                    if (_renderer?.IsInitialized == true)
                        _ = _renderer.WebViewControl.CoreWebView2.ExecuteScriptAsync(
                            $"setVideoScale({Settings.VideoScale})");
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName + "|Fetch description",
                Description = "From Steam",
                Action = (menuArgs) =>
                {
                    FetchStoreDescription(menuArgs.Games, "steam");
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName + "|Fetch description",
                Description = "From GOG",
                Action = (menuArgs) =>
                {
                    FetchStoreDescription(menuArgs.Games, "gog");
                }
            });

            // Add media — position picks the submenu, source picks the row
            foreach (var position in new[] { "top", "bottom" })
            {
                var pos = position;
                var section = Constants.MenuSectionName + "|Add media|" +
                    (pos == "top" ? "To top" : "To bottom");

                // Web search first — it is the most-used way to add media
                items.Add(new GameMenuItem
                {
                    MenuSection = section,
                    Description = "Search web images",
                    Action = (menuArgs) =>
                    {
                        var game = menuArgs.Games.FirstOrDefault();
                        if (game == null) return;
                        SearchWebImages(game, pos);
                    }
                });

                items.Add(new GameMenuItem
                {
                    MenuSection = section,
                    Description = "Local file",
                    Action = (menuArgs) =>
                    {
                        var game = menuArgs.Games.FirstOrDefault();
                        if (game == null) return;

                        var filePath = _api.Dialogs.SelectFile(
                            "Media files|*.gif;*.mp4;*.webp;*.apng;*.avif;*.png;*.jpg|All files|*.*");
                        if (string.IsNullOrEmpty(filePath)) return;

                        var tag = CopyAndBuildMediaTag(game, filePath);
                        if (tag != null) InsertMediaTag(game, tag, pos);
                    }
                });

                items.Add(new GameMenuItem
                {
                    MenuSection = section,
                    Description = "From URL",
                    Action = (menuArgs) =>
                    {
                        var game = menuArgs.Games.FirstOrDefault();
                        if (game == null) return;

                        var input = _api.Dialogs.SelectString(
                            "Enter media URL (GIF, MP4, image):", Constants.PluginName, "");
                        if (input == null || !input.Result ||
                            string.IsNullOrWhiteSpace(input.SelectedString)) return;

                        DownloadAndInsertMedia(game, input.SelectedString.Trim(), pos);
                    }
                });
            }

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Edit description...",
                Action = (menuArgs) =>
                {
                    var game = menuArgs.Games.FirstOrDefault();
                    if (game != null) EditDescription(game);
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Remove media...",
                Action = (menuArgs) =>
                {
                    var game = menuArgs.Games.FirstOrDefault();
                    if (game != null) MediaLibrary.RemoveMedia(game);
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Reset description",
                Action = (menuArgs) =>
                {
                    if (_cacheService == null) return;

                    // This discards the cached description and every cached media
                    // file, including anything added or edited by hand. Name and
                    // wording say so, because it is not recoverable.
                    var count = menuArgs.Games.Count;
                    var confirm = _api.Dialogs.ShowMessage(
                        (count == 1
                            ? "Reset this game's description?\n\n"
                            : $"Reset the description for {count} games?\n\n") +
                        "This discards the cached description and all cached media, " +
                        "including media you added and any changes made with " +
                        "\"Edit description\". The description is re-fetched from the " +
                        "store as you browse.\n\nThis cannot be undone.",
                        Constants.PluginName,
                        MessageBoxButton.YesNo);
                    if (confirm != MessageBoxResult.Yes) return;

                    foreach (var game in menuArgs.Games)
                    {
                        _cacheService.ClearGameCache(game.Id);
                        _steamService?.ClearAllCachedDescriptions(game.Id);
                    }
                    if (_renderer?.IsInitialized == true && _lastSelectedGame != null)
                        TryInjectAndRender(_lastSelectedGame);
                    _api.Dialogs.ShowMessage(
                        "Description reset. It will re-fetch as you browse.",
                        Constants.PluginName);
                }
            });

            // Diagnostic — hidden unless debug mode is on
            if (!Settings.EnableDebugMode) return items;

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Dump layout diagnostics",
                Action = (menuArgs) =>
                {
                    // Runs against whatever view is open right now, which is the
                    // only way to capture Grid-view and tab state accurately.
                    _viewMonitor?.DumpDiagnostics(Application.Current.MainWindow);
                    OpenLogFolder();
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Open debug log folder",
                Action = (menuArgs) => OpenLogFolder()
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Preview description",
                Action = (menuArgs) =>
                {
                    var game = menuArgs.Games.FirstOrDefault();
                    if (game == null) return;

                    var storedHtml = game.Description ?? "";

                    string cachedHtml = "";
                    var hasCached = _steamService?.HasCachedDescription(game.Id) == true;
                    if (hasCached)
                    {
                        var task = _steamService.GetRichDescriptionAsync(game);
                        cachedHtml = task.GetAwaiter().GetResult() ?? "";
                    }

                    var appId = _steamService?.ResolveSteamAppId(game) ?? "N/A";
                    var gogId = _steamService?.ResolveGogProductId(game) ?? "N/A";

                    string AnalyzeHtml(string html, string label)
                    {
                        if (string.IsNullOrEmpty(html)) return $"--- {label}: (empty) ---\n";
                        var videos = System.Text.RegularExpressions.Regex.Matches(html, "<video").Count;
                        var imgs = System.Text.RegularExpressions.Regex.Matches(html, "<img").Count;
                        var iframes = System.Text.RegularExpressions.Regex.Matches(html, "<iframe").Count;
                        var embeds = System.Text.RegularExpressions.Regex.Matches(html, "<embed").Count;
                        var objects = System.Text.RegularExpressions.Regex.Matches(html, "<object").Count;
                        var sources = System.Text.RegularExpressions.Regex.Matches(html, "<source").Count;

                        return $"--- {label} ({html.Length} chars) ---\n" +
                            $"<video>: {videos}  |  <img>: {imgs}  |  <source>: {sources}\n" +
                            $"<iframe>: {iframes}  |  <embed>: {embeds}  |  <object>: {objects}\n" +
                            $".webm: {html.Contains(".webm")}  |  .mp4: {html.Contains(".mp4")}\n" +
                            $".gif: {html.Contains(".gif")}  |  .avif: {html.Contains(".avif")}\n" +
                            $".webp: {html.Contains(".webp")}  |  .png: {html.Contains(".png")}\n" +
                            $"playgif.local: {html.Contains(Common.Constants.VirtualHostName)}\n\n" +
                            $"First 800 chars:\n" +
                            html.Substring(0, System.Math.Min(800, html.Length)) + "\n";
                    }

                    var cacheDir = System.IO.Path.Combine(
                        GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, game.Id.ToString());
                    var cacheFiles = System.IO.Directory.Exists(cacheDir)
                        ? string.Join("\n  ", System.IO.Directory.GetFiles(cacheDir).Select(f =>
                            $"{System.IO.Path.GetFileName(f)} ({new System.IO.FileInfo(f).Length / 1024} KB)"))
                        : "(empty)";

                    var msg = $"=== {game.Name} ===\n\n" +
                        $"Playnite Game ID: {game.Id}\n" +
                        $"Plugin: {(game.PluginId == Guid.Empty ? "Manual" : game.PluginId.ToString())}\n" +
                        $"Steam AppId: {appId}  |  GOG ID: {gogId}\n" +
                        $"PlayGif cache: {(hasCached ? "YES" : "none")}\n" +
                        $"Cache folder: {cacheDir}\n" +
                        $"Cached files:\n  {cacheFiles}\n\n" +
                        AnalyzeHtml(storedHtml, "Stored Description") + "\n" +
                        (hasCached ? AnalyzeHtml(cachedHtml, "Cached Rich Description") : "");

                    var tempPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), $"PlayGif_Preview_{game.Name}.txt");
                    System.IO.File.WriteAllText(tempPath, msg);
                    System.Diagnostics.Process.Start(tempPath);
                }
            });

            return items;
        }

        #endregion

        #region Bulk Fetch

        internal void RunBulkSteamFetch()
        {
            if (_steamService == null)
            {
                // Service not initialized yet — create a temporary one for the fetch
                var basePath = GetPluginUserDataPath();
                _steamService = new SteamDescriptionService(basePath, () => _api.ApplicationSettings.Language);
            }

            var allGames = _api.Database.Games.ToList();
            var resolvable = allGames.Where(g => _steamService.ResolveSteamAppId(g) != null).ToList();
            var alreadyCached = resolvable.Count(g => _steamService.HasCachedDescription(g.Id));
            var toFetch = resolvable.Count - alreadyCached;

            var result = _api.Dialogs.ShowMessage(
                $"Found {resolvable.Count} games with Steam AppIds.\n" +
                $"Already cached: {alreadyCached}\n" +
                $"To fetch: {toFetch}\n\n" +
                "This may take a while depending on library size. Continue?",
                Constants.PluginName,
                System.Windows.MessageBoxButton.YesNo);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            var cts = new System.Threading.CancellationTokenSource();
            var task = _steamService.BulkFetchAsync(resolvable, _api, cts.Token);
            task.ContinueWith(t =>
            {
                if (t.IsCompleted && !t.IsFaulted)
                {
                    var (fetched, skipped, failed) = t.Result;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        _api.Dialogs.ShowMessage(
                            $"Fetch complete!\n\nFetched: {fetched}\nFailed: {failed}",
                            Constants.PluginName)));
                }
            });
        }

        #endregion

        #region Settings

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new PlayGifSettingsView();
        }

        internal PlayGifSettings Settings => _settingsViewModel.Settings;

        #endregion

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            if (Application.Current?.MainWindow != null)
            {
                Application.Current.MainWindow.Activated -= OnWindowActivated;
                Application.Current.MainWindow.Deactivated -= OnWindowDeactivated;
            }
            _viewMonitor?.Restore();
            _renderer?.Dispose();
        }
    }
}
