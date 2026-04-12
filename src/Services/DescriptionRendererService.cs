using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayGif.Common;

namespace PlayGif.Services
{
    public class DescriptionRendererService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly PlayGifSettings _settings;
        private readonly Func<string> _cacheBasePathProvider;
        private WebView2 _webView;
        private CoreWebView2Environment _environment;
        private bool _isInitialized;
        private string _pendingContent;

        public WebView2 WebViewControl => _webView;
        public bool IsInitialized => _isInitialized;

        public DescriptionRendererService(PlayGifSettings settings, Func<string> cacheBasePathProvider)
        {
            _settings = settings;
            _cacheBasePathProvider = cacheBasePathProvider;
        }

        public async Task InitializeAsync()
        {
            try
            {
                var userDataFolder = Path.Combine(
                    _cacheBasePathProvider(), "WebView2Data");
                _environment = await CoreWebView2Environment.CreateAsync(
                    null, userDataFolder);

                _webView = new WebView2();
                _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                _webView.Visibility = Visibility.Collapsed;
                _webView.Focusable = false;

                _webView.CoreWebView2InitializationCompleted += OnCoreWebView2Ready;
                await _webView.EnsureCoreWebView2Async(_environment);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize WebView2. Falling back to default renderer.");
                _isInitialized = false;
            }
        }

        private void OnCoreWebView2Ready(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Logger.Error(e.InitializationException,
                    "WebView2 CoreWebView2 initialization failed.");
                return;
            }

            var core = _webView.CoreWebView2;

            // Lockdown
            var coreSettings = core.Settings;
            coreSettings.AreBrowserAcceleratorKeysEnabled = false;
            coreSettings.IsStatusBarEnabled = false;
            coreSettings.AreDefaultContextMenusEnabled = false;
            coreSettings.IsZoomControlEnabled = false;
            coreSettings.AreDevToolsEnabled = _settings.EnableDebugMode;
            coreSettings.IsWebMessageEnabled = true;

            // Map local cache folder to virtual host
            var cachePath = Path.Combine(_cacheBasePathProvider(), Constants.GamesCacheFolder);
            Directory.CreateDirectory(cachePath);
            core.SetVirtualHostNameToFolderMapping(
                Constants.VirtualHostName,
                cachePath,
                CoreWebView2HostResourceAccessKind.Allow);

            // Intercept external navigation — open in system browser
            core.NavigationStarting += OnNavigationStarting;

            // Listen for height reports from JS
            core.WebMessageReceived += OnWebMessageReceived;

            // Load shell page from embedded resource
            var shellHtml = LoadEmbeddedShellHtml();
            core.NavigateToString(shellHtml);

            _isInitialized = true;
            Logger.Info("WebView2 renderer initialized.");

            // If content was queued before init completed, render it now
            if (_pendingContent != null)
            {
                var content = _pendingContent;
                _pendingContent = null;
                _ = SetDescriptionAsync(content);
            }
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Allow initial about:blank and data: navigations
            if (e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:"))
                return;

            // Block all other navigations — open links in system browser
            e.Cancel = true;
            if (e.Uri.StartsWith("http"))
            {
                try { System.Diagnostics.Process.Start(e.Uri); }
                catch { }
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                var msg = JsonConvert.DeserializeAnonymousType(json,
                    new { type = "", value = 0.0 });
                if (msg?.type == "height" && msg.value > 0)
                {
                    _webView.Height = msg.value;
                }
            }
            catch { }
        }

        public async Task SetDescriptionAsync(string html)
        {
            if (!_isInitialized)
            {
                _pendingContent = html;
                return;
            }

            if (string.IsNullOrEmpty(html))
            {
                _webView.Visibility = Visibility.Collapsed;
                return;
            }

            var escaped = JsonConvert.SerializeObject(html);
            await _webView.CoreWebView2.ExecuteScriptAsync(
                $"setContent({escaped})");
            _webView.Visibility = Visibility.Visible;
        }

        public void UpdateTheme(Color textColor, Color linkColor, double fontSize, string fontFamily)
        {
            if (!_isInitialized) return;

            var textHex = $"#{textColor.R:X2}{textColor.G:X2}{textColor.B:X2}";
            var linkHex = $"#{linkColor.R:X2}{linkColor.G:X2}{linkColor.B:X2}";
            var sizeStr = $"{fontSize}px";

            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setTheme('{textHex}', '{linkHex}', '{sizeStr}', '{fontFamily}', 'transparent')");
        }

        public void PauseAll()
        {
            if (!_isInitialized) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync("pauseAll()");
            _webView.CoreWebView2.TrySuspendAsync();
        }

        public void ResumeAll()
        {
            if (!_isInitialized) return;
            _webView.CoreWebView2.Resume();
            _ = _webView.CoreWebView2.ExecuteScriptAsync("resumeAll()");
        }

        public void SetMemoryLevel(bool low)
        {
            if (!_isInitialized) return;
            _webView.CoreWebView2.MemoryUsageTargetLevel = low
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }

        private string LoadEmbeddedShellHtml()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(Constants.ShellPageResource))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        public void Dispose()
        {
            if (_webView != null)
            {
                _webView.CoreWebView2InitializationCompleted -= OnCoreWebView2Ready;
                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }
                _webView.Dispose();
                _webView = null;
            }
            _isInitialized = false;
        }
    }
}
