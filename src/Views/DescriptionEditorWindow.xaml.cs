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
        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;

                var msg = Newtonsoft.Json.Linq.JObject.Parse(raw);
                if (msg["type"]?.ToString() != "insert") return;
                if (_mediaProvider == null) return;

                var source = msg["source"]?.ToString();
                var tag = await _mediaProvider(source);
                if (string.IsNullOrEmpty(tag)) return;

                var escaped = JsonConvert.SerializeObject(tag);
                await Editor.CoreWebView2.ExecuteScriptAsync($"insertHtml({escaped})");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Editor media insert failed");
                MessageBox.Show($"Could not insert media: {ex.Message}",
                    Constants.PluginName, MessageBoxButton.OK, MessageBoxImage.Error);
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

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                if (Editor?.CoreWebView2 != null)
                    Editor.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;

                // Dispose after the window has finished closing. Tearing the
                // control down mid-close can drop the shared GPU device while WPF
                // is still compositing this frame, which blanks the main window.
                var control = Editor;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { try { control?.Dispose(); } catch { } }));
            }
            catch { }

            base.OnClosed(e);
        }
    }
}
