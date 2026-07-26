using System.Collections.Generic;
using Newtonsoft.Json;
using Playnite.SDK;

namespace PlayGif
{
    public class PlayGifSettingsViewModel : ObservableObject, ISettings
    {
        private readonly PlayGif _plugin;
        private PlayGifSettings _settings;

        public PlayGifSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
            }
        }

        public PlayGifSettingsViewModel(PlayGif plugin)
        {
            _plugin = plugin;

            var saved = plugin.LoadPluginSettings<PlayGifSettings>();
            _settings = saved ?? new PlayGifSettings();
        }

        // Called when settings dialog opens
        public void BeginEdit() { }

        // Called when user cancels — reload from disk
        public void CancelEdit()
        {
            var saved = _plugin.LoadPluginSettings<PlayGifSettings>();
            if (saved != null)
            {
                Settings = saved;
            }
        }

        // Called when user confirms — save to disk
        public void EndEdit()
        {
            _plugin.SavePluginSettings(_settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }

        public void BulkFetchSteamDescriptions()
        {
            _plugin.RunBulkSteamFetch();
        }

        public void RunLayoutReport()
        {
            _plugin.RunLayoutReport();
        }

        public void OpenLogFolder()
        {
            _plugin.OpenLogFolderPublic();
        }
    }
}
