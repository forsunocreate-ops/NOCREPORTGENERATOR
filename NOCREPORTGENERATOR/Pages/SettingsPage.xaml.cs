using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Services;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

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
                if (App.MainAppWindow is null)
                {
                    SyncStatusTextBlock.Text = "Window utama tidak tersedia.";
                    return;
                }

                SynchronizeButton.IsEnabled = false;
                SyncStatusTextBlock.Text = "Pilih file DATABASE_LINK...";

                var databaseLinkFile = await PickFileAsync(
                    "Pilih file DATABASE_LINK",
                    ".xlsb", ".xlsx", ".xlsm");
                if (databaseLinkFile is null)
                {
                    SyncStatusTextBlock.Text = "Sinkronisasi dibatalkan.";
                    return;
                }

                SyncStatusTextBlock.Text = "Pilih file SEGMENTPM...";
                var segmentPmFile = await PickFileAsync(
                    "Pilih file SEGMENTPM",
                    ".xlsx", ".xlsm", ".xlsb");
                if (segmentPmFile is null)
                {
                    SyncStatusTextBlock.Text = "Sinkronisasi dibatalkan.";
                    return;
                }

                SyncStatusTextBlock.Text = "Pilih file DATABASE_TT...";
                var databaseTtFile = await PickFileAsync(
                    "Pilih file DATABASE_TT",
                    ".xlsx", ".xlsm", ".xlsb");
                if (databaseTtFile is null)
                {
                    SyncStatusTextBlock.Text = "Sinkronisasi dibatalkan.";
                    return;
                }

                SyncStatusTextBlock.Text = "Memulai sinkronisasi...";
                var token = ImportJobService.Start("Sinkronisasi data dimulai...");
                var progress = new Progress<DataSynchronizationService.SyncProgressInfo>(info =>
                {
                    SyncStatusTextBlock.Text = info.Message;
                    ImportJobService.Report(info.Percent, info.Message);
                });

                var result = await DataSynchronizationService.SynchronizeAllAsync(
                    databaseLinkFile.Path,
                    segmentPmFile.Path,
                    databaseTtFile.Path,
                    progress,
                    token);
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

        private async Task<Windows.Storage.StorageFile?> PickFileAsync(string commitButtonText, params string[] extensions)
        {
            if (App.MainAppWindow is null)
            {
                return null;
            }

            var picker = new FileOpenPicker
            {
                CommitButtonText = commitButtonText,
                SuggestedStartLocation = PickerLocationId.Downloads
            };

            foreach (var extension in extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            return await picker.PickSingleFileAsync();
        }

        private async void ImportTtTrackerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainAppWindow is null)
                {
                    ImportStatusTextBlock.Text = "Window utama tidak tersedia.";
                    return;
                }

                SetImportButtonsEnabled(false);
                ImportStatusTextBlock.Text = "Pilih file TT Tracker...";

                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".xlsm");
                picker.FileTypeFilter.Add(".xlsb");
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));

                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    ImportStatusTextBlock.Text = "Import TT dibatalkan.";
                    return;
                }

                var token = ImportJobService.Start("Import TT dimulai...");
                var progress = new Progress<TtTrackerImportService.ImportProgressInfo>(p =>
                {
                    ImportStatusTextBlock.Text = p.Message;
                    ImportJobService.Report(p.Percent, p.Message);
                });

                var result = await TtTrackerImportService.ImportAsync(file.Path, token, progress);
                ImportJobService.Complete("Import TT selesai");
                ImportStatusTextBlock.Text =
                    "Import TT selesai | +" + result.Inserted.ToString(CultureInfo.InvariantCulture) +
                    ", update " + result.Updated.ToString(CultureInfo.InvariantCulture) +
                    ", existing skip " + result.SkippedExisting.ToString(CultureInfo.InvariantCulture) +
                    ", invalid skip " + (result.SkippedRows + result.StorageSkipped).ToString(CultureInfo.InvariantCulture);
            }
            catch (OperationCanceledException)
            {
                ImportJobService.Complete("Import TT dibatalkan.");
                ImportStatusTextBlock.Text = "Import TT dibatalkan.";
            }
            catch (Exception ex)
            {
                ImportJobService.Complete("Import TT gagal.");
                ImportStatusTextBlock.Text = "Import TT gagal. Cek developer log.";
                DeveloperDiagnostics.LogError("SettingsPage.ImportTtTrackerButton_Click", ex);
            }
            finally
            {
                SetImportButtonsEnabled(true);
            }
        }

        private async void ImportSegmentPmButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainAppWindow is null)
                {
                    ImportStatusTextBlock.Text = "Window utama tidak tersedia.";
                    return;
                }

                SetImportButtonsEnabled(false);
                ImportStatusTextBlock.Text = "Pilih file SEGMENTPM...";

                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".xlsm");
                picker.FileTypeFilter.Add(".xlsb");
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));

                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    ImportStatusTextBlock.Text = "Import SEGMENTPM dibatalkan.";
                    return;
                }

                var token = ImportJobService.Start("Import SEGMENTPM dimulai...");
                var result = await SegmentPmMapService.ImportFromWorkbookAsync(filePath: file.Path, password: "no", cancellationToken: token);
                AppSettingsService.SetSegmentPmSourcePath(file.Path);
                ImportJobService.Complete("Import SEGMENTPM selesai");
                ImportStatusTextBlock.Text =
                    "Import SEGMENTPM selesai | link=" + result.InsertedLinks.ToString(CultureInfo.InvariantCulture) +
                    ", rows=" + result.ProcessedRows.ToString(CultureInfo.InvariantCulture);
            }
            catch (OperationCanceledException)
            {
                ImportJobService.Complete("Import SEGMENTPM dibatalkan.");
                ImportStatusTextBlock.Text = "Import SEGMENTPM dibatalkan.";
            }
            catch (Exception ex)
            {
                ImportJobService.Complete("Import SEGMENTPM gagal.");
                ImportStatusTextBlock.Text = "Import SEGMENTPM gagal. Cek developer log.";
                DeveloperDiagnostics.LogError("SettingsPage.ImportSegmentPmButton_Click", ex);
            }
            finally
            {
                SetImportButtonsEnabled(true);
            }
        }

        private async void ImportSegmentRouteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.MainAppWindow is null)
                {
                    ImportStatusTextBlock.Text = "Window utama tidak tersedia.";
                    return;
                }

                SetImportButtonsEnabled(false);
                ImportStatusTextBlock.Text = "Pilih file DATABASE_LINK...";

                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".xlsb");
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".xlsm");
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));

                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    ImportStatusTextBlock.Text = "Import Segment Route dibatalkan.";
                    return;
                }

                var token = ImportJobService.Start("Import Segment Route dimulai...");
                ImportJobService.Report(25, "Membangun cache Segment Route...");
                await DatabaseLinkLookupService.RefreshCacheFromSourceAsync(file.Path);
                token.ThrowIfCancellationRequested();
                ImportJobService.Complete("Import Segment Route selesai");
                ImportStatusTextBlock.Text = "Import Segment Route selesai dari file: " + file.Name;
            }
            catch (OperationCanceledException)
            {
                ImportJobService.Complete("Import Segment Route dibatalkan.");
                ImportStatusTextBlock.Text = "Import Segment Route dibatalkan.";
            }
            catch (Exception ex)
            {
                ImportJobService.Complete("Import Segment Route gagal.");
                ImportStatusTextBlock.Text = "Import Segment Route gagal. Cek developer log.";
                DeveloperDiagnostics.LogError("SettingsPage.ImportSegmentRouteButton_Click", ex);
            }
            finally
            {
                SetImportButtonsEnabled(true);
            }
        }

        private void SetImportButtonsEnabled(bool enabled)
        {
            ImportTtTrackerButton.IsEnabled = enabled;
            ImportSegmentPmButton.IsEnabled = enabled;
            ImportSegmentRouteButton.IsEnabled = enabled;
        }
    }
}
