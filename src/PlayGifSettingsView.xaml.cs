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

        private void ConvertToMp4Button_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as PlayGifSettingsViewModel)?.ConvertAllToMp4();
        }

        private void RepairLinksButton_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as PlayGifSettingsViewModel)?.RepairDescriptionLinks();
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as PlayGifSettingsViewModel)?.RunLayoutReport();
        }

        private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            (DataContext as PlayGifSettingsViewModel)?.OpenLogFolder();
        }

        private void ReportIssueButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(Common.Constants.IssuesUrl);
        }

        private void ProjectPageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(Common.Constants.ProjectUrl);
        }

        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch (Exception ex)
            {
                LogManager.GetLogger().Error(ex, $"Failed to open {url}");
            }
        }
    }
}
