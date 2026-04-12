using System.Collections.Generic;
using System.ComponentModel;
using Playnite.SDK;

namespace PlayGif
{
    public class PlayGifSettings : ISettings, INotifyPropertyChanged
    {
        private readonly PlayGif _plugin;

        public event PropertyChangedEventHandler PropertyChanged;

        // Serialization constructor
        public PlayGifSettings() { }

        public PlayGifSettings(PlayGif plugin)
        {
            _plugin = plugin;
            var saved = plugin.LoadPluginSettings<PlayGifSettings>();
            if (saved != null)
            {
                EnableAnimatedDescriptions = saved.EnableAnimatedDescriptions;
                AutoCacheMedia = saved.AutoCacheMedia;
                MaxCachePerGameMB = saved.MaxCachePerGameMB;
                EnableDebugMode = saved.EnableDebugMode;
            }
        }

        private bool enableAnimatedDescriptions = true;
        public bool EnableAnimatedDescriptions
        {
            get => enableAnimatedDescriptions;
            set { enableAnimatedDescriptions = value; OnPropertyChanged(nameof(EnableAnimatedDescriptions)); }
        }

        private bool autoCacheMedia = true;
        public bool AutoCacheMedia
        {
            get => autoCacheMedia;
            set { autoCacheMedia = value; OnPropertyChanged(nameof(AutoCacheMedia)); }
        }

        private int maxCachePerGameMB = Common.Constants.DefaultMaxCachePerGameMB;
        public int MaxCachePerGameMB
        {
            get => maxCachePerGameMB;
            set { maxCachePerGameMB = value; OnPropertyChanged(nameof(MaxCachePerGameMB)); }
        }

        private bool enableDebugMode = false;
        public bool EnableDebugMode
        {
            get => enableDebugMode;
            set { enableDebugMode = value; OnPropertyChanged(nameof(EnableDebugMode)); }
        }

        protected void OnPropertyChanged(string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // ISettings implementation
        public void BeginEdit() { }
        public void CancelEdit() { }
        public void EndEdit()
        {
            _plugin.SavePluginSettings(this);
        }
        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}
