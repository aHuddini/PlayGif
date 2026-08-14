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
        private bool _ready;

        public string ResultHtml { get; private set; }

        // mediaProvider takes "search" | "url" | "file" and returns the HTML tag to
        // insert, or null if cancelled. Supplied by the plugin so the editor reuses
        // the same download, format-detection and caching paths as the menu.
        public DescriptionEditorWindow(string html, Guid gameId, string cacheBasePath, string gameName,
                                       Func<string, Task<string>> mediaProvider = null)
        {
            InitializeComponent();

            _initialHtml = html ?? "";
            _gameId = gameId;
            _cacheBasePath = cacheBasePath;
            _mediaProvider = mediaProvider;

            Title = $"Edit description — {gameName}";
            StatusText.Text = "Saved to PlayGif's cached description. " +
                              "Refreshing or clearing the cache discards these edits.";

            Loaded += async (s, e) => await InitializeEditorAsync();
        }

        private async Task InitializeEditorAsync()
        {
            try
            {
                // Its own user data folder so it cannot disturb the renderer's
                var userData = Path.Combine(_cacheBasePath, "EditorWebView2");
                Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData);
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
            try { Editor?.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}
