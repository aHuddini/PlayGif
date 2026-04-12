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
        private Game _lastSelectedGame;
        private int _injectionAttempts;

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
                _steamService = new SteamDescriptionService(basePath);
                _renderer = new DescriptionRendererService(Settings, () => basePath);

                // Create the environment (doesn't need visual tree)
                await _renderer.PrepareEnvironmentAsync();

                // Create the WebView2 control on the UI thread
                _renderer.CreateWebViewControl();

                // Set up the visual tree monitor
                _viewMonitor = new DescriptionViewMonitor(
                    () => _renderer.WebViewControl,
                    () => Settings.EnableAnimatedDescriptions);
                _viewMonitor.StartMonitoring();

                // Subscribe to window events
                if (Application.Current?.MainWindow != null)
                {
                    Application.Current.MainWindow.Activated += OnWindowActivated;
                    Application.Current.MainWindow.Deactivated += OnWindowDeactivated;
                }

                var isFullscreen = _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
                Logger.Info($"Mode: {(isFullscreen ? "Fullscreen" : "Desktop")}. Services prepared.");

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

                // Step 1: Inject into visual tree if not already done
                if (!_viewMonitor.IsInjected && _injectionAttempts < 5)
                {
                    _injectionAttempts++;
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

                        // Full content height — no internal WebView2 scrolling
                        // SetWindowRgn clips the HWND, parent ScrollViewer scrolls it
                        _renderer.HeightReported += (height) =>
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                _renderer.WebViewControl.Height = height;
                            }));
                        };

                        // Forward wheel events from WebView2 to parent ScrollViewer
                        // Batch at render frame rate for smoothness
                        double _pendingDelta = 0;
                        bool _renderHooked = false;

                        _renderer.ScrollOverflow += (delta) =>
                        {
                            _pendingDelta += delta;
                            if (!_renderHooked)
                            {
                                _renderHooked = true;
                                CompositionTarget.Rendering += OnRenderFrame;
                            }
                        };

                        void OnRenderFrame(object s2, EventArgs e2)
                        {
                            if (_pendingDelta != 0)
                            {
                                var sv = _viewMonitor.ParentScrollViewer;
                                if (sv != null)
                                    sv.ScrollToVerticalOffset(sv.VerticalOffset + _pendingDelta);
                                _pendingDelta = 0;
                            }
                            else
                            {
                                _renderHooked = false;
                                CompositionTarget.Rendering -= OnRenderFrame;
                            }
                        }


                        Logger.Info("WebView2 fully initialized.");
                    }
                    else
                    {
                        Logger.Info("Injection failed — PART_HtmlDescription not found yet.");
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
                {
                    html = ReplaceVideosWithPosters(html);
                    Logger.Info($"After poster replacement: {html.Length} chars, has video: {html.Contains("<video")}");
                    Logger.Info($"Poster preview: {html.Substring(0, System.Math.Min(300, html.Length))}");
                }

                html = _cacheService.RewriteDescriptionHtml(html, game.Id);

                _ = _renderer.SetDescriptionAsync(html);
                UpdateThemeColors();

                // Apply video scale — per-game override or global default
                var scale = _cacheService.GetGameVideoScale(game.Id) ?? Settings.VideoScale;
                if (scale != 100 && _renderer.IsInitialized)
                {
                    _ = _renderer.WebViewControl.CoreWebView2.ExecuteScriptAsync(
                        $"setVideoScale({scale})");
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
                                // Keep video but disable playback — shows first frame
                                video.SetAttributeValue("preload", "metadata");
                                video.Attributes.Remove("autoplay");
                                video.Attributes.Remove("loop");
                                // Remove all source elements except the MP4
                                foreach (var s in video.SelectNodes(".//source")?.ToList()
                                    ?? new System.Collections.Generic.List<HtmlAgilityPack.HtmlNode>())
                                {
                                    if (s != source) s.Remove();
                                }
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

        private async void PreviewAndInsertMedia(Game game, string url, string position)
        {
            try
            {
                var uri = new Uri(url);
                var fileName = System.IO.Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrEmpty(fileName) || !fileName.Contains("."))
                    fileName = "media" + (url.Contains(".webm") ? ".webm" : url.Contains(".gif") ? ".gif" : ".mp4");

                var gameDir = System.IO.Path.Combine(
                    GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, game.Id.ToString());
                System.IO.Directory.CreateDirectory(gameDir);
                var destPath = System.IO.Path.Combine(gameDir, fileName);

                // Download the file
                using (var client = new System.Net.Http.HttpClient())
                {
                    var bytes = await client.GetByteArrayAsync(url);
                    System.IO.File.WriteAllBytes(destPath, bytes);
                }

                var localUrl = $"https://{Common.Constants.VirtualHostName}/{game.Id}/{fileName}";
                var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                var tag = BuildMediaTag(localUrl, ext);
                var fileSize = new System.IO.FileInfo(destPath).Length / 1024.0;

                InsertMediaTag(game, tag, position);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to preview media from: {url}");
                _api.Dialogs.ShowMessage($"Failed to download: {ex.Message}", Constants.PluginName);
                // Restore original
                if (_lastSelectedGame?.Id == game.Id)
                    TryInjectAndRender(game);
            }
        }

        private async void DownloadAndInsertMedia(Game game, string url, string position)
        {
            try
            {
                var uri = new Uri(url);
                var fileName = System.IO.Path.GetFileName(uri.AbsolutePath);
                if (string.IsNullOrEmpty(fileName) || !fileName.Contains("."))
                    fileName = "media" + (url.Contains(".webm") ? ".webm" : url.Contains(".gif") ? ".gif" : ".mp4");

                var gameDir = System.IO.Path.Combine(
                    GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, game.Id.ToString());
                System.IO.Directory.CreateDirectory(gameDir);
                var destPath = System.IO.Path.Combine(gameDir, fileName);

                // Download the file
                using (var client = new System.Net.Http.HttpClient())
                {
                    var bytes = await client.GetByteArrayAsync(url);
                    System.IO.File.WriteAllBytes(destPath, bytes);
                }

                var localUrl = $"https://{Common.Constants.VirtualHostName}/{game.Id}/{fileName}";
                var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                var tag = BuildMediaTag(localUrl, ext);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    InsertMediaTag(game, tag, position);
                    _api.Dialogs.ShowMessage(
                        $"Downloaded and added {fileName} to {game.Name} ({position}).",
                        Constants.PluginName);
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to download media from: {url}");
                Application.Current.Dispatcher.Invoke(() =>
                    _api.Dialogs.ShowMessage($"Failed to download: {ex.Message}", Constants.PluginName));
            }
        }

        private void SearchAndAddGif(Game game, string position)
        {
            // First get search query from user
            var input = _api.Dialogs.SelectString(
                "Search for GIFs:", Constants.PluginName, game.Name);
            if (input == null || !input.Result || string.IsNullOrWhiteSpace(input.SelectedString))
                return;

            // Search GIPHY
            var imageOptions = new System.Collections.Generic.List<ImageFileOption>();
            // Store full-size URLs keyed by thumbnail path
            var fullUrls = new System.Collections.Generic.Dictionary<string, string>();

            try
            {
                var apiKey = Settings.GiphyApiKey;
                var encoded = Uri.EscapeDataString(input.SelectedString.Trim());
                var url = $"https://api.giphy.com/v1/gifs/search?api_key={apiKey}&q={encoded}&limit=25&rating=g";

                using (var client = new System.Net.Http.HttpClient())
                {
                    var response = client.GetStringAsync(url).GetAwaiter().GetResult();
                    var json = Newtonsoft.Json.Linq.JObject.Parse(response);
                    var data = json["data"] as Newtonsoft.Json.Linq.JArray;

                    if (data != null)
                    {
                        foreach (var gif in data)
                        {
                            var title = gif["title"]?.ToString() ?? "Untitled";
                            var originalUrl = gif["images"]?["original"]?["url"]?.ToString();
                            var thumbUrl = gif["images"]?["fixed_width_small"]?["url"]?.ToString()
                                ?? gif["images"]?["preview_gif"]?["url"]?.ToString()
                                ?? originalUrl;
                            var width = gif["images"]?["original"]?["width"]?.ToString();
                            var height = gif["images"]?["original"]?["height"]?.ToString();
                            var size = gif["images"]?["original"]?["size"]?.ToString();

                            if (!string.IsNullOrEmpty(originalUrl) && !string.IsNullOrEmpty(thumbUrl))
                            {
                                var sizeKb = 0L;
                                long.TryParse(size ?? "0", out sizeKb);

                                var option = new ImageFileOption
                                {
                                    Name = title,
                                    Description = $"{width}x{height} | {sizeKb / 1024} KB",
                                    Path = thumbUrl
                                };
                                imageOptions.Add(option);
                                fullUrls[thumbUrl] = originalUrl;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "GIPHY search failed");
                _api.Dialogs.ShowMessage($"Search failed: {ex.Message}", Constants.PluginName);
                return;
            }

            if (imageOptions.Count == 0)
            {
                _api.Dialogs.ShowMessage("No GIFs found.", Constants.PluginName);
                return;
            }

            // Show image picker grid
            var selected = _api.Dialogs.ChooseImageFile(
                imageOptions,
                $"Pick a GIF for {game.Name} ({imageOptions.Count} results)");

            if (selected == null) return;

            // Get the full-size URL for the selected thumbnail
            var gifUrl = fullUrls.ContainsKey(selected.Path) ? fullUrls[selected.Path] : selected.Path;

            DownloadAndInsertMedia(game, gifUrl, position);
        }

        private static string ExtractFilenameFromTag(string tag)
        {
            var match = System.Text.RegularExpressions.Regex.Match(tag, @"playgif\.local/[^/]+/([^""']+)");
            return match.Success ? match.Groups[1].Value : "unknown";
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

            if (_lastSelectedGame?.Id == game.Id)
                TryInjectAndRender(game);
        }

        private void FetchStoreDescription(System.Collections.Generic.List<Game> games, string store)
        {
            if (_steamService == null)
            {
                var basePath = GetPluginUserDataPath();
                _steamService = new SteamDescriptionService(basePath);
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

                        var videoCount = System.Text.RegularExpressions.Regex.Matches(html, "<video").Count;
                        var imgCount = System.Text.RegularExpressions.Regex.Matches(html, "<img").Count;

                        _api.Dialogs.ShowMessage(
                            $"Fetched from {storeName} for: {capturedGame.Name}\n" +
                            $"Store ID: {capturedId}\n" +
                            $"Size: {html.Length} chars\n" +
                            $"Videos: {videoCount}  |  Images: {imgCount}\n" +
                            $"Has .webm: {html.Contains(".webm")}  |  Has .mp4: {html.Contains(".mp4")}\n\n" +
                            $"First 500 chars:\n" +
                            html.Substring(0, System.Math.Min(500, html.Length)),
                            Constants.PluginName);

                        if (_lastSelectedGame?.Id == capturedGame.Id)
                            TryInjectAndRender(capturedGame);
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

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Clear cached media",
                Action = (menuArgs) =>
                {
                    if (_cacheService == null) return;
                    foreach (var game in menuArgs.Games)
                        _cacheService.ClearGameCache(game.Id);
                    _api.Dialogs.ShowMessage(
                        $"Cleared cached media for {menuArgs.Games.Count} game(s).",
                        Constants.PluginName);
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Preview description HTML",
                Action = (menuArgs) =>
                {
                    var game = menuArgs.Games.FirstOrDefault();
                    if (game == null) return;

                    // Get stored description
                    var storedHtml = game.Description ?? "";

                    // Get cached rich description (if any)
                    string cachedHtml = null;
                    if (_steamService != null)
                        cachedHtml = _steamService.HasCachedDescription(game.Id)
                            ? "(cached rich description available)" : null;

                    // Resolve Steam AppId
                    var appId = _steamService?.ResolveSteamAppId(game) ?? "N/A";

                    // Analyze stored description
                    var sVideoCount = System.Text.RegularExpressions.Regex.Matches(storedHtml, "<video").Count;
                    var sImgCount = System.Text.RegularExpressions.Regex.Matches(storedHtml, "<img").Count;
                    var sGifCount = System.Text.RegularExpressions.Regex.Matches(storedHtml, @"\.(gif|webp|apng)").Count;

                    var msg = $"=== {game.Name} ===\n\n" +
                        $"Source: {(game.PluginId == Guid.Empty ? "Manual" : game.PluginId.ToString())}\n" +
                        $"Steam AppId: {appId}\n" +
                        $"PlayGif cache: {(cachedHtml ?? "none")}\n\n" +
                        $"--- Stored Description ({storedHtml.Length} chars) ---\n" +
                        $"Videos: {sVideoCount}  |  Images: {sImgCount}  |  GIF/WebP/APNG: {sGifCount}\n" +
                        $"Has <video>: {storedHtml.Contains("<video")}\n" +
                        $"Has .webm: {storedHtml.Contains(".webm")}\n" +
                        $"Has .mp4: {storedHtml.Contains(".mp4")}\n" +
                        $"Has .avif: {storedHtml.Contains(".avif")}\n" +
                        $"Has .gif: {storedHtml.Contains(".gif")}\n\n" +
                        $"--- First 500 chars ---\n" +
                        storedHtml.Substring(0, System.Math.Min(500, storedHtml.Length));

                    _api.Dialogs.ShowMessage(msg, Constants.PluginName);
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

            // Add media — local file, top/bottom
            foreach (var position in new[] { "top", "bottom" })
            {
                var pos = position;
                items.Add(new GameMenuItem
                {
                    MenuSection = Constants.MenuSectionName + "|Add media to description",
                    Description = $"Local file → {pos}",
                    Action = (menuArgs) =>
                    {
                        var game = menuArgs.Games.FirstOrDefault();
                        if (game == null) return;

                        var filePath = _api.Dialogs.SelectFile(
                            "Media files|*.gif;*.webm;*.mp4;*.webp;*.apng;*.avif|All files|*.*");
                        if (string.IsNullOrEmpty(filePath)) return;

                        var tag = CopyAndBuildMediaTag(game, filePath);
                        if (tag == null) return;

                        InsertMediaTag(game, tag, pos);
                    }
                });

                items.Add(new GameMenuItem
                {
                    MenuSection = Constants.MenuSectionName + "|Add media to description",
                    Description = $"From URL → {pos}",
                    Action = (menuArgs) =>
                    {
                        var game = menuArgs.Games.FirstOrDefault();
                        if (game == null) return;

                        var input = _api.Dialogs.SelectString(
                            "Enter media URL (GIF, WebM, MP4, image):",
                            Constants.PluginName, "");
                        if (input == null || !input.Result || string.IsNullOrWhiteSpace(input.SelectedString))
                            return;

                        var mediaUrl = input.SelectedString.Trim();
                        PreviewAndInsertMedia(game, mediaUrl, pos);
                    }
                });
            }

            // Search and add GIF
            foreach (var position in new[] { "top", "bottom" })
            {
                var pos = position;
                items.Add(new GameMenuItem
                {
                    MenuSection = Constants.MenuSectionName + "|Add media to description",
                    Description = $"Search GIF → {pos}",
                    Action = (menuArgs) =>
                    {
                        var game = menuArgs.Games.FirstOrDefault();
                        if (game == null) return;

                        if (string.IsNullOrWhiteSpace(Settings.GiphyApiKey))
                        {
                            _api.Dialogs.ShowMessage(
                                "GIPHY API key not set.\n\n" +
                                "Go to PlayGif settings and enter your free API key.\n" +
                                "Get one at: developers.giphy.com",
                                Constants.PluginName);
                            return;
                        }

                        SearchAndAddGif(game, pos);
                    }
                });
            }

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Remove added media",
                Action = (menuArgs) =>
                {
                    var game = menuArgs.Games.FirstOrDefault();
                    if (game == null) return;

                    var virtualHost = Common.Constants.VirtualHostName;
                    var pattern = $"(<img[^>]*{virtualHost}[^>]*/>|<video[^>]*>.*?{virtualHost}.*?</video>)";

                    // Check both stored and cached descriptions
                    var desc = game.Description ?? "";
                    var cachedDesc = "";
                    var hasCached = _steamService?.HasCachedDescription(game.Id) == true;
                    if (hasCached)
                    {
                        var task = _steamService.GetRichDescriptionAsync(game);
                        cachedDesc = task.GetAwaiter().GetResult() ?? "";
                    }

                    var storedMatches = System.Text.RegularExpressions.Regex.Matches(
                        desc, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
                    var cachedMatches = System.Text.RegularExpressions.Regex.Matches(
                        cachedDesc, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);

                    // Build list of all found media
                    var allMatches = new System.Collections.Generic.List<(string match, string source, string name)>();
                    foreach (System.Text.RegularExpressions.Match m in storedMatches)
                    {
                        var fname = ExtractFilenameFromTag(m.Value);
                        allMatches.Add((m.Value, "stored", fname));
                    }
                    foreach (System.Text.RegularExpressions.Match m in cachedMatches)
                    {
                        var fname = ExtractFilenameFromTag(m.Value);
                        if (!allMatches.Exists(a => a.name == fname))
                            allMatches.Add((m.Value, "cached", fname));
                    }

                    if (allMatches.Count == 0)
                    {
                        _api.Dialogs.ShowMessage(
                            "No manually-added PlayGif media found.",
                            Constants.PluginName);
                        return;
                    }

                    // Let user pick which to remove
                    var choices = allMatches.Select(a =>
                        new GenericItemOption(a.name, $"Source: {a.source}")).ToList();

                    var selected = _api.Dialogs.ChooseItemWithSearch(
                        choices,
                        (s) => string.IsNullOrEmpty(s) ? choices :
                            choices.Where(c => c.Name.Contains(s)).ToList(),
                        "",
                        "Select media to remove");

                    if (selected == null) return;

                    var toRemove = allMatches.First(a => a.name == selected.Name);

                    // Remove from stored description
                    if (desc.Contains(toRemove.match))
                    {
                        desc = desc.Replace(toRemove.match, "").Trim();
                        game.Description = desc;
                        _api.Database.Games.Update(game);
                    }

                    // Remove from cached description
                    if (hasCached && cachedDesc.Contains(toRemove.match))
                    {
                        cachedDesc = cachedDesc.Replace(toRemove.match, "").Trim();
                        // Rewrite cache
                        _steamService.ClearCachedDescription(game.Id);
                        if (!string.IsNullOrEmpty(cachedDesc))
                        {
                            // Re-save the cleaned cache
                            var cacheTask = _steamService.GetRichDescriptionAsync(game);
                            // Force re-cache by clearing and saving
                        }
                    }

                    // Also delete the local file if it exists
                    var gameDir = System.IO.Path.Combine(
                        GetPluginUserDataPath(), Common.Constants.GamesCacheFolder, game.Id.ToString());
                    var filePath = System.IO.Path.Combine(gameDir, toRemove.name);
                    if (System.IO.File.Exists(filePath))
                        try { System.IO.File.Delete(filePath); } catch { }

                    _api.Dialogs.ShowMessage($"Removed: {toRemove.name}", Constants.PluginName);

                    if (_lastSelectedGame?.Id == game.Id)
                        TryInjectAndRender(game);
                }
            });

            items.Add(new GameMenuItem
            {
                MenuSection = Constants.MenuSectionName,
                Description = "Re-download media",
                Action = (menuArgs) =>
                {
                    if (_cacheService == null) return;
                    foreach (var game in menuArgs.Games)
                    {
                        _cacheService.ClearGameCache(game.Id);
                        _steamService?.ClearCachedDescription(game.Id);
                    }
                    if (_renderer?.IsInitialized == true && _lastSelectedGame != null)
                        TryInjectAndRender(_lastSelectedGame);
                    _api.Dialogs.ShowMessage(
                        "Cache cleared. Media will re-download as you browse.",
                        Constants.PluginName);
                }
            });

            // Video scale submenu
            foreach (var pct in new[] { 75, 50, 25 })
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
                _steamService = new SteamDescriptionService(basePath);
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
