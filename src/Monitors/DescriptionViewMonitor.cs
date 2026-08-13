using System;
using System.Collections.Generic;
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
        private readonly Func<DesktopView> _activeViewProvider;
        private bool _isHooked;
        private bool _loggedMissing;
        private FrameworkElement _hiddenHtmlTextView;
        private FrameworkElement _lastSignalledElement;
        private object _injectionTarget;
        private ScrollViewer _parentScrollViewer;
        private HwndClipper _clipper;

        public bool IsInjected => _hiddenHtmlTextView != null;
        public ScrollViewer ParentScrollViewer => _parentScrollViewer;

        // Content changes don't always resize the WebView, so the clip region can
        // keep an outdated rectangle and hide part of the description
        public void RefreshClip() => _clipper?.UpdateClipRegion();

        public void ResetSearchState()
        {
            _loggedMissing = false;
        }

        public DescriptionViewMonitor(
            Func<WebView2> webViewProvider,
            Func<bool> isEnabled,
            Func<DesktopView> activeViewProvider)
        {
            _webViewProvider = webViewProvider;
            _isEnabled = isEnabled;
            _activeViewProvider = activeViewProvider;
        }

        // Raised when the injected view is torn down (Grid <-> Details switch, tab
        // change), so the host can re-inject without waiting for a game selection.
        public event Action InjectionLost;

        // Raised when a description element appears in the tree while we are not
        // injected. Playnite builds Grid view's details panel lazily, so at startup
        // the element often does not exist yet and there is nothing to inject into.
        public event Action DescriptionAppeared;

        public void StartMonitoring()
        {
            if (_isHooked) return;
            EventManager.RegisterClassHandler(typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));

            // The description element can appear long after startup: Playnite
            // creates the Grid view details panel on demand, and themes may nest
            // it in a lazily-realized TabItem. Watch both directions.
            EventManager.RegisterClassHandler(typeof(FrameworkElement),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnElementLoaded));

            EventManager.RegisterClassHandler(typeof(FrameworkElement),
                FrameworkElement.UnloadedEvent,
                new RoutedEventHandler(OnElementUnloaded));

            _isHooked = true;
        }

        private void OnElementLoaded(object sender, RoutedEventArgs e)
        {
            // Cheap rejection first — this fires for every element in the app
            if (_hiddenHtmlTextView != null) return;
            if (!(sender is FrameworkElement fe)) return;

            var name = fe.Name;
            if (string.IsNullOrEmpty(name)) return;
            if (name != Constants.HtmlDescriptionPartName &&
                name != Constants.DescriptionPanelPartName) return;

            if (!_isEnabled()) return;

            // WPF re-raises Loaded when an element is re-parented, and both the
            // panel and the text view match, so the same arrival can fire several
            // times. Only signal for the first one until injection settles.
            if (ReferenceEquals(fe, _lastSignalledElement)) return;
            _lastSignalledElement = fe;

            Logger.Info($"Description element '{name}' appeared — signalling injection.");
            DescriptionAppeared?.Invoke();
        }

        private void OnElementUnloaded(object sender, RoutedEventArgs e)
        {
            // Only care about the element we are currently injected against
            if (_hiddenHtmlTextView == null) return;
            if (!ReferenceEquals(sender, _hiddenHtmlTextView) &&
                !ReferenceEquals(sender, _injectionTarget)) return;

            Logger.Info("Injected description view unloaded — signalling re-injection.");
            InjectionLost?.Invoke();
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

        // True when the element we injected into is gone from the live visual tree
        // or no longer visible — happens when the user switches view (Grid <-> Details)
        // or moves to a different tab, since WPF tears down the old view.
        public bool IsStale()
        {
            if (_hiddenHtmlTextView == null) return false;

            // Gone from the live tree — the view that owned it was torn down
            if (!_hiddenHtmlTextView.IsLoaded) return true;
            if (PresentationSource.FromVisual(_hiddenHtmlTextView) == null) return true;

            // We are parented under a view host that is no longer the active one:
            // the user switched between Grid and Details. Checked against the SDK
            // rather than IsVisible, which reads false even for the live element
            // when a theme collapses an ancestor.
            var hostName = ActiveViewHostName();
            if (hostName != null && !IsInsideHost(_hiddenHtmlTextView, hostName))
                return true;

            return false;
        }

        // The view host type Playnite instantiates for the active Desktop view.
        // Themes name these consistently because Playnite loads them by convention.
        private string ActiveViewHostName()
        {
            try
            {
                switch (_activeViewProvider())
                {
                    case DesktopView.Details: return "DetailsViewGameOverview";
                    case DesktopView.Grid: return "GridViewGameOverview";
                    // List view shares the Details overview panel
                    case DesktopView.List: return "DetailsViewGameOverview";
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static bool IsInsideHost(DependencyObject node, string hostTypeName)
        {
            var cur = VisualTreeHelper.GetParent(node);
            while (cur != null)
            {
                if (cur.GetType().Name == hostTypeName) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        // Laid-out area of the element, or of its parent when it has not been
        // measured itself (we collapse the element we inject next to).
        private static double OwnerArea(FrameworkElement e)
        {
            var area = e.ActualWidth * e.ActualHeight;
            if (area > 1) return area;
            var p = VisualTreeHelper.GetParent(e) as FrameworkElement;
            return p == null ? 0 : p.ActualWidth * p.ActualHeight;
        }

        // Detaches the WebView from its current parent so it can be re-injected
        // into whichever view is now on screen.
        public void Detach()
        {
            var webView = _webViewProvider();

            _clipper?.Detach();
            _clipper = null;

            if (webView != null)
            {
                if (webView.Parent is Panel p) p.Children.Remove(webView);
                else if (webView.Parent is ContentControl cc && ReferenceEquals(cc.Content, webView)) cc.Content = null;
                else if (_injectionTarget is ScrollViewer sv && ReferenceEquals(sv.Content, webView)) sv.Content = null;
            }

            // Restore the theme's own description element in the old view
            if (_hiddenHtmlTextView != null)
                _hiddenHtmlTextView.Visibility = Visibility.Visible;

            _hiddenHtmlTextView = null;
            _injectionTarget = null;
            _parentScrollViewer = null;
            _loggedMissing = false;

            // Allow the next arrival to signal again, otherwise re-injection after
            // a view switch would be suppressed by the duplicate guard.
            _lastSignalledElement = null;
        }

        public void TryInject(DependencyObject root, WebView2 webView)
        {
            if (webView.Parent != null) return;

            var htmlTextView = FindVisibleByName(root, Constants.HtmlDescriptionPartName);

            if (htmlTextView == null)
            {
                foreach (var altName in Constants.AlternateDescriptionNames)
                {
                    htmlTextView = FindVisibleByName(root, altName);
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
                    Logger.Info("Description element not found in visual tree.");
                    DumpDiagnostics(root);
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

        // Grid view and Details view both declare PART_HtmlDescription, so the tree
        // can hold several. Taking the first match injects into whichever the walk
        // reaches first, which is often the hidden one — pick a rendered element
        // instead, falling back to the first match if none look visible yet.
        private FrameworkElement FindVisibleByName(DependencyObject root, string name)
        {
            var hits = new List<FrameworkElement>();
            FindAllByName(root, name, hits);
            if (hits.Count == 0) return null;
            if (hits.Count == 1) return hits[0];

            // Ask Playnite which view is active rather than guessing. IsVisible is
            // useless here — themes collapse an ancestor (an Expander around the
            // description), so every candidate reports IsVisible=false even when one
            // of them is in the view on screen.
            var hostName = ActiveViewHostName();
            if (hostName != null)
            {
                foreach (var e in hits)
                {
                    if (IsInsideHost(e, hostName))
                    {
                        Logger.Info($"{hits.Count} '{name}' elements; chose the one under {hostName}.");
                        return e;
                    }
                }
                Logger.Info($"{hits.Count} '{name}' elements, none under {hostName}.");
            }

            // Fallback: the largest laid-out candidate. The inactive view's copy
            // is normally 0x0.
            FrameworkElement best = null;
            double bestArea = 0;
            foreach (var e in hits)
            {
                if (PresentationSource.FromVisual(e) == null) continue;
                var area = OwnerArea(e);
                if (area > bestArea) { bestArea = area; best = e; }
            }

            if (best != null)
            {
                Logger.Info($"Chose the laid-out '{name}' ({best.ActualWidth:F0}x{best.ActualHeight:F0}).");
                return best;
            }

            Logger.Info($"{hits.Count} '{name}' elements found, none laid out yet; using the first.");
            return hits[0];
        }

        // Collects every element with the given name, not just the first. Grid view
        // and Details view both declare PART_HtmlDescription, so more than one can
        // be live at once.
        private static void FindAllByName(DependencyObject parent, string name, List<FrameworkElement> results)
        {
            if (parent == null) return;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name)
                    results.Add(fe);
                FindAllByName(child, name, results);
            }
        }

        // Walks up recording the ancestor chain, so we can tell which view an
        // element belongs to and whether it sits inside a lazily-realized TabItem.
        private static string DescribeAncestry(DependencyObject node, int maxDepth = 14)
        {
            var parts = new List<string>();
            var cur = VisualTreeHelper.GetParent(node);
            int depth = 0;
            while (cur != null && depth++ < maxDepth)
            {
                var n = cur is FrameworkElement f && !string.IsNullOrEmpty(f.Name)
                    ? $"{cur.GetType().Name}#{f.Name}" : cur.GetType().Name;
                parts.Add(n);
                cur = VisualTreeHelper.GetParent(cur);
            }
            return string.Join(" < ", parts);
        }

        // Dumps everything needed to tell the two failure modes apart:
        //   1. element missing entirely  -> lazy TabItem not yet realized
        //   2. multiple elements present -> we may be injecting into the hidden one
        public void DumpDiagnostics(DependencyObject root)
        {
            try
            {
                Logger.Info("===== PlayGif injection diagnostics =====");
                string activeView;
                try { activeView = _activeViewProvider().ToString(); }
                catch (Exception ex) { activeView = "unavailable: " + ex.Message; }
                Logger.Info($"[active view] {activeView} -> host {ActiveViewHostName() ?? "unknown"}");

                foreach (var name in new[] { Constants.HtmlDescriptionPartName,
                                             Constants.DescriptionPanelPartName })
                {
                    var hits = new List<FrameworkElement>();
                    FindAllByName(root, name, hits);
                    Logger.Info($"[{name}] found {hits.Count}");

                    for (int i = 0; i < hits.Count; i++)
                    {
                        var e = hits[i];
                        var tab = FindAncestor<TabItem>(e);
                        var tabInfo = tab == null ? "none"
                            : $"{(string.IsNullOrEmpty(tab.Name) ? "(unnamed)" : tab.Name)} selected={tab.IsSelected}";
                        Logger.Info(
                            $"  #{i}: type={e.GetType().Name} visible={e.IsVisible} " +
                            $"loaded={e.IsLoaded} size={e.ActualWidth:F0}x{e.ActualHeight:F0} " +
                            $"vis={e.Visibility} tabItem=[{tabInfo}]");
                        var host = ActiveViewHostName();
                        Logger.Info($"       parent={VisualTreeHelper.GetParent(e)?.GetType().Name ?? "null"} " +
                                    $"inActiveHost={(host != null && IsInsideHost(e, host))}");
                        Logger.Info($"       ancestry={DescribeAncestry(e)}");
                    }
                }

                // Which TabControls exist and what is selected in each
                var tabs = new List<FrameworkElement>();
                CollectByType<TabControl>(root, tabs);
                Logger.Info($"[TabControls] found {tabs.Count}");
                foreach (var t in tabs)
                {
                    var tc = (TabControl)t;
                    Logger.Info($"  items={tc.Items.Count} selectedIndex={tc.SelectedIndex} visible={tc.IsVisible}");
                }

                Logger.Info($"[state] isInjected={IsInjected} scrollViewer={(_parentScrollViewer == null ? "null" : "set")}");

                // Catches themes that use a description element name we don't know
                Logger.Info("[named elements] description/detail-like names in tree:");
                DumpNamedElements(root, 0);

                Logger.Info("===== end diagnostics =====");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Diagnostics dump failed");
            }
        }

        private static void CollectByType<T>(DependencyObject parent, List<FrameworkElement> results)
            where T : FrameworkElement
        {
            if (parent == null) return;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) results.Add(t);
                CollectByType<T>(child, results);
            }
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
