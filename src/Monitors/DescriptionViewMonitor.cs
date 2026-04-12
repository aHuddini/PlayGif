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
            if (webView.Parent != null) return;

            TryInject(window, webView);
        }

        public void TryInject(DependencyObject root, WebView2 webView)
        {
            if (webView.Parent != null) return;

            var htmlTextView = FindChildByName(root, Constants.HtmlDescriptionPartName);
            if (htmlTextView == null)
            {
                // Only log once, not on every attempt
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    Logger.Info("PART_HtmlDescription not found in visual tree. Dumping named elements...");
                    DumpNamedElements(root, 0);
                }
                return;
            }

            var parent = VisualTreeHelper.GetParent(htmlTextView);
            if (parent == null)
            {
                Logger.Info("PART_HtmlDescription has no visual parent.");
                return;
            }

            Logger.Info($"Found PART_HtmlDescription. Parent type: {parent.GetType().Name}");

            // Desktop mode: parent is a Panel (StackPanel PART_ElemDescription)
            if (parent is Panel panel)
            {
                htmlTextView.Visibility = Visibility.Collapsed;
                _hiddenHtmlTextView = htmlTextView;
                _injectionTarget = panel;

                // Size WebView2 to fill the available viewport
                _parentScrollViewer = FindAncestor<ScrollViewer>(panel);
                if (_parentScrollViewer != null)
                {
                    webView.SetBinding(FrameworkElement.HeightProperty,
                        new System.Windows.Data.Binding("ViewportHeight")
                        {
                            Source = _parentScrollViewer,
                            Mode = System.Windows.Data.BindingMode.OneWay
                        });
                    Logger.Info($"Bound height to viewport: {_parentScrollViewer.ViewportHeight}px");
                }
                else
                {
                    webView.Height = 600;
                }

                int index = panel.Children.IndexOf(htmlTextView);
                if (index < 0) index = panel.Children.Count;
                panel.Children.Insert(index + 1, webView);

                Logger.Info("Injected into Panel (desktop mode).");
                return;
            }

            // Fullscreen mode: parent is a ScrollViewer (ScrollViewerEx PART_ScrollHtmlDescription)
            // ScrollViewer has a single Content property — we need to wrap both in a StackPanel
            if (parent is ScrollViewer scrollViewer)
            {
                htmlTextView.Visibility = Visibility.Collapsed;
                _hiddenHtmlTextView = htmlTextView;
                _injectionTarget = scrollViewer;

                // Replace the ScrollViewer content with a wrapper containing both
                var wrapper = new StackPanel();
                scrollViewer.Content = wrapper;
                wrapper.Children.Add(webView);

                Logger.Info("Injected into ScrollViewer (fullscreen mode).");
                return;
            }

            // Fallback: try to find a Panel ancestor higher up
            var ancestorPanel = FindAncestor<Panel>(htmlTextView);
            if (ancestorPanel != null)
            {
                htmlTextView.Visibility = Visibility.Collapsed;
                _hiddenHtmlTextView = htmlTextView;
                _injectionTarget = ancestorPanel;

                int index = ancestorPanel.Children.IndexOf(htmlTextView);
                if (index >= 0)
                {
                    ancestorPanel.Children.Insert(index + 1, webView);
                }
                else
                {
                    ancestorPanel.Children.Add(webView);
                }

                Logger.Info($"Injected via ancestor Panel: {ancestorPanel.GetType().Name}");
                return;
            }

            Logger.Info($"Could not inject — unsupported parent type: {parent.GetType().Name}");
        }

        public void Restore()
        {
            var webView = _webViewProvider();

            if (webView?.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(webView);
            }
            else if (webView?.Parent is StackPanel wrapper && _injectionTarget is ScrollViewer sv)
            {
                wrapper.Children.Remove(webView);
                // Restore original content to ScrollViewer
                if (_hiddenHtmlTextView != null)
                {
                    sv.Content = _hiddenHtmlTextView;
                }
            }

            if (_hiddenHtmlTextView != null)
            {
                _hiddenHtmlTextView.Visibility = Visibility.Visible;
                _hiddenHtmlTextView = null;
            }

            _injectionTarget = null;
        }

        public void ForwardScroll(double delta)
        {
            if (_parentScrollViewer == null) return;
            _parentScrollViewer.ScrollToVerticalOffset(
                _parentScrollViewer.VerticalOffset + delta);
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

        // Dump named elements to help diagnose fullscreen visual tree
        private static void DumpNamedElements(DependencyObject parent, int depth)
        {
            if (parent == null || depth > 15) return;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                {
                    // Log elements with names containing "description", "html", "detail", or "PART_"
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
