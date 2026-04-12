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
        private bool _clipDirty;

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
            System.Windows.Media.CompositionTarget.Rendering += OnRender;
            _clipDirty = true;
        }

        public void Detach()
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.ScrollChanged -= OnScrollChanged;
                _scrollViewer.SizeChanged -= OnSizeChanged;
            }
            System.Windows.Media.CompositionTarget.Rendering -= OnRender;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            _clipDirty = true;
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _clipDirty = true;
        }

        private void OnRender(object sender, EventArgs e)
        {
            if (_clipDirty)
            {
                _clipDirty = false;
                UpdateClipRegion();
            }
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
                    var emptyRgn = CreateRectRgn(0, 0, 0, 0);
                    SetWindowRgn(webViewHwnd, emptyRgn, true);
                }
                else
                {
                    var localTransform = _window.TransformToDescendant(_webView);
                    var localRect = localTransform.TransformBounds(intersect);

                    var dpi = VisualTreeHelper.GetDpi(_webView);
                    var rgn = CreateRectRgn(
                        (int)(localRect.X * dpi.DpiScaleX),
                        (int)(localRect.Y * dpi.DpiScaleY),
                        (int)((localRect.X + localRect.Width) * dpi.DpiScaleX),
                        (int)((localRect.Y + localRect.Height) * dpi.DpiScaleY));
                    SetWindowRgn(webViewHwnd, rgn, false);
                }
            }
            catch { }
        }
    }
}
