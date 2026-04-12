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
