using System.ComponentModel;
using Newtonsoft.Json;

namespace PlayGif
{
    public class PlayGifSettings : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

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

        private bool enableInFullscreen = false;
        public bool EnableInFullscreen
        {
            get => enableInFullscreen;
            set { enableInFullscreen = value; OnPropertyChanged(nameof(EnableInFullscreen)); }
        }

        private bool useVideoPosterOnly = false;
        public bool UseVideoPosterOnly
        {
            get => useVideoPosterOnly;
            set { useVideoPosterOnly = value; OnPropertyChanged(nameof(UseVideoPosterOnly)); }
        }

        private int maxVideoHeight = 0;
        public int MaxVideoHeight
        {
            get => maxVideoHeight;
            set { maxVideoHeight = value; OnPropertyChanged(nameof(MaxVideoHeight)); }
        }

        private int videoScale = 100;
        public int VideoScale
        {
            get => videoScale;
            set { videoScale = value; OnPropertyChanged(nameof(VideoScale)); }
        }

        private string ffmpegPath = "";
        public string FfmpegPath
        {
            get => ffmpegPath;
            set { ffmpegPath = value; OnPropertyChanged(nameof(FfmpegPath)); }
        }

        private bool enableDebugMode = false;
        public bool EnableDebugMode
        {
            get => enableDebugMode;
            set { enableDebugMode = value; OnPropertyChanged(nameof(EnableDebugMode)); }
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
