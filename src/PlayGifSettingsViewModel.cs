namespace PlayGif
{
    public class PlayGifSettingsViewModel
    {
        private readonly PlayGif _plugin;

        public PlayGifSettings Settings => _plugin.Settings;

        public PlayGifSettingsViewModel(PlayGif plugin)
        {
            _plugin = plugin;
        }
    }
}
