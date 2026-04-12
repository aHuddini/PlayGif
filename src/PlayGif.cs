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

                        // Fullscreen: set to content height so ScrollViewerEx scrolls it
                        if (_api.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                        {
                            _renderer.HeightReported += (height) =>
                            {
                                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    _renderer.WebViewControl.Height = height;
                                }));
                            };
                        }

                        // Forward scroll at top/bottom boundary to parent
                        _renderer.ScrollOverflow += (delta) =>
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                var sv = _viewMonitor.ParentScrollViewer;
                                if (sv != null)
                                    sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
                            }));
                        };


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
                var hasMedia = !string.IsNullOrEmpty(html) &&
                    (html.Contains("<video") || html.Contains(".webm") || html.Contains(".mp4"));

                // If the stored description lacks media, fetch the rich version from Steam
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

                if (string.IsNullOrEmpty(html))
                {
                    Logger.Info($"No description for: {game.Name}");
                    _ = _renderer.SetDescriptionAsync(null);
                    return;
                }

                Logger.Info($"Rendering: {game.Name} ({html.Length} chars)");

                html = _cacheService.RewriteDescriptionHtml(html, game.Id);
                _ = _renderer.SetDescriptionAsync(html);
                UpdateThemeColors();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Error in TryInjectAndRender for: {game?.Name}");
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
