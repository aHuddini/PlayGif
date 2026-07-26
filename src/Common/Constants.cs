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

        // Video scale choices, shared by the settings dropdown and the per-game
        // menu so the two can't drift apart. The shell clamps to 10-100.
        public static readonly int[] VideoScaleSteps = { 100, 90, 75, 50, 35, 25 };

        // Verbose injection logging. Grid view always has two description
        // candidates, so this fires on normal browsing — keep it behind debug
        // mode rather than filling the shared extension.log for every user.
        public const bool LogInjectionDiagnostics = false;

        #endregion
    }
}
