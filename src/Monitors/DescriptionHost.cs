using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Playnite.SDK.Controls;
using PlayGif.Common;

namespace PlayGif.Monitors
{
    // The theme-placed renderer slot, <ContentControl x:Name="PlayGif_AnimatedDescription"/>.
    // Injection prefers it over PART_HtmlDescription, so the theme decides where the
    // renderer goes and styles around it. Hidden while the renderer is elsewhere or
    // PlayGif is off, so a theme can fall back to its own description.
    public class DescriptionHost : PluginUserControl
    {
        public DescriptionHost(Action<FrameworkElement> appeared, Action<FrameworkElement> gone)
        {
            // What injection finds and sits beside, as it would a theme's description
            var slot = new FrameworkElement { Name = Constants.HostSlotName };
            var grid = new HostGrid(this);
            grid.Children.Add(slot);
            Content = grid;
            Focusable = false;
            // Hidden rather than collapsed: a collapsed control is never measured, so
            // its content never joins the visual tree and injection could not find it
            Visibility = Visibility.Hidden;

            // This control's own handlers, so WPF raises them, unlike class handlers
            // on a theme's elements
            Loaded += (s, e) => appeared(slot);
            Unloaded += (s, e) => gone(slot);
        }

        private sealed class HostGrid : Grid
        {
            private readonly DescriptionHost _host;

            public HostGrid(DescriptionHost host)
            {
                _host = host;
            }

            protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
            {
                base.OnVisualChildrenChanged(added, removed);
                _host.Visibility = Children.OfType<WebView2>().Any() ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
}
