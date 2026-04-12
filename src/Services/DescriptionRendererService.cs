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
        private bool _isInitializing;
        private bool _shellReady;
        private string _pendingContent;
        private TaskCompletionSource<bool> _initTcs;

        public WebView2 WebViewControl => _webView;
        public bool IsInitialized => _isInitialized;

        public DescriptionRendererService(PlayGifSettings settings, Func<string> cacheBasePathProvider)
        {
            _settings = settings;
            _cacheBasePathProvider = cacheBasePathProvider;
        }

        // Create the environment early (can be done before visual tree attachment)
        public async Task PrepareEnvironmentAsync()
        {
            try
            {
                var userDataFolder = Path.Combine(_cacheBasePathProvider(), "WebView2Data");
                Directory.CreateDirectory(userDataFolder);
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-threaded-scrolling");
                _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                Logger.Info("WebView2 environment created.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to create WebView2 environment.");
            }
        }

        // Create the WebView2 control (call on UI thread)
        public WebView2 CreateWebViewControl()
        {
            if (_webView != null) return _webView;

            _webView = new WebView2();
            _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            _webView.Visibility = Visibility.Collapsed;
            _webView.Focusable = false;

            return _webView;
        }

        // Initialize after the control is in the visual tree
        public async Task InitializeAsync()
        {
            if (_isInitialized || _isInitializing) return;
            if (_environment == null)
            {
                Logger.Error("Cannot initialize — environment not prepared.");
                return;
            }
            if (_webView == null)
            {
                Logger.Error("Cannot initialize — WebView2 control not created.");
                return;
            }

            _isInitializing = true;
            _initTcs = new TaskCompletionSource<bool>();

            try
            {
                Logger.Info("Starting WebView2 core initialization...");
                _webView.CoreWebView2InitializationCompleted += OnCoreWebView2Ready;
                await _webView.EnsureCoreWebView2Async(_environment);

                // Wait for OnCoreWebView2Ready to complete setup
                await _initTcs.Task;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize WebView2.");
                _isInitialized = false;
                _isInitializing = false;
            }
        }

        private void OnCoreWebView2Ready(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                Logger.Error(e.InitializationException, "WebView2 CoreWebView2 initialization failed.");
                _initTcs?.TrySetResult(false);
                return;
            }

            var core = _webView.CoreWebView2;

            // Lockdown
            var s = core.Settings;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreDevToolsEnabled = _settings.EnableDebugMode;
            s.IsWebMessageEnabled = true;

            // Map local cache folder to virtual host
            var cachePath = Path.Combine(_cacheBasePathProvider(), Constants.GamesCacheFolder);
            Directory.CreateDirectory(cachePath);
            core.SetVirtualHostNameToFolderMapping(
                Constants.VirtualHostName, cachePath, CoreWebView2HostResourceAccessKind.Allow);

            core.NavigationStarting += OnNavigationStarting;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += OnShellNavigationCompleted;

            // Load shell page
            var shellHtml = LoadEmbeddedShellHtml();
            Logger.Info($"Loading shell page ({shellHtml.Length} chars)...");
            core.NavigateToString(shellHtml);

            _isInitialized = true;
            _isInitializing = false;
            Logger.Info("WebView2 core initialized. Waiting for shell page navigation...");
        }

        private void OnShellNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView.CoreWebView2.NavigationCompleted -= OnShellNavigationCompleted;

            if (e.IsSuccess)
            {
                _shellReady = true;
                Logger.Info("Shell page loaded. Ready to render content.");

                _initTcs?.TrySetResult(true);

                // Render queued content
                if (_pendingContent != null)
                {
                    var content = _pendingContent;
                    _pendingContent = null;
                    _ = SetDescriptionAsync(content);
                }
            }
            else
            {
                Logger.Error($"Shell page navigation failed: {e.WebErrorStatus}");
                _initTcs?.TrySetResult(false);
            }
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // Allow the initial shell page load
            if (!_shellReady) return;

            if (e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:"))
                return;

            e.Cancel = true;
            if (e.Uri.StartsWith("http"))
            {
                try { System.Diagnostics.Process.Start(e.Uri); }
                catch { }
            }
        }

        public event Action<double> HeightReported;
        public event Action<double> ScrollOverflow;

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;

                // Fast path for scroll overflow (S{delta})
                if (raw[0] == 'S')
                {
                    if (double.TryParse(raw.Substring(1),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var delta))
                        ScrollOverflow?.Invoke(delta);
                    return;
                }

                var msg = Newtonsoft.Json.Linq.JObject.Parse(raw);
                var type = msg["type"]?.ToString();
                if (type == "height")
                {
                    var height = msg["value"]?.ToObject<double>() ?? 0;
                    if (height > 0)
                        HeightReported?.Invoke(height);
                }
            }
            catch { }
        }

        public async Task SetDescriptionAsync(string html)
        {
            if (!_isInitialized || !_shellReady)
            {
                Logger.Info($"Queuing content (initialized={_isInitialized}, shellReady={_shellReady})");
                _pendingContent = html;
                return;
            }

            if (string.IsNullOrEmpty(html))
            {
                _webView.Visibility = Visibility.Collapsed;
                return;
            }

            var escaped = JsonConvert.SerializeObject(html);
            await _webView.CoreWebView2.ExecuteScriptAsync($"setContent({escaped})");
            _webView.Visibility = Visibility.Visible;
        }

        public void UpdateTheme(Color textColor, Color linkColor, double fontSize, string fontFamily)
        {
            if (!_isInitialized) return;
            var textHex = $"#{textColor.R:X2}{textColor.G:X2}{textColor.B:X2}";
            var linkHex = $"#{linkColor.R:X2}{linkColor.G:X2}{linkColor.B:X2}";
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setTheme('{textHex}', '{linkHex}', '{fontSize}px', '{fontFamily}', 'transparent')");
        }

        public void PauseAll()
        {
            if (!_isInitialized) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync("pauseAll()");
        }

        public void ResumeAll()
        {
            if (!_isInitialized) return;
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
