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
