using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using XamlAnimatedGif;

namespace PlayGif.Views
{
    public class MediaItem
    {
        public string ThumbUrl { get; set; }
        public string FullUrl { get; set; }
        public string Size { get; set; }

        // Shown in the picker so the format is known before downloading.
        // Derived from the URL, so it is a hint — the real format is confirmed
        // from the response when the file is fetched.
        public string Format
        {
            get
            {
                if (string.IsNullOrEmpty(FullUrl)) return "?";

                var path = FullUrl.Split('?')[0].ToLowerInvariant();
                var dot = path.LastIndexOf('.');
                if (dot < 0 || dot < path.Length - 6) return "link";

                var ext = path.Substring(dot + 1);
                switch (ext)
                {
                    case "gif": return "GIF";
                    case "gifv": return "GIFV";   // Imgur wrapper, fetched as MP4
                    case "webp": return "WEBP";
                    case "apng": return "APNG";
                    case "png": return "PNG";
                    case "jpg":
                    case "jpeg": return "JPG";
                    case "avif": return "AVIF";
                    case "mp4": return "MP4";
                    case "webm": return "WEBM";
                    default: return "link";
                }
            }
        }

        // Animated formats get highlighted, since that is usually what is wanted
        public bool IsAnimated
        {
            get
            {
                var f = Format;
                return f == "GIF" || f == "GIFV" || f == "WEBP" ||
                       f == "APNG" || f == "MP4" || f == "WEBM";
            }
        }
    }

    public partial class MediaPickerWindow : Window
    {
        public string SelectedUrl { get; private set; }

        public MediaPickerWindow(List<MediaItem> items)
        {
            InitializeComponent();
            ImageList.ItemsSource = items;
        }

        private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = ImageList.SelectedItem as MediaItem;
            if (item == null) return;

            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;

            // XamlAnimatedGif handles GIF animation natively
            AnimationBehavior.SetSourceUri(PreviewImage, new Uri(item.FullUrl));
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            var item = ImageList.SelectedItem as MediaItem;
            if (item == null)
            {
                MessageBox.Show("Select an image first.", "PlayGif");
                return;
            }
            SelectedUrl = item.FullUrl;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
