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
        // Some themes use non-standard names
        public static readonly string[] AlternateDescriptionNames =
            { "DescriptionText", "PART_Description" };

        // Extra logging around visual-tree injection. Themes that nest the
        // description in a lazily-realized TabItem, or views that declare it
        // twice, are only diagnosable from a live session.
        public const bool LogInjectionDiagnostics = true;

        #endregion
    }
}
