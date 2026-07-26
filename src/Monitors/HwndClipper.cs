using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;

namespace PlayGif.Monitors
{
    // Clips a WebView2 HWND to its parent ScrollViewer's viewport bounds
    // using SetWindowRgn — the only way to clip an HWND in WPF (airspace workaround)
    public class HwndClipper
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        [DllImport("User32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("Gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        private readonly WebView2 _webView;
        private readonly ScrollViewer _scrollViewer;
        private Window _window;
        // Last region actually handed to SetWindowRgn, in HWND-local device pixels
        private Rect? _lastRegion;

        public HwndClipper(WebView2 webView, ScrollViewer scrollViewer)
        {
            _webView = webView;
            _scrollViewer = scrollViewer;
        }

        public void Attach()
        {
            if (_scrollViewer == null) return;
            _scrollViewer.ScrollChanged += OnScrollChanged;
            _scrollViewer.SizeChanged += OnSizeChanged;
            // The WebView grows when content loads — without this the clip region
            // keeps the old, shorter rectangle and the extra text stays hidden
            _webView.SizeChanged += OnSizeChanged;
            // Catches offset/layout shifts that raise no scroll or size event.
            // Cheap: the region cache turns most of these into a no-op.
            _webView.LayoutUpdated += OnLayoutUpdated;
            // Initial clip after a short delay to let layout settle
            _webView.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => UpdateClipRegion()));
        }

        private void OnLayoutUpdated(object sender, EventArgs e)
        {
            UpdateClipRegion();
        }

        public void Detach()
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer.SizeChanged -= OnSizeChanged;
            }
            if (_webView != null)
            {
                _webView.SizeChanged -= OnSizeChanged;
                _webView.LayoutUpdated -= OnLayoutUpdated;
            }
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Always recompute — the region cache below skips the GDI call when
            // nothing actually moved, so filtering on VerticalChange here only
            // risks missing offset changes that report zero delta
            UpdateClipRegion();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Re-clip now, then again once layout has settled — on a size change the
            // ScrollViewer may not have updated its viewport/extent yet
            UpdateClipRegion();
            _webView.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => UpdateClipRegion()));
        }

        public void UpdateClipRegion()
        {
            try
            {
                if (_webView == null || _scrollViewer == null) return;
                if (!_webView.IsLoaded) return;

                if (_window == null)
                    _window = Window.GetWindow(_webView);
                if (_window == null) return;

                var hwndSource = PresentationSource.FromVisual(_webView) as HwndSource;
                if (hwndSource == null) return;

                var webViewHwnd = _webView.Handle;
                if (webViewHwnd == IntPtr.Zero) return;

                var svTransform = _scrollViewer.TransformToAncestor(_window);
                var svRect = new Rect(
                    svTransform.Transform(new Point(0, 0)),
                    new Size(_scrollViewer.ViewportWidth, _scrollViewer.ViewportHeight));

                var wvTransform = _webView.TransformToAncestor(_window);
                var wvRect = new Rect(
                    wvTransform.Transform(new Point(0, 0)),
                    new Size(_webView.ActualWidth, _webView.ActualHeight));

                var intersect = Rect.Intersect(svRect, wvRect);

                if (intersect.IsEmpty)
                {
                    if (_lastRegion.HasValue && _lastRegion.Value.IsEmpty) return;
                    _lastRegion = Rect.Empty;

                    var emptyRgn = CreateRectRgn(0, 0, 0, 0);
                    SetWindowRgn(webViewHwnd, emptyRgn, true);
                }
                else
                {
                    // Region coordinates are HWND-local. Scrolling moves the WebView, so
                    // the same window-space intersection maps to a different local rect —
                    // the comparison must happen after this transform, not before it.
                    var localTransform = _window.TransformToDescendant(_webView);
                    var localRect = localTransform.TransformBounds(intersect);

                    var dpi = VisualTreeHelper.GetDpi(_webView);
                    var region = new Rect(
                        (int)(localRect.X * dpi.DpiScaleX),
                        (int)(localRect.Y * dpi.DpiScaleY),
                        (int)(localRect.Width * dpi.DpiScaleX),
                        (int)(localRect.Height * dpi.DpiScaleY));

                    if (_lastRegion.HasValue && region == _lastRegion.Value) return;
                    _lastRegion = region;

                    var rgn = CreateRectRgn(
                        (int)region.X,
                        (int)region.Y,
                        (int)(region.X + region.Width),
                        (int)(region.Y + region.Height));
                    SetWindowRgn(webViewHwnd, rgn, false);
                }
            }
            catch { }
        }
    }
}
