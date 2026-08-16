using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayGif.Common;

namespace PlayGif.Views
{
    public partial class DescriptionEditorWindow : Window
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly string _initialHtml;
        private readonly Guid _gameId;
        private readonly string _cacheBasePath;
        private readonly Func<string, Task<string>> _mediaProvider;
        private readonly CoreWebView2Environment _sharedEnvironment;
        private bool _ready;
        private bool _inserting;

        public string ResultHtml { get; private set; }

        // mediaProvider takes "search" | "url" | "file" and returns the HTML tag to
        // insert, or null if cancelled. Supplied by the plugin so the editor reuses
        // the same download, format-detection and caching paths as the menu.
        public DescriptionEditorWindow(string html, Guid gameId, string cacheBasePath, string gameName,
                                       Func<string, Task<string>> mediaProvider = null,
                                       CoreWebView2Environment sharedEnvironment = null)
        {
            InitializeComponent();

            _initialHtml = html ?? "";
            _gameId = gameId;
            _cacheBasePath = cacheBasePath;
            _mediaProvider = mediaProvider;
            _sharedEnvironment = sharedEnvironment;

            Title = $"Edit description — {gameName}";
            StatusText.Text = "Saved to PlayGif's cached description. " +
                              "Refreshing or clearing the cache discards these edits.";

            Loaded += async (s, e) => await InitializeEditorAsync();
        }

        private async Task InitializeEditorAsync()
        {
            try
            {
                // Reuse the renderer's environment. A second environment created
                // with different GPU options shares the same GPU process, and
                // tearing one down could leave WPF's own hardware rendering on a
                // dead device — the window went black while the WebView2 HWND,
                // which composites separately, kept drawing.
                var env = _sharedEnvironment;
                if (env == null)
                {
                    var userData = Path.Combine(_cacheBasePath, "WebView2Data");
                    Directory.CreateDirectory(userData);
                    env = await CoreWebView2Environment.CreateAsync(null, userData);
                }

                await Editor.EnsureCoreWebView2Async(env);

                var core = Editor.CoreWebView2;
                var s = core.Settings;
                s.AreBrowserAcceleratorKeysEnabled = false;
                s.IsStatusBarEnabled = false;
                s.AreDefaultContextMenusEnabled = true;   // cut/copy/paste
                s.IsZoomControlEnabled = false;
                s.IsWebMessageEnabled = true;

                core.WebMessageReceived += OnWebMessageReceived;

                // Same virtual host as the renderer, so cached media resolves and
                // animates inside the editor exactly as it will in the description
                var mediaPath = Path.Combine(_cacheBasePath, Constants.GamesCacheFolder);
                Directory.CreateDirectory(mediaPath);
                core.SetVirtualHostNameToFolderMapping(
                    Constants.VirtualHostName, mediaPath, CoreWebView2HostResourceAccessKind.Allow);

                // Editing should never navigate away
                core.NavigationStarting += (s2, e2) =>
                {
                    if (!e2.Uri.StartsWith("about:") && !e2.Uri.StartsWith("data:") && _ready)
                        e2.Cancel = true;
                };

                core.NavigationCompleted += async (s2, e2) =>
                {
                    if (_ready) return;
                    _ready = true;
                    var escaped = JsonConvert.SerializeObject(_initialHtml);
                    await core.ExecuteScriptAsync($"setContent({escaped})");
                };

                core.NavigateToString(LoadEditorHtml());
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize the description editor");
                MessageBox.Show($"Could not open the editor: {ex.Message}",
                    Constants.PluginName, MessageBoxButton.OK, MessageBoxImage.Error);
                DialogResult = false;
            }
        }

        // The editor asks for media; the plugin fetches it and the finished tag is
        // inserted at the caret the editor saved before the dialog took focus.
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string source;
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;

                var msg = Newtonsoft.Json.Linq.JObject.Parse(raw);
                if (msg["type"]?.ToString() != "insert") return;
                if (_mediaProvider == null) return;

                source = msg["source"]?.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not read the editor message");
                return;
            }

            // Must not open a dialog from inside the WebView2 message callback.
            // Doing so re-enters the WPF dispatcher from the browser's message
            // pump, and the picker spins up a third WebView while this one is
            // live — that combination wedged the compositor and blacked out the
            // main window. Hand off and let the callback return first.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(async () => await InsertMediaAsync(source)));
        }

        private async Task InsertMediaAsync(string source)
        {
            if (_inserting) return;   // ignore repeat clicks while a picker is open
            _inserting = true;
            try
            {
                var tag = await _mediaProvider(source);
                if (string.IsNullOrEmpty(tag)) return;
                if (Editor?.CoreWebView2 == null) return;

                var escaped = JsonConvert.SerializeObject(tag);
                await Editor.CoreWebView2.ExecuteScriptAsync($"insertHtml({escaped})");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Editor media insert failed");
                MessageBox.Show($"Could not insert media: {ex.Message}",
                    Constants.PluginName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _inserting = false;
            }
        }

        private static string LoadEditorHtml()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(Constants.EditorPageResource))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;

            try
            {
                var json = await Editor.CoreWebView2.ExecuteScriptAsync("getContent()");
                // ExecuteScriptAsync returns a JSON-encoded string
                ResultHtml = JsonConvert.DeserializeObject<string>(json) ?? "";
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to read the edited description");
                MessageBox.Show($"Could not read the edited description: {ex.Message}",
                    Constants.PluginName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // Tear the WebView2 down while the window still exists. Disposing it after
        // the window is gone detaches its HWND from a destroyed parent, which can
        // leave the main window's rendering broken.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;

            try
            {
                if (Editor?.CoreWebView2 != null)
                    Editor.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;

                // Detach from the visual tree first so WPF stops compositing it
                if (Editor?.Parent is System.Windows.Controls.Border host)
                    host.Child = null;

                Editor?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Editor teardown failed");
            }
        }
    }
}
