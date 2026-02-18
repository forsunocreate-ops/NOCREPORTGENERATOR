using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NOCREPORTGENERATOR.Models;
using NOCREPORTGENERATOR.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class TtDetailPage : Page
    {
        private static readonly Regex TtIohRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private string _recordId = string.Empty;
        private string _coordinate = string.Empty;

        public TtDetailPage()
        {
            InitializeComponent();
            Loaded += TtDetailPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string id)
            {
                _recordId = id;
            }
        }

        private async void TtDetailPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadDetailAsync();
        }

        private async Task LoadDetailAsync()
        {
            try
            {
                var records = await LocalFormStorageService.GetAllAsync();
                var record = records.FirstOrDefault(x => string.Equals(x.Id, _recordId, StringComparison.OrdinalIgnoreCase));
                if (record is null)
                {
                    HeaderSubTitleTextBlock.Text = "Data tidak ditemukan.";
                    return;
                }

                var ttIoh = ResolveTtIoh(record);
                HeaderSubTitleTextBlock.Text = ttIoh;
                TtIohTextBlock.Text = ttIoh;
                TitleTextBlock.Text = Normalize(record.Title);
                OccurTimeTextBlock.Text = FormatDate(record.OccurDateTime);
                DispatchTimeTextBlock.Text = FormatDate(record.DispatchDateTime);
                var finish = record.FinishDateTime == default ? record.DispatchDateTime : record.FinishDateTime;
                FinishTimeTextBlock.Text = FormatDate(finish);
                MttrTextBlock.Text = FormatMttr(record.DispatchDateTime, finish);
                PicTextBlock.Text = Normalize(record.Pic);
                StatusLinkTextBlock.Text = Normalize(record.StatusLink);
                SegmentRouteTextBlock.Text = Normalize(record.SegmentRoute);
                var segmentPmValue = Normalize(record.SegmentPm);
                SegmentPmTextBlock.Text = "Segment PM: " + segmentPmValue;
                SegmentPmManualHintTextBlock.Visibility = string.Equals(segmentPmValue, "-", StringComparison.Ordinal)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SystemKeyTextBlock.Text = Normalize(record.SystemKey);
                RootCauseTextBlock.Text = Normalize(record.RootCause);
                CutPointTextBlock.Text = Normalize(record.CutPoint);
                _coordinate = Normalize(record.Coordinate);
                CoordinateTextBlock.Text = _coordinate;
                OpenLiveMapButton.IsEnabled = CoordinateFormatService.TryParseAnyToDecimal(_coordinate, out _, out _);
                SavedAtTextBlock.Text = FormatDate(record.SavedAt);
                DraftNameTextBlock.Text = Normalize(record.Name);
                UpdateProgressTextBox.Text = Normalize(record.UpdateProgress);
            }
            catch (Exception ex)
            {
                HeaderSubTitleTextBlock.Text = "Gagal membuka detail TT.";
                DeveloperDiagnostics.LogError("TtDetailPage.LoadDetailAsync", ex);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Frame.GoBack();
                return;
            }

            if (App.MainAppWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToModule("history-tt");
            }
        }

        private void OpenLiveMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CoordinateFormatService.TryParseAnyToDecimal(_coordinate, out var lat, out var lon))
            {
                return;
            }

            PendingMapFocusService.Set(lat, lon, TtIohTextBlock.Text);
            if (App.MainAppWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToModule("live-map");
                return;
            }

            Frame?.Navigate(typeof(LiveMapPage));
        }

        private static string ResolveTtIoh(LocalFormRecord record)
        {
            var fromSaved = Normalize(record.TtIoh);
            if (!string.Equals(fromSaved, "-", StringComparison.Ordinal))
            {
                return fromSaved.ToUpperInvariant();
            }

            if (string.IsNullOrWhiteSpace(record.Title))
            {
                return "-";
            }

            var match = TtIohRegex.Match(record.Title);
            return match.Success ? match.Value.ToUpperInvariant() : "-";
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
        }

        private static string FormatDate(DateTimeOffset value)
        {
            return value == default
                ? "-"
                : value.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        private static string FormatMttr(DateTimeOffset dispatch, DateTimeOffset finish)
        {
            if (dispatch == default || finish == default || finish < dispatch)
            {
                return "-";
            }

            var duration = finish - dispatch;
            var days = (int)duration.TotalDays;
            var hours = duration.Hours;
            var minutes = duration.Minutes;

            return days > 0
                ? days.ToString(CultureInfo.InvariantCulture) + "d " +
                  hours.ToString(CultureInfo.InvariantCulture) + "h " +
                  minutes.ToString(CultureInfo.InvariantCulture) + "m"
                : ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " +
                  minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }
    }
}
