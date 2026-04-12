using System.Collections.Generic;
using System.ComponentModel;
using Playnite.SDK;

namespace PlayGif
{
    public class PlayGifSettings : ISettings, INotifyPropertyChanged
    {
        private readonly PlayGif _plugin;

        public event PropertyChangedEventHandler PropertyChanged;

        // Serialization constructor (used by Playnite when loading settings from disk)
        public PlayGifSettings() { }

        public PlayGifSettings(PlayGif plugin)
        {
            _plugin = plugin;
            var saved = plugin.LoadPluginSettings<PlayGifSettings>();
            if (saved != null)
            {
                // Copy saved values to this instance
                EnableGifs = saved.EnableGifs;
            }
        }

        // Settings properties
        private bool enableGifs = true;
        public bool EnableGifs
        {
            get => enableGifs;
            set { enableGifs = value; OnPropertyChanged(nameof(EnableGifs)); }
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
