using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private PlayGifSettings _settings;
        private PlayGifSettingsViewModel _settingsViewModel;

        private DescriptionRendererService _renderer;
        private MediaCacheService _cacheService;
        private DescriptionViewMonitor _viewMonitor;
        private bool _customElementActive;

        public override Guid Id { get; } = Guid.Parse("2e196d25-24d1-4db3-b732-9766c994a496");

        public PlayGif(IPlayniteAPI api) : base(api)
        {
            _api = api;
            Properties = new GenericPluginProperties { HasSettings = true };

            _settings = new PlayGifSettings(this);
            _settingsViewModel = new PlayGifSettingsViewModel(this);

            // Register custom element for theme integration
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

                _cacheService = new MediaCacheService(_settings, basePath);

                _renderer = new DescriptionRendererService(_settings, () => basePath);
                await _renderer.InitializeAsync();

                if (!_renderer.IsInitialized)
                {
                    Logger.Error("WebView2 renderer failed to initialize. Plugin will be inactive.");
                    return;
                }

                // Start visual tree monitor as fallback injection
                _viewMonitor = new DescriptionViewMonitor(
                    () => _renderer.WebViewControl,
                    () => _settings.EnableAnimatedDescriptions);
                _viewMonitor.StartMonitoring();

                // Subscribe to window activation for resource management
                Application.Current.MainWindow.Activated += OnWindowActivated;
                Application.Current.MainWindow.Deactivated += OnWindowDeactivated;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize PlayGif services.");
            }
        }

        public override Control GetGameViewControl(GetGameViewControlArgs args)
        {
            if (args.Name == Constants.CustomElementName
                && _renderer?.IsInitialized == true
                && _settings.EnableAnimatedDescriptions)
            {
                _customElementActive = true;
                return new ContentControl { Content = _renderer.WebViewControl };
            }
            return null;
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            if (_renderer == null || !_renderer.IsInitialized) return;
            if (!_settings.EnableAnimatedDescriptions) return;

            var games = args.NewValue;
            if (games == null || games.Count == 0) return;

            // Single game selection — show its description
            if (games.Count == 1)
            {
                var game = games[0];
                UpdateDescription(game);
            }
            else
            {
                // Multi-selection — hide the renderer
                _ = _renderer.SetDescriptionAsync(null);
            }
        }

        private void UpdateDescription(Game game)
        {
            var html = game.Description;
            if (string.IsNullOrEmpty(html))
            {
                _ = _renderer.SetDescriptionAsync(null);
                return;
            }

            // Rewrite remote URLs to local cache
            html = _cacheService.RewriteDescriptionHtml(html, game.Id);

            _ = _renderer.SetDescriptionAsync(html);

            // Sync theme colors
            UpdateThemeColors();

            // Try visual tree injection if custom element wasn't used
            if (!_customElementActive && !_viewMonitor.IsInjected)
            {
                _viewMonitor.TryInject(Application.Current.MainWindow, _renderer.WebViewControl);
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
                    foreach (var game in menuArgs.Games)
                    {
                        _cacheService?.ClearGameCache(game.Id);
                    }
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
                    foreach (var game in menuArgs.Games)
                    {
                        _cacheService?.ClearGameCache(game.Id);
                    }
                    // Re-trigger description rendering for the currently selected game
                    var selected = _api.MainView.SelectedGames?.FirstOrDefault();
                    if (selected != null)
                    {
                        UpdateDescription(selected);
                    }
                    _api.Dialogs.ShowMessage(
                        "Cache cleared. Media will re-download as you browse.",
                        Constants.PluginName);
                }
            });

            return items;
        }

        #endregion

        #region Settings

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return _settings;
        }

        public override UserControl GetSettingsView(bool firstRunView)
        {
            return new PlayGifSettingsView();
        }

        internal PlayGifSettings Settings => _settings;

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
