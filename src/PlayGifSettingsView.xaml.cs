using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace PlayGif
{
    public partial class PlayGifSettingsView : UserControl
    {
        public PlayGifSettingsView()
        {
            InitializeComponent();
        }

        private void BulkFetchButton_Click(object sender, RoutedEventArgs e)
        {
            // Get the plugin instance via the ViewModel's reference
            var vm = DataContext as PlayGifSettingsViewModel;
            if (vm == null) return;

            vm.BulkFetchSteamDescriptions();
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as PlayGifSettingsViewModel)?.RunLayoutReport();
        }

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as PlayGifSettingsViewModel)?.OpenLogFolder();
        }
    }
}
