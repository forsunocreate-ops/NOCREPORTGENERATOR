using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Services;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            GeminiApiKeyPasswordBox.Password = AppSettingsService.GetGeminiApiKey();
            StatusTextBlock.Text = "API key saat ini " + (string.IsNullOrWhiteSpace(GeminiApiKeyPasswordBox.Password) ? "belum diisi." : "sudah terisi.");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsService.SetGeminiApiKey(GeminiApiKeyPasswordBox.Password);
            StatusTextBlock.Text = "API key Gemini tersimpan.";
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            GeminiApiKeyPasswordBox.Password = string.Empty;
            AppSettingsService.SetGeminiApiKey(string.Empty);
            StatusTextBlock.Text = "API key Gemini dihapus.";
        }

        private async void SynchronizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SynchronizeButton.IsEnabled = false;
                SyncStatusTextBlock.Text = "Memulai sinkronisasi...";
                var token = ImportJobService.Start("Sinkronisasi data dimulai...");
                var progress = new Progress<DataSynchronizationService.SyncProgressInfo>(info =>
                {
                    SyncStatusTextBlock.Text = info.Message;
                    ImportJobService.Report(info.Percent, info.Message);
                });

                var result = await DataSynchronizationService.SynchronizeAllAsync(progress, token);
                SyncStatusTextBlock.Text =
                    "Selesai " + result.CompletedAt.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) +
                    " | link=" + result.SegmentPmLinkCount.ToString(CultureInfo.InvariantCulture) +
                    ", importDBTT=" + result.ImportedFromDatabaseTt.ToString(CultureInfo.InvariantCulture) +
                    ", mapTT=" + result.SegmentPmMappedTtCount.ToString(CultureInfo.InvariantCulture) +
                    ", updateHistory=" + result.UpdatedHistoryRows.ToString(CultureInfo.InvariantCulture);
                ImportJobService.Complete("Sinkronisasi selesai");
            }
            catch (OperationCanceledException)
            {
                SyncStatusTextBlock.Text = "Sinkronisasi dibatalkan.";
                ImportJobService.Complete("Sinkronisasi dibatalkan.");
            }
            catch (Exception ex)
            {
                SyncStatusTextBlock.Text = "Sinkronisasi gagal. Cek developer log.";
                ImportJobService.Complete("Sinkronisasi gagal.");
                DeveloperDiagnostics.LogError("SettingsPage.SynchronizeButton_Click", ex);
            }
            finally
            {
                SynchronizeButton.IsEnabled = true;
            }
        }
    }
}
