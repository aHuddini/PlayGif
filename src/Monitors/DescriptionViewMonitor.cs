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
        private bool _loggedMissing;
        private FrameworkElement _hiddenHtmlTextView;
        private object _injectionTarget;
        private ScrollViewer _parentScrollViewer;
        private HwndClipper _clipper;

        public bool IsInjected => _hiddenHtmlTextView != null;
        public ScrollViewer ParentScrollViewer => _parentScrollViewer;

        public void ResetSearchState()
        {
            _loggedMissing = false;
        }

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
            if (webView.Parent != null) return;

            TryInject(window, webView);
        }

        public void TryInject(DependencyObject root, WebView2 webView)
        {
            if (webView.Parent != null) return;

            var htmlTextView = FindChildByName(root, Constants.HtmlDescriptionPartName);

            if (htmlTextView == null)
            {
                foreach (var altName in Constants.AlternateDescriptionNames)
                {
                    htmlTextView = FindChildByName(root, altName);
                    if (htmlTextView != null)
                    {
                        Logger.Info($"Found description via alternate name: {altName}");
                        break;
                    }
                }
            }

            if (htmlTextView == null)
            {
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    Logger.Info("Description element not found in visual tree. Dumping named elements...");
                    DumpNamedElements(root, 0);
                }
                return;
            }

            var parent = VisualTreeHelper.GetParent(htmlTextView);
            if (parent == null)
            {
                Logger.Info("Description element has no visual parent.");
                return;
            }

            Logger.Info($"Found description element. Parent type: {parent.GetType().Name}");

            // Desktop mode: parent is a Panel (StackPanel PART_ElemDescription)
            // WebView2 is full content height (set by JS reportHeight)
            // No internal scrolling — parent ScrollViewer scrolls, SetWindowRgn clips
            if (parent is Panel panel)
            {
                htmlTextView.Visibility = Visibility.Collapsed;
                _hiddenHtmlTextView = htmlTextView;
                _injectionTarget = panel;
                _parentScrollViewer = FindAncestor<ScrollViewer>(panel);

                webView.HorizontalAlignment = HorizontalAlignment.Stretch;

                int index = panel.Children.IndexOf(htmlTextView);
                if (index < 0) index = panel.Children.Count;
                panel.Children.Insert(index + 1, webView);

                // Clip the HWND to prevent airspace overflow
                if (_parentScrollViewer != null)
                {
                    _clipper = new HwndClipper(webView, _parentScrollViewer);
                    _clipper.Attach();
                }

                Logger.Info("Injected into Panel (desktop mode) with HWND clipping.");
                return;
            }

            // Fullscreen / other: find the nearest ScrollViewer ancestor
            _parentScrollViewer = FindAncestor<ScrollViewer>(htmlTextView);
            if (_parentScrollViewer != null)
            {
                htmlTextView.Visibility = Visibility.Collapsed;
                _hiddenHtmlTextView = htmlTextView;
                _injectionTarget = _parentScrollViewer;

                webView.HorizontalAlignment = HorizontalAlignment.Stretch;
                _parentScrollViewer.Content = webView;

                _clipper = new HwndClipper(webView, _parentScrollViewer);
                _clipper.Attach();

                Logger.Info($"Injected into ScrollViewer ({_parentScrollViewer.GetType().Name}) with HWND clipping.");
                return;
            }

            // Last resort
            var ancestorPanel = FindAncestor<Panel>(htmlTextView);
            if (ancestorPanel != null)
            {
                htmlTextView.Visibility = Visibility.Collapsed;
                _hiddenHtmlTextView = htmlTextView;
                _injectionTarget = ancestorPanel;
                ancestorPanel.Children.Add(webView);
                Logger.Info($"Injected via ancestor Panel: {ancestorPanel.GetType().Name}");
                return;
            }

            Logger.Info($"Could not inject — unsupported parent type: {parent.GetType().Name}");
        }

        public void Restore()
        {
            _clipper?.Detach();
            _clipper = null;

            var webView = _webViewProvider();

            if (webView?.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(webView);
            }
            else if (_injectionTarget is ScrollViewer sv)
            {
                sv.Content = _hiddenHtmlTextView;
            }

            if (_hiddenHtmlTextView != null)
            {
                _hiddenHtmlTextView.Visibility = Visibility.Visible;
                _hiddenHtmlTextView = null;
            }

            _injectionTarget = null;
            _parentScrollViewer = null;
        }

        private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(element);
            while (parent != null)
            {
                if (parent is T match) return match;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
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

        private static void DumpNamedElements(DependencyObject parent, int depth)
        {
            if (parent == null || depth > 15) return;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                {
                    var nameLower = fe.Name.ToLowerInvariant();
                    if (nameLower.Contains("desc") || nameLower.Contains("html") ||
                        nameLower.Contains("detail") || nameLower.Contains("part_") ||
                        nameLower.Contains("content") || nameLower.Contains("scroll"))
                    {
                        Logger.Info($"  [Tree] {new string(' ', depth * 2)}{fe.GetType().Name} Name=\"{fe.Name}\"");
                    }
                }
                DumpNamedElements(child, depth + 1);
            }
        }
    }
}
