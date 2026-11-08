using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK;
using PlayGif.Common;

namespace PlayGif.Monitors
{
    // Clips a WebView2 HWND to its parent ScrollViewer's viewport bounds
    // using SetWindowRgn — the only way to clip an HWND in WPF (airspace workaround)
    public class HwndClipper
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        [DllImport("User32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("User32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [DllImport("Gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

        private readonly WebView2 _webView;
        private readonly ScrollViewer _scrollViewer;
        private Window _window;
        // Last region actually handed to SetWindowRgn, in HWND-local device pixels
        private Rect? _lastRegion;

        // Hidden after appearing until its on-screen position holds still
        private bool _settling;
        private Rect _lastBounds;
        private int _stillFrames;
        private int _settleFrames;

        // The WebView is sized to the whole description, so the page itself thinks
        // everything is on screen. This reports the slice that really is, top to
        // bottom in WebView DIPs (equal to CSS px), whenever the clip changes.
        public event Action<double, double> VisibleRangeChanged;

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
            _webView.IsVisibleChanged += OnIsVisibleChanged;
            // A WebView moved in from parking, or from another view, arrives
            // already visible, so no change will be raised for it
            if (_webView.IsVisible) StartSettling();
            // No LayoutUpdated: it fires for every layout pass in Playnite, not just
            // this element's, so it taxed every other extension's UI. Content
            // shifting above the WebView changes the extent, which ScrollChanged covers.
            // Initial clip after a short delay to let layout settle
            _webView.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    // Display-only. Windows skips a disabled HWND when hit-testing,
                    // so the mouse and wheel reach WPF beneath it and the page
                    // scrolls natively — same speed and smoothing as the rest of it
                    if (_webView.Handle != IntPtr.Zero) EnableWindow(_webView.Handle, false);
                    UpdateClipRegion();
                }));
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
                _webView.IsVisibleChanged -= OnIsVisibleChanged;
            }
            if (_settling)
            {
                _settling = false;
                CompositionTarget.Rendering -= OnSettleFrame;
            }
        }

        // Themes slide and zoom panels in with render transforms, which never cause
        // a layout pass, so HwndHost leaves the HWND wherever the first frame of the
        // animation put it, on top of the UI beside the panel. HWNDs ignore opacity
        // too, so fades can't be followed either. Stay hidden until it holds still.
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue) StartSettling();
        }

        private void StartSettling()
        {
            if (_settling) return;
            _settling = true;
            _stillFrames = 0;
            _settleFrames = 0;
            _lastBounds = Rect.Empty;

            if (_webView.Handle != IntPtr.Zero)
            {
                SetWindowRgn(_webView.Handle, CreateRectRgn(0, 0, 0, 0), true);
                _lastRegion = Rect.Empty;
            }
            CompositionTarget.Rendering += OnSettleFrame;
        }

        private void OnSettleFrame(object sender, EventArgs e)
        {
            var bounds = WindowBounds(_webView);
            _stillFrames = bounds == _lastBounds ? _stillFrames + 1 : 0;
            _lastBounds = bounds;
            // ponytail: frame counts, not the theme's real animation length; an
            // entrance longer than the cap shows the description before it ends
            if (_stillFrames < Constants.SettleStillFrames &&
                ++_settleFrames < Constants.SettleMaxFrames) return;

            CompositionTarget.Rendering -= OnSettleFrame;
            _settling = false;
            _webView.UpdateWindowPos();
            UpdateClipRegion();
        }

        // On-screen rectangle in window DIPs, render transforms included
        private Rect WindowBounds(FrameworkElement e)
        {
            try
            {
                if (_window == null) _window = Window.GetWindow(_webView);
                if (_window == null || !e.IsVisible) return Rect.Empty;
                return e.TransformToAncestor(_window).TransformBounds(new Rect(e.RenderSize));
            }
            catch { return Rect.Empty; }
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
                // Shown when it settles; clipping now would reveal it mid-animation
                if (_settling) return;

                if (_window == null)
                    _window = Window.GetWindow(_webView);
                if (_window == null) return;

                var hwndSource = PresentationSource.FromVisual(_webView) as HwndSource;
                if (hwndSource == null) return;

                var webViewHwnd = _webView.Handle;
                if (webViewHwnd == IntPtr.Zero) return;

                // Bounds rather than origin + size: fullscreen lays out at 1080p and
                // scales the whole UI to the screen, so sizes must be transformed too
                var svRect = _scrollViewer.TransformToAncestor(_window).TransformBounds(
                    new Rect(0, 0, _scrollViewer.ViewportWidth, _scrollViewer.ViewportHeight));
                var wvRect = WindowBounds(_webView);
                if (wvRect.IsEmpty) return;

                // HwndHost sizes the HWND in scaled pixels, but the page would lay out
                // and report its height in unscaled ones: text came out too large and
                // the end of the description was cut off. Zoom the page to match.
                if (_webView.ActualWidth > 0)
                {
                    var scale = wvRect.Width / _webView.ActualWidth;
                    if (Math.Abs(_webView.ZoomFactor - scale) > 0.001)
                        _webView.ZoomFactor = scale;
                }

                var intersect = Rect.Intersect(svRect, wvRect);

                if (intersect.IsEmpty)
                {
                    if (_lastRegion.HasValue && _lastRegion.Value.IsEmpty) return;
                    _lastRegion = Rect.Empty;

                    var emptyRgn = CreateRectRgn(0, 0, 0, 0);
                    SetWindowRgn(webViewHwnd, emptyRgn, true);
                    VisibleRangeChanged?.Invoke(0, 0);
                }
                else
                {
                    // Region coordinates are HWND-local device pixels, and the HWND sits
                    // exactly on wvRect. Scrolling moves the WebView, so the same window-
                    // space intersection maps to a different local rect — the comparison
                    // must happen after this conversion, not before it.
                    var dpi = VisualTreeHelper.GetDpi(_webView);
                    var region = new Rect(
                        (int)((intersect.X - wvRect.X) * dpi.DpiScaleX),
                        (int)((intersect.Y - wvRect.Y) * dpi.DpiScaleY),
                        (int)(intersect.Width * dpi.DpiScaleX),
                        (int)(intersect.Height * dpi.DpiScaleY));

                    if (_lastRegion.HasValue && region == _lastRegion.Value) return;
                    _lastRegion = region;

                    // In WebView DIPs, which the zoom above keeps equal to CSS px
                    var localRect = _window.TransformToDescendant(_webView).TransformBounds(intersect);

                    var rgn = CreateRectRgn(
                        (int)region.X,
                        (int)region.Y,
                        (int)(region.X + region.Width),
                        (int)(region.Y + region.Height));
                    SetWindowRgn(webViewHwnd, rgn, false);
                    VisibleRangeChanged?.Invoke(localRect.Top, localRect.Bottom);
                }
            }
            catch { }
        }
    }
}
