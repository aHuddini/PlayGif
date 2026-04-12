# PlayGif Animated Description Renderer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Playnite's static HtmlTextView with a WebView2-based renderer that displays animated GIFs, WebM, MP4, and animated WebP/AVIF inline in game descriptions.

**Architecture:** Hybrid injection (custom element primary, visual tree fallback) with a single persistent WebView2 instance. Content updates via JavaScript — no page navigation on game switch. Media caching downloads remote assets to local folders for offline support.

**Tech Stack:** .NET 4.6.2, WPF, Microsoft.Web.WebView2, HtmlAgilityPack, Playnite SDK 6.16.0

**Spec:** `docs/dev_docs/2026-04-11-animated-description-renderer-design.md`

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `src/PlayGif.csproj` | Modify | Add WebView2 + HtmlAgilityPack NuGet refs |
| `src/Common/Constants.cs` | Modify | Add cache paths, virtual host name, supported media extensions |
| `src/PlayGifSettings.cs` | Modify | Replace `EnableGifs` with spec settings |
| `src/PlayGifSettingsView.xaml` | Modify | Settings UI for new properties |
| `src/PlayGifSettingsViewModel.cs` | Modify | Add cache size display |
| `src/PlayGif.cs` | Modify | Wire services, custom elements, lifecycle events, menu items |
| `src/Services/DescriptionRendererService.cs` | Create | WebView2 lifecycle, content updates, theme sync |
| `src/Services/MediaCacheService.cs` | Create | Download, cache, URL rewriting |
| `src/Monitors/DescriptionViewMonitor.cs` | Create | Visual tree walking, HtmlTextView replacement |
| `src/Resources/shell.html` | Create | Minimal HTML shell page for WebView2 |
| `scripts/package_extension.ps1` | Modify | Include WebView2 DLLs + runtimes/ folder |

---

## Task 1: Add NuGet Dependencies and Fix Packaging

**Files:**
- Modify: `src/PlayGif.csproj`
- Modify: `scripts/package_extension.ps1`

- [ ] **Step 1: Add WebView2 and HtmlAgilityPack to csproj**

Replace the `<ItemGroup>` containing PackageReferences in `src/PlayGif.csproj`:

```xml
    <ItemGroup>
        <PackageReference Include="PlayniteSDK" Version="6.16.0.0" PrivateAssets="none" />
        <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
        <PackageReference Include="Microsoft.Web.WebView2" Version="1.0.3124.44" />
        <PackageReference Include="HtmlAgilityPack" Version="1.11.72" />
    </ItemGroup>
```

- [ ] **Step 2: Update packaging script to handle WebView2 native DLLs**

The WebView2 NuGet outputs `runtimes/win-{arch}/native/WebView2Loader.dll` subdirectories. The current script only copies flat DLLs and excludes `Microsoft.*.dll`. Fix both issues.

Replace the dependency copy section in `scripts/package_extension.ps1` (the `$excludedDlls` array and the loop after it) with:

```powershell
# Copy dependencies from build output (exclude SDK and system DLLs)
$excludedDlls = @(
    "Playnite.SDK.dll",
    "PlayGif.dll"
)
$systemPrefixes = @("System.", "Microsoft.CSharp.", "Microsoft.VisualBasic.")
$buildOutput = Join-Path $projectRoot "src\bin\$Configuration\net4.6.2"
foreach ($dll in (Get-ChildItem "$buildOutput\*.dll")) {
    $excluded = $false
    foreach ($pattern in $excludedDlls) {
        if ($dll.Name -eq $pattern) {
            $excluded = $true
            break
        }
    }
    if (-not $excluded) {
        foreach ($prefix in $systemPrefixes) {
            if ($dll.Name.StartsWith($prefix)) {
                $excluded = $true
                break
            }
        }
    }
    if (-not $excluded) {
        Copy-Item $dll.FullName -Destination $packageDir -Force
        Write-Host "  Copied: $($dll.Name)" -ForegroundColor Gray
    }
}

# Copy runtimes directory (WebView2 native loaders)
$runtimesDir = Join-Path $buildOutput "runtimes"
if (Test-Path $runtimesDir) {
    Copy-Item $runtimesDir -Destination $packageDir -Recurse -Force
    Write-Host "  Copied: runtimes/ (WebView2 native DLLs)" -ForegroundColor Gray
}
```

- [ ] **Step 3: Build and verify dependencies resolve**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds. `src/bin/Release/net4.6.2/` contains `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll`, `HtmlAgilityPack.dll`, and `runtimes/` folder.

- [ ] **Step 4: Package and verify output**

Run:
```bash
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```
Expected: Package contains `Microsoft.Web.WebView2.*.dll`, `HtmlAgilityPack.dll`, and `runtimes/` subdirectory with `WebView2Loader.dll` for each architecture.

- [ ] **Step 5: Commit**

```bash
git add src/PlayGif.csproj scripts/package_extension.ps1
git commit -m "Add WebView2 and HtmlAgilityPack dependencies, fix packaging"
```

---

## Task 2: Update Constants and Settings

**Files:**
- Modify: `src/Common/Constants.cs`
- Modify: `src/PlayGifSettings.cs`
- Modify: `src/PlayGifSettingsView.xaml`
- Modify: `src/PlayGifSettingsViewModel.cs`

- [ ] **Step 1: Update Constants.cs**

Replace the entire contents of `src/Common/Constants.cs`:

```csharp
namespace PlayGif.Common
{
    public static class Constants
    {
        #region Plugin Info

        public const string PluginName = "PlayGif";
        public const string MenuSectionName = "PlayGif";
        public const string CustomElementSource = "PlayGif";
        public const string CustomElementName = "AnimatedDescription";

        #endregion

        #region WebView2

        public const string VirtualHostName = "playgif.local";
        public const string ShellPageResource = "PlayGif.Resources.shell.html";

        #endregion

        #region Cache

        public const string GamesCacheFolder = "Games";
        public const int DefaultMaxCachePerGameMB = 100;

        #endregion

        #region File Extensions

        public static readonly string[] SupportedMediaExtensions =
            { ".gif", ".webp", ".apng", ".avif", ".webm", ".mp4" };

        #endregion

        #region Visual Tree

        public const string HtmlDescriptionPartName = "PART_HtmlDescription";
        public const string DescriptionPanelPartName = "PART_ElemDescription";
        public const string FullscreenScrollPartName = "PART_ScrollHtmlDescription";

        #endregion
    }
}
```

- [ ] **Step 2: Update PlayGifSettings.cs**

Replace the entire contents of `src/PlayGifSettings.cs`:

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using Playnite.SDK;

namespace PlayGif
{
    public class PlayGifSettings : ISettings, INotifyPropertyChanged
    {
        private readonly PlayGif _plugin;

        public event PropertyChangedEventHandler PropertyChanged;

        // Serialization constructor
        public PlayGifSettings() { }

        public PlayGifSettings(PlayGif plugin)
        {
            _plugin = plugin;
            var saved = plugin.LoadPluginSettings<PlayGifSettings>();
            if (saved != null)
            {
                EnableAnimatedDescriptions = saved.EnableAnimatedDescriptions;
                AutoCacheMedia = saved.AutoCacheMedia;
                MaxCachePerGameMB = saved.MaxCachePerGameMB;
                EnableDebugMode = saved.EnableDebugMode;
            }
        }

        private bool enableAnimatedDescriptions = true;
        public bool EnableAnimatedDescriptions
        {
            get => enableAnimatedDescriptions;
            set { enableAnimatedDescriptions = value; OnPropertyChanged(nameof(EnableAnimatedDescriptions)); }
        }

        private bool autoCacheMedia = true;
        public bool AutoCacheMedia
        {
            get => autoCacheMedia;
            set { autoCacheMedia = value; OnPropertyChanged(nameof(AutoCacheMedia)); }
        }

        private int maxCachePerGameMB = Common.Constants.DefaultMaxCachePerGameMB;
        public int MaxCachePerGameMB
        {
            get => maxCachePerGameMB;
            set { maxCachePerGameMB = value; OnPropertyChanged(nameof(MaxCachePerGameMB)); }
        }

        private bool enableDebugMode = false;
        public bool EnableDebugMode
        {
            get => enableDebugMode;
            set { enableDebugMode = value; OnPropertyChanged(nameof(EnableDebugMode)); }
        }

        protected void OnPropertyChanged(string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ISettings implementation
        public void BeginEdit() { }
        public void CancelEdit() { }
        public void EndEdit()
        {
            _plugin.SavePluginSettings(this);
        }
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}
```

- [ ] **Step 3: Update PlayGifSettingsView.xaml**

Replace the entire contents of `src/PlayGifSettingsView.xaml`:

```xml
<UserControl x:Class="PlayGif.PlayGifSettingsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel Margin="20">
        <TextBlock Text="PlayGif Settings" FontSize="18" FontWeight="Bold" Margin="0,0,0,15"/>

        <CheckBox Content="Enable animated descriptions"
                  IsChecked="{Binding Settings.EnableAnimatedDescriptions}"
                  Margin="0,5,0,5"/>

        <CheckBox Content="Auto-cache media for offline use"
                  IsChecked="{Binding Settings.AutoCacheMedia}"
                  Margin="0,5,0,5"
                  IsEnabled="{Binding Settings.EnableAnimatedDescriptions}"/>

        <StackPanel Orientation="Horizontal" Margin="0,10,0,5">
            <TextBlock Text="Max cache per game (MB): " VerticalAlignment="Center"/>
            <TextBox Text="{Binding Settings.MaxCachePerGameMB, UpdateSourceTrigger=PropertyChanged}"
                     Width="60" VerticalAlignment="Center"
                     IsEnabled="{Binding Settings.AutoCacheMedia}"/>
        </StackPanel>

        <Separator Margin="0,15,0,15"/>

        <CheckBox Content="Enable debug mode (WebView2 DevTools)"
                  IsChecked="{Binding Settings.EnableDebugMode}"
                  Margin="0,5,0,5"/>
    </StackPanel>
</UserControl>
```

- [ ] **Step 4: Build and verify**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add src/Common/Constants.cs src/PlayGifSettings.cs src/PlayGifSettingsView.xaml src/PlayGifSettingsViewModel.cs
git commit -m "Update settings and constants for animated description renderer"
```

---

## Task 3: Create HTML Shell Page

**Files:**
- Create: `src/Resources/shell.html`
- Modify: `src/PlayGif.csproj` (add EmbeddedResource)

- [ ] **Step 1: Create the Resources directory and shell.html**

Create `src/Resources/shell.html`:

```html
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<style>
    :root {
        --text-color: #ffffff;
        --link-color: #0078d4;
        --font-size: 14px;
        --font-family: Segoe UI, sans-serif;
        --bg-color: transparent;
    }
    html, body {
        margin: 0;
        padding: 0;
        background: var(--bg-color);
        color: var(--text-color);
        font-family: var(--font-family);
        font-size: var(--font-size);
        overflow: hidden;
    }
    #content {
        padding: 0;
    }
    #content img {
        max-width: 100%;
        height: auto;
        display: block;
    }
    #content video {
        max-width: 100%;
        height: auto;
        display: block;
    }
    #content a {
        color: var(--link-color);
        text-decoration: none;
    }
    #content a:hover {
        text-decoration: underline;
    }
    /* Hide scrollbar but allow content measurement */
    ::-webkit-scrollbar {
        display: none;
    }
</style>
</head>
<body>
<div id="content"></div>
<script>
    function setContent(html) {
        var el = document.getElementById('content');
        el.innerHTML = html;
        // Ensure all videos are muted, looping, autoplay
        var videos = el.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) {
            videos[i].muted = true;
            videos[i].loop = true;
            videos[i].autoplay = true;
            videos[i].playsInline = true;
            videos[i].play().catch(function(){});
        }
        reportHeight();
    }

    function setTheme(textColor, linkColor, fontSize, fontFamily, bgColor) {
        var root = document.documentElement.style;
        root.setProperty('--text-color', textColor);
        root.setProperty('--link-color', linkColor);
        root.setProperty('--font-size', fontSize);
        root.setProperty('--font-family', fontFamily);
        root.setProperty('--bg-color', bgColor || 'transparent');
    }

    function pauseAll() {
        var videos = document.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) {
            videos[i].pause();
        }
    }

    function resumeAll() {
        var videos = document.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) {
            videos[i].play().catch(function(){});
        }
    }

    function reportHeight() {
        var height = document.getElementById('content').scrollHeight;
        window.chrome.webview.postMessage(JSON.stringify({ type: 'height', value: height }));
    }

    // Report height after images/videos load
    new MutationObserver(function() {
        setTimeout(reportHeight, 100);
    }).observe(document.getElementById('content'), { childList: true, subtree: true });

    window.addEventListener('load', reportHeight);
    window.addEventListener('resize', reportHeight);
</script>
</body>
</html>
```

- [ ] **Step 2: Add shell.html as EmbeddedResource in csproj**

Add this `<ItemGroup>` to `src/PlayGif.csproj` after the existing `<ItemGroup>` blocks:

```xml
    <ItemGroup>
        <EmbeddedResource Include="Resources\shell.html" />
    </ItemGroup>
```

- [ ] **Step 3: Build and verify resource is embedded**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds. The shell.html is embedded in the assembly (no file copy to output).

- [ ] **Step 4: Commit**

```bash
git add src/Resources/shell.html src/PlayGif.csproj
git commit -m "Add WebView2 HTML shell page as embedded resource"
```

---

## Task 4: Create DescriptionRendererService

**Files:**
- Create: `src/Services/DescriptionRendererService.cs`

This is the core service: manages the WebView2 control lifecycle, content updates, theme sync, and resource management.

- [ ] **Step 1: Create DescriptionRendererService.cs**

Create `src/Services/DescriptionRendererService.cs`:

```csharp
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
                _webView.IsTabStop = false;
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
```

- [ ] **Step 2: Build and verify**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add src/Services/DescriptionRendererService.cs
git commit -m "Add DescriptionRendererService for WebView2 lifecycle and content management"
```

---

## Task 5: Create DescriptionViewMonitor

**Files:**
- Create: `src/Monitors/DescriptionViewMonitor.cs`

This is the visual tree walking fallback that finds `PART_HtmlDescription` and injects the WebView2 control.

- [ ] **Step 1: Create DescriptionViewMonitor.cs**

Create `src/Monitors/DescriptionViewMonitor.cs`:

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;
using PlayGif.Common;

namespace PlayGif.Monitors
{
    public class DescriptionViewMonitor
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly Func<WebView2> _webViewProvider;
        private readonly Func<bool> _isEnabled;
        private bool _isHooked;
        private FrameworkElement _hiddenHtmlTextView;
        private Panel _injectedParent;

        public bool IsInjected => _hiddenHtmlTextView != null;

        public DescriptionViewMonitor(Func<WebView2> webViewProvider, Func<bool> isEnabled)
        {
            _webViewProvider = webViewProvider;
            _isEnabled = isEnabled;
        }

        public void StartMonitoring()
        {
            if (_isHooked) return;
            EventManager.RegisterClassHandler(typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
            _isHooked = true;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Window window)) return;
            if (!_isEnabled()) return;

            var webView = _webViewProvider();
            if (webView == null) return;

            // Already injected somewhere
            if (webView.Parent != null) return;

            TryInject(window, webView);
        }

        public void TryInject(DependencyObject root, WebView2 webView)
        {
            if (webView.Parent != null) return;

            var htmlTextView = FindChildByName(root, Constants.HtmlDescriptionPartName);
            if (htmlTextView == null) return;

            var parent = VisualTreeHelper.GetParent(htmlTextView) as Panel;
            if (parent == null) return;

            // Hide the original HtmlTextView
            htmlTextView.Visibility = Visibility.Collapsed;
            _hiddenHtmlTextView = htmlTextView;
            _injectedParent = parent;

            // Insert WebView2 at the same position
            int index = parent.Children.IndexOf(htmlTextView);
            if (index < 0) index = parent.Children.Count;
            parent.Children.Insert(index + 1, webView);

            Logger.Info("Injected WebView2 renderer via visual tree fallback.");
        }

        public void Restore()
        {
            var webView = _webViewProvider();
            if (_injectedParent != null && webView != null)
            {
                _injectedParent.Children.Remove(webView);
            }

            if (_hiddenHtmlTextView != null)
            {
                _hiddenHtmlTextView.Visibility = Visibility.Visible;
                _hiddenHtmlTextView = null;
            }

            _injectedParent = null;
        }

        public string ReadCurrentDescription()
        {
            if (_hiddenHtmlTextView == null) return null;

            // Read the HtmlText dependency property from HtmlTextView
            // HtmlTextView is a Playnite type; we access via reflection
            var prop = _hiddenHtmlTextView.GetType().GetProperty("HtmlText");
            if (prop != null)
            {
                return prop.GetValue(_hiddenHtmlTextView) as string;
            }

            // Fallback: try the dependency property directly
            var dp = FindDependencyProperty(_hiddenHtmlTextView.GetType(), "HtmlTextProperty");
            if (dp != null)
            {
                return _hiddenHtmlTextView.GetValue(dp) as string;
            }

            return null;
        }

        private static DependencyProperty FindDependencyProperty(Type type, string fieldName)
        {
            var field = type.GetField(fieldName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.FlattenHierarchy);
            return field?.GetValue(null) as DependencyProperty;
        }

        private static FrameworkElement FindChildByName(DependencyObject parent, string name)
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name)
                    return fe;

                var found = FindChildByName(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Monitors/DescriptionViewMonitor.cs
git commit -m "Add DescriptionViewMonitor for visual tree injection fallback"
```

---

## Task 6: Create MediaCacheService

**Files:**
- Create: `src/Services/MediaCacheService.cs`

Handles downloading remote media assets, caching them locally, and rewriting HTML URLs to point to the local cache.

- [ ] **Step 1: Create MediaCacheService.cs**

Create `src/Services/MediaCacheService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Playnite.SDK;
using PlayGif.Common;

namespace PlayGif.Services
{
    public class MediaCacheService
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly PlayGifSettings _settings;
        private readonly string _cacheBasePath;

        public MediaCacheService(PlayGifSettings settings, string cacheBasePath)
        {
            _settings = settings;
            _cacheBasePath = Path.Combine(cacheBasePath, Constants.GamesCacheFolder);
            Directory.CreateDirectory(_cacheBasePath);
        }

        // Rewrites remote media URLs to local cache paths, queues downloads for uncached items
        public string RewriteDescriptionHtml(string html, Guid gameId)
        {
            if (string.IsNullOrEmpty(html)) return html;
            if (!_settings.AutoCacheMedia) return html;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var gameDir = GetGameCacheDir(gameId);
            var mediaUrls = new List<(HtmlNode node, string attrName, string url)>();

            // Find img sources
            foreach (var img in doc.DocumentNode.SelectNodes("//img[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = img.GetAttributeValue("src", "");
                if (IsRemoteUrl(src))
                    mediaUrls.Add((img, "src", src));
            }

            // Find video/source sources
            foreach (var source in doc.DocumentNode.SelectNodes("//source[@src]") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = source.GetAttributeValue("src", "");
                if (IsRemoteUrl(src))
                    mediaUrls.Add((source, "src", src));
            }

            // Find video poster attributes
            foreach (var video in doc.DocumentNode.SelectNodes("//video[@poster]") ?? Enumerable.Empty<HtmlNode>())
            {
                var poster = video.GetAttributeValue("poster", "");
                if (IsRemoteUrl(poster))
                    mediaUrls.Add((video, "poster", poster));
            }

            // Rewrite cached URLs, queue downloads for uncached
            var toDownload = new List<(string url, string localPath)>();

            foreach (var (node, attrName, url) in mediaUrls)
            {
                var filename = GetCacheFilename(url);
                var localPath = Path.Combine(gameDir, filename);

                if (File.Exists(localPath))
                {
                    var virtualUrl = $"https://{Constants.VirtualHostName}/{gameId}/{filename}";
                    node.SetAttributeValue(attrName, virtualUrl);
                }
                else
                {
                    toDownload.Add((url, localPath));
                }
            }

            // Fire and forget background downloads
            if (toDownload.Count > 0)
            {
                _ = DownloadAllAsync(toDownload, gameId);
            }

            return doc.DocumentNode.OuterHtml;
        }

        private async Task DownloadAllAsync(List<(string url, string localPath)> items, Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            Directory.CreateDirectory(gameDir);

            foreach (var (url, localPath) in items)
            {
                try
                {
                    if (GetGameCacheSize(gameId) > _settings.MaxCachePerGameMB * 1024L * 1024L)
                    {
                        Logger.Info($"Cache limit reached for game {gameId}, skipping remaining downloads.");
                        break;
                    }

                    var response = await HttpClient.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    File.WriteAllBytes(localPath, bytes);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"Failed to cache media: {url}");
                }
            }
        }

        public void ClearGameCache(Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            if (Directory.Exists(gameDir))
            {
                Directory.Delete(gameDir, true);
            }
        }

        public long GetGameCacheSize(Guid gameId)
        {
            var gameDir = GetGameCacheDir(gameId);
            if (!Directory.Exists(gameDir)) return 0;
            return new DirectoryInfo(gameDir)
                .GetFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }

        private string GetGameCacheDir(Guid gameId)
        {
            return Path.Combine(_cacheBasePath, gameId.ToString());
        }

        private static string GetCacheFilename(string url)
        {
            // Strip query parameters for the extension
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";

            // Hash the full URL for uniqueness
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                var hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToLowerInvariant();
                return hex + ext;
            }
        }

        private static bool IsRemoteUrl(string url)
        {
            return !string.IsNullOrEmpty(url)
                && (url.StartsWith("http://") || url.StartsWith("https://"))
                && !url.Contains(Constants.VirtualHostName);
        }
    }
}
```

- [ ] **Step 2: Build and verify**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Services/MediaCacheService.cs
git commit -m "Add MediaCacheService for downloading and caching description media"
```

---

## Task 7: Wire Everything Together in PlayGif.cs

**Files:**
- Modify: `src/PlayGif.cs`

This is the integration task — wire all services, register custom elements, handle lifecycle events, and implement game menu items.

- [ ] **Step 1: Replace PlayGif.cs with full implementation**

Replace the entire contents of `src/PlayGif.cs`:

```csharp
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
```

- [ ] **Step 2: Build and verify**

Run:
```bash
dotnet clean -c Release && dotnet build -c Release
```
Expected: Build succeeds with no errors or warnings.

- [ ] **Step 3: Package**

Run:
```bash
powershell -ExecutionPolicy Bypass -File scripts/package_extension.ps1
```
Expected: Package creates successfully with all WebView2 DLLs, HtmlAgilityPack.dll, runtimes/ folder, and PlayGif.dll.

- [ ] **Step 4: Commit**

```bash
git add src/PlayGif.cs
git commit -m "Wire all services, lifecycle events, and menu items in plugin entry point"
```

---

## Task 8: Manual Integration Test with Deep Rock Galactic

**No files changed** — this is a verification task using the built package.

- [ ] **Step 1: Install the extension in Playnite**

1. Locate the `.pext` file in the `pext/` folder
2. Open Playnite → Add-ons → Install from file → select the `.pext` file
3. Restart Playnite

- [ ] **Step 2: Verify animated description renders**

1. Navigate to Deep Rock Galactic in the library
2. Open the detail view (description tab)
3. Confirm: animated content (videos, GIFs) plays inline in the description area
4. Confirm: videos are muted and loop
5. Confirm: text styling matches the current Playnite theme

- [ ] **Step 3: Verify scrolling**

1. Scroll through the description in desktop mode
2. Confirm smooth scrolling — no stuck areas, no double-scroll

- [ ] **Step 4: Verify resource management**

1. Minimize Playnite → videos should pause
2. Restore Playnite → videos should resume
3. Launch a game → videos should pause
4. Exit the game → videos should resume

- [ ] **Step 5: Verify cache**

1. Open the plugin data folder (`%AppData%/Playnite/ExtensionsData/{pluginId}/Games/`)
2. Confirm game folder exists with cached media files
3. Right-click game → PlayGif → Clear cached media
4. Confirm folder is cleared

- [ ] **Step 6: Verify disable/rollback**

1. Go to Settings → PlayGif → uncheck "Enable animated descriptions"
2. Confirm the original HtmlTextView reappears with static content
3. Re-enable → animated content returns

- [ ] **Step 7: Document any issues found**

Note any issues for follow-up tasks. Common things to watch for:
- Theme color mismatch (CSS variable mapping)
- Height calculation issues (content clipping or excessive whitespace)
- WebView2 focus stealing (keyboard input captured by WebView2 instead of Playnite)
- Scroll synchronization problems

---

## Implementation Order Summary

| Task | Component | Dependencies |
|------|-----------|-------------|
| 1 | NuGet deps + packaging | None |
| 2 | Constants + Settings | None |
| 3 | HTML shell page | None |
| 4 | DescriptionRendererService | Tasks 1, 3 |
| 5 | DescriptionViewMonitor | Task 1 |
| 6 | MediaCacheService | Task 1, 2 |
| 7 | PlayGif.cs integration | Tasks 2-6 |
| 8 | Manual testing | Task 7 |

Tasks 1, 2, and 3 are independent and can be done in parallel. Tasks 4, 5, and 6 depend on Task 1 but are independent of each other. Task 7 requires all prior tasks. Task 8 requires Task 7.
