using System;
using System.Collections.Generic;
using System.Linq;
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

        // Scale choices for the settings dropdown. The shell clamps to 10-100,
        // so the list stays inside that range.
        public class ScaleOption
        {
            public string Label { get; set; }
            public int Value { get; set; }
        }

        public List<ScaleOption> VideoScaleOptions { get; } =
            Common.Constants.VideoScaleSteps.Select(v => new ScaleOption
            {
                Value = v,
                Label = v == 100 ? "100% (full size)"
                      : v == 50 ? "50% (half size)"
                      : v == 25 ? "25% (quarter size)"
                      : $"{v}%"
            }).ToList();

        public PlayGifSettingsViewModel(PlayGif plugin)
        {
            _plugin = plugin;

            var saved = plugin.LoadPluginSettings<PlayGifSettings>();
            _settings = saved ?? new PlayGifSettings();

            NormalizeVideoScale();
        }

        // Older builds let the scale be typed freely, so a saved value may not
        // match any dropdown entry — the ComboBox would render blank. Snap it to
        // the nearest offered value.
        private void NormalizeVideoScale()
        {
            if (VideoScaleOptions.Exists(o => o.Value == _settings.VideoScale)) return;

            var nearest = VideoScaleOptions[0];
            foreach (var o in VideoScaleOptions)
            {
                if (Math.Abs(o.Value - _settings.VideoScale) <
                    Math.Abs(nearest.Value - _settings.VideoScale))
                    nearest = o;
            }
            _settings.VideoScale = nearest.Value;
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
