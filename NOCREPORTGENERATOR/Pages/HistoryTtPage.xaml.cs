using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using NOCREPORTGENERATOR.Models;
using NOCREPORTGENERATOR.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using DataGrid = CommunityToolkit.WinUI.UI.Controls.DataGrid;
using DataGridTextColumn = CommunityToolkit.WinUI.UI.Controls.DataGridTextColumn;

namespace NOCREPORTGENERATOR.Pages
{
        public sealed partial class HistoryTtPage : Page
        {
            public ObservableCollection<HistoryTtItem> HistoryItems { get; } = new();
        private List<HistoryTtItem> _allHistoryItems = new();
        private readonly List<int> _pageSizeOptions = new() { 25, 50, 100, 250 };
        private const string AllStatusFilterOption = "Semua Status";
        private const string AllSegmentFilterOption = "Semua Segment";
        private const string AllSegmentPmFilterOption = "Semua Segment PM";
        private static readonly Regex SegmentIdPairRegex = new(@"\b\d{5,8}\s*[-/]\s*\d{5,8}\b", RegexOptions.Compiled);
        private static readonly Regex SegmentIdPairStrictRegex = new(@"^\s*(?<a>\d{5,8})\s*[-/]\s*(?<b>\d{5,8})\s*$", RegexOptions.Compiled);
        private int _currentPage = 1;
        private int _pageSize = 50;
        private int _totalPages = 1;
        private bool _viewOptionsInitialized;
        private bool _isImporting;
        private bool _isUpdatingAdvancedFilters;
        private CancellationTokenSource? _viewRefreshCts;
        private CancellationTokenSource? _searchDebounceCts;
        private List<string> _segmentFilterOptionsAll = new();
        private List<string> _segmentPmFilterOptionsAll = new();

        public HistoryTtPage()
        {
            InitializeComponent();
            EditableComboBoxContainsFilterHelper.Attach(
                SegmentFilterComboBox,
                () => _segmentFilterOptionsAll,
                AllSegmentFilterOption);
            EditableComboBoxContainsFilterHelper.Attach(
                SegmentPmFilterComboBox,
                () => _segmentPmFilterOptionsAll,
                AllSegmentPmFilterOption);
            Loaded += HistoryTtPage_Loaded;
        }

        private async void HistoryTtPage_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureViewOptionsInitialized();
            await LoadHistoryAsync();
        }

        public async Task RefreshFromShellAsync()
        {
            await LoadHistoryAsync();
        }

        public async Task ImportFromShellAsync()
        {
            if (_isImporting)
            {
                return;
            }

            try
            {
                _isImporting = true;
                SummaryTextBlock.Text = "Memilih file import...";

                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".xlsm");
                picker.FileTypeFilter.Add(".xlsb");
                picker.SuggestedStartLocation = PickerLocationId.Downloads;

                if (App.MainAppWindow is null)
                {
                    SummaryTextBlock.Text = "Window utama tidak tersedia.";
                    DeveloperDiagnostics.LogError("HistoryTtPage.ImportButton_Click.MainWindowNull", null);
                    return;
                }

                var hWnd = WindowNative.GetWindowHandle(App.MainAppWindow);
                InitializeWithWindow.Initialize(picker, hWnd);
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    await LoadHistoryAsync();
                    return;
                }

                SummaryTextBlock.Text = "Menyiapkan preview...";
                var preview = await TtTrackerImportService.BuildPreviewAsync(file.Path, 100);
                var shouldCommit = await ShowImportPreviewDialogAsync(preview, file.Name);
                if (!shouldCommit)
                {
                    SummaryTextBlock.Text = "Import dibatalkan.";
                    await LoadHistoryAsync();
                    return;
                }

                var token = ImportJobService.Start("Import TT dimulai...");
                var progress = new Progress<TtTrackerImportService.ImportProgressInfo>(p =>
                {
                    ImportJobService.Report(p.Percent, p.Message);
                });

                var importResult = await TtTrackerImportService.ImportAsync(file.Path, token, progress);
                ImportJobService.Complete("Import selesai");
                await LoadHistoryAsync();

                SummaryTextBlock.Text =
                    _allHistoryItems.Count.ToString(CultureInfo.InvariantCulture) + " data | " +
                    "Import: +" + importResult.Inserted.ToString(CultureInfo.InvariantCulture) +
                    ", existing skip " + importResult.SkippedExisting.ToString(CultureInfo.InvariantCulture) +
                    ", invalid skip " + (importResult.SkippedRows + importResult.StorageSkipped).ToString(CultureInfo.InvariantCulture);
            }
            catch (OperationCanceledException)
            {
                ImportJobService.Complete("Import dibatalkan pengguna.");
                SummaryTextBlock.Text = "Import dibatalkan.";
                await LoadHistoryAsync();
            }
            catch (Exception ex)
            {
                ImportJobService.Complete("Import gagal.");
                SummaryTextBlock.Text = "Import gagal. Cek log developer.";
                DeveloperDiagnostics.LogError("HistoryTtPage.ImportButton_Click", ex);
            }
            finally
            {
                _isImporting = false;
            }
        }

        private async Task<bool> ShowImportPreviewDialogAsync(
            TtTrackerImportService.ImportPreviewResult preview,
            string fileName)
        {
            var previewRows = preview.Rows
                .Take(100)
                .Select(HistoryTtPreviewItem.FromRecord)
                .ToList();

            var titleText = "Preview Import - " + fileName;
            var headerText =
                "Preview 100 row pertama dari " +
                preview.Rows.Count.ToString(CultureInfo.InvariantCulture) +
                " row sample valid.";

            var root = new StackPanel { Spacing = 10 };
            root.Children.Add(new TextBlock
            {
                Text = headerText,
                TextWrapping = TextWrapping.Wrap
            });

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserSortColumns = true,
                IsReadOnly = true,
                HeadersVisibility = CommunityToolkit.WinUI.UI.Controls.DataGridHeadersVisibility.Column,
                GridLinesVisibility = CommunityToolkit.WinUI.UI.Controls.DataGridGridLinesVisibility.Horizontal,
                MinHeight = 460,
                RowHeight = 40,
                ItemsSource = previewRows
            };

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "TT IOH",
                MinWidth = 210,
                Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(1.6, CommunityToolkit.WinUI.UI.Controls.DataGridLengthUnitType.Star),
                Binding = new Binding { Path = new PropertyPath(nameof(HistoryTtPreviewItem.TtIoh)) }
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Status Link",
                MinWidth = 120,
                Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(1.0, CommunityToolkit.WinUI.UI.Controls.DataGridLengthUnitType.Star),
                Binding = new Binding { Path = new PropertyPath(nameof(HistoryTtPreviewItem.StatusLink)) }
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Segment Route",
                MinWidth = 190,
                Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(1.7, CommunityToolkit.WinUI.UI.Controls.DataGridLengthUnitType.Star),
                Binding = new Binding { Path = new PropertyPath(nameof(HistoryTtPreviewItem.SegmentRoute)) }
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Root Cause",
                MinWidth = 180,
                Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(1.8, CommunityToolkit.WinUI.UI.Controls.DataGridLengthUnitType.Star),
                Binding = new Binding { Path = new PropertyPath(nameof(HistoryTtPreviewItem.RootCause)) }
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "CP Location",
                MinWidth = 160,
                Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(1.8, CommunityToolkit.WinUI.UI.Controls.DataGridLengthUnitType.Star),
                Binding = new Binding { Path = new PropertyPath(nameof(HistoryTtPreviewItem.CpLocation)) }
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Dispatch Time",
                MinWidth = 150,
                Width = new CommunityToolkit.WinUI.UI.Controls.DataGridLength(1.1, CommunityToolkit.WinUI.UI.Controls.DataGridLengthUnitType.Star),
                Binding = new Binding { Path = new PropertyPath(nameof(HistoryTtPreviewItem.DispatchTime)) }
            });

            root.Children.Add(grid);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = titleText,
                Content = root,
                PrimaryButtonText = "Commit Import",
                CloseButtonText = "Batal",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async void DeleteRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not HistoryTtItem item)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "Hapus data TT?",
                Content = "Data ini akan dihapus permanen dari penyimpanan lokal.",
                PrimaryButtonText = "Hapus",
                CloseButtonText = "Batal",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            await LocalFormStorageService.DeleteAsync(item.RecordId);
            await LoadHistoryAsync();
        }

        private void TtIohButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not HistoryTtItem item)
            {
                return;
            }

            Frame?.Navigate(typeof(TtDetailPage), item.RecordId);
        }

        private async Task LoadHistoryAsync()
        {
            try
            {
                var records = await LocalFormStorageService.GetAllAsync();
                var projected = await Task.Run(() =>
                    records.Select(HistoryTtItem.FromRecord).ToList());
                _allHistoryItems = projected;
                RefreshAdvancedFilterOptions();
                _currentPage = 1;
                await RefreshCurrentViewAsync();
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("HistoryTtPage.LoadHistoryAsync", ex);
            }
        }

        private void EnsureViewOptionsInitialized()
        {
            if (_viewOptionsInitialized)
            {
                return;
            }

            SortComboBox.ItemsSource = new[]
            {
                "Dispatch Terbaru",
                "Dispatch Terlama",
                "TT IOH A-Z",
                "TT IOH Z-A",
                "Status Link A-Z",
                "Status Link Z-A",
                "Segment Route A-Z",
                "Segment Route Z-A"
            };
            SortComboBox.SelectedIndex = 0;

            PageSizeComboBox.ItemsSource = _pageSizeOptions;
            PageSizeComboBox.SelectedItem = _pageSize;
            StatusFilterComboBox.ItemsSource = new[] { AllStatusFilterOption };
            StatusFilterComboBox.SelectedIndex = 0;
            SegmentFilterComboBox.ItemsSource = new[] { AllSegmentFilterOption };
            SegmentFilterComboBox.SelectedIndex = 0;
            SegmentPmFilterComboBox.ItemsSource = new[] { AllSegmentPmFilterOption };
            SegmentPmFilterComboBox.SelectedIndex = 0;
            _viewOptionsInitialized = true;
        }

        private void RefreshAdvancedFilterOptions()
        {
            _isUpdatingAdvancedFilters = true;
            try
            {
                var selectedStatus = StatusFilterComboBox.SelectedItem as string;
                var selectedSegment = GetSegmentFilterValue();
                var selectedSegmentPm = GetSegmentPmFilterValue();

                var statuses = _allHistoryItems
                    .Select(x => x.StatusLink)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "-", StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                statuses.Insert(0, AllStatusFilterOption);
                StatusFilterComboBox.ItemsSource = statuses;
                StatusFilterComboBox.SelectedItem = statuses.FirstOrDefault(x =>
                    string.Equals(x, selectedStatus, StringComparison.OrdinalIgnoreCase)) ?? AllStatusFilterOption;

                var segments = _allHistoryItems
                    .Select(x => x.SegmentRoute)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "-", StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                segments.Insert(0, AllSegmentFilterOption);
                _segmentFilterOptionsAll = segments.ToList();
                SegmentFilterComboBox.ItemsSource = segments;
                if (!string.IsNullOrWhiteSpace(selectedSegment) &&
                    segments.Any(x => string.Equals(x, selectedSegment, StringComparison.OrdinalIgnoreCase)))
                {
                    SegmentFilterComboBox.SelectedItem = segments.First(x =>
                        string.Equals(x, selectedSegment, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    SegmentFilterComboBox.SelectedIndex = 0;
                }

                var segmentPms = _allHistoryItems
                    .Select(x => x.SegmentPm)
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "-", StringComparison.Ordinal))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                segmentPms.Insert(0, AllSegmentPmFilterOption);
                _segmentPmFilterOptionsAll = segmentPms.ToList();
                SegmentPmFilterComboBox.ItemsSource = segmentPms;
                if (!string.IsNullOrWhiteSpace(selectedSegmentPm) &&
                    segmentPms.Any(x => string.Equals(x, selectedSegmentPm, StringComparison.OrdinalIgnoreCase)))
                {
                    SegmentPmFilterComboBox.SelectedItem = segmentPms.First(x =>
                        string.Equals(x, selectedSegmentPm, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    SegmentPmFilterComboBox.SelectedIndex = 0;
                }

                EditableComboBoxContainsFilterHelper.Refresh(SegmentFilterComboBox);
                EditableComboBoxContainsFilterHelper.Refresh(SegmentPmFilterComboBox);
            }
            finally
            {
                _isUpdatingAdvancedFilters = false;
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentPage = 1;
            DebounceRefreshCurrentView();
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchTextBox is not null && !string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                SearchTextBox.Text = string.Empty;
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewOptionsInitialized || _isUpdatingAdvancedFilters)
            {
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void SegmentFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewOptionsInitialized || _isUpdatingAdvancedFilters)
            {
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void SegmentPmFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewOptionsInitialized || _isUpdatingAdvancedFilters)
            {
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void DispatchFromDatePicker_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs e)
        {
            if (!_viewOptionsInitialized || _isUpdatingAdvancedFilters)
            {
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void DispatchToDatePicker_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs e)
        {
            if (!_viewOptionsInitialized || _isUpdatingAdvancedFilters)
            {
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void ResetAdvancedFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _isUpdatingAdvancedFilters = true;
            try
            {
                if (StatusFilterComboBox.Items.Count > 0)
                {
                    StatusFilterComboBox.SelectedIndex = 0;
                }

                if (SegmentFilterComboBox.Items.Count > 0)
                {
                    SegmentFilterComboBox.SelectedIndex = 0;
                }

                if (SegmentPmFilterComboBox.Items.Count > 0)
                {
                    SegmentPmFilterComboBox.SelectedIndex = 0;
                }

                if (DispatchFromDatePicker.Date.HasValue)
                {
                    DispatchFromDatePicker.Date = null;
                }

                if (DispatchToDatePicker.Date.HasValue)
                {
                    DispatchToDatePicker.Date = null;
                }
            }
            finally
            {
                _isUpdatingAdvancedFilters = false;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewOptionsInitialized)
            {
                return;
            }

            _currentPage = 1;
            RefreshCurrentView();
        }

        private void PageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewOptionsInitialized || PageSizeComboBox.SelectedItem is not int selected || selected <= 0)
            {
                return;
            }

            _pageSize = selected;
            _currentPage = 1;
            RefreshCurrentView();
        }

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage <= 1)
            {
                return;
            }

            _currentPage--;
            RefreshCurrentView();
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage >= _totalPages)
            {
                return;
            }

            _currentPage++;
            RefreshCurrentView();
        }

        private void PageNumberButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not int page)
            {
                return;
            }

            _currentPage = page;
            RefreshCurrentView();
        }

        private void RefreshCurrentView()
        {
            _ = RefreshCurrentViewAsync();
        }

        private void DebounceRefreshCurrentView(int delayMs = 180)
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _searchDebounceCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs);
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        _ = RefreshCurrentViewAsync();
                    });
                }
                finally
                {
                    if (ReferenceEquals(_searchDebounceCts, cts))
                    {
                        cts.Dispose();
                        _searchDebounceCts = null;
                    }
                }
            });
        }

        private async Task RefreshCurrentViewAsync()
        {
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _viewRefreshCts, cts);
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                catch
                {
                }
                finally
                {
                    previous.Dispose();
                }
            }

            try
            {
                var keyword = SearchTextBox?.Text?.Trim() ?? string.Empty;
                var statusFilter = GetStatusFilterValue();
                var segmentFilter = GetSegmentFilterValue();
                var segmentPmFilter = GetSegmentPmFilterValue();
                var fromDate = DispatchFromDatePicker?.Date?.Date;
                var toDate = DispatchToDatePicker?.Date?.Date;
                var sort = SortComboBox?.SelectedItem as string ?? "Dispatch Terbaru";
                var allSnapshot = _allHistoryItems.ToList();
                var currentPageSnapshot = _currentPage;
                var pageSizeSnapshot = _pageSize;
                var token = cts.Token;

                var prepared = await Task.Run(() =>
                    PrepareHistoryView(
                        allSnapshot,
                        keyword,
                        statusFilter,
                        segmentFilter,
                        segmentPmFilter,
                        fromDate,
                        toDate,
                        sort,
                        currentPageSnapshot,
                        pageSizeSnapshot,
                        token));

                if (prepared is null || token.IsCancellationRequested)
                {
                    return;
                }

                _currentPage = prepared.CurrentPage;
                _totalPages = prepared.TotalPages;
                HistoryItems.Clear();
                foreach (var item in prepared.PageItems)
                {
                    HistoryItems.Add(item);
                }

                SummaryTextBlock.Text =
                    prepared.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                    prepared.End.ToString(CultureInfo.InvariantCulture) + " dari " +
                    prepared.TotalFiltered.ToString(CultureInfo.InvariantCulture) +
                    " data (total: " + prepared.TotalAll.ToString(CultureInfo.InvariantCulture) + ")";

                KpiTotalTextBlock.Text = prepared.TotalAll.ToString(CultureInfo.InvariantCulture);
                KpiFilteredTextBlock.Text = prepared.TotalFiltered.ToString(CultureInfo.InvariantCulture);
                KpiOpenTextBlock.Text = prepared.TotalOpen.ToString(CultureInfo.InvariantCulture);
                KpiSegmentTextBlock.Text = prepared.DistinctSegmentCount.ToString(CultureInfo.InvariantCulture);

                PageInfoTextBlock.Text = "Page " + _currentPage.ToString(CultureInfo.InvariantCulture) + " / " + _totalPages.ToString(CultureInfo.InvariantCulture);
                PrevPageButton.IsEnabled = _currentPage > 1;
                NextPageButton.IsEnabled = _currentPage < _totalPages;
                RenderPageButtons();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_viewRefreshCts, cts))
                {
                    cts.Dispose();
                    _viewRefreshCts = null;
                }
                else
                {
                    cts.Dispose();
                }
            }
        }

        private static HistoryViewPreparedData? PrepareHistoryView(
            IReadOnlyList<HistoryTtItem> allItems,
            string keyword,
            string? statusFilter,
            string? segmentFilter,
            string? segmentPmFilter,
            DateTime? fromDate,
            DateTime? toDate,
            string sort,
            int currentPage,
            int pageSize,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            IEnumerable<HistoryTtItem> query = allItems;

            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                query = query.Where(x => ContainsInsensitive(x.StatusLink, statusFilter));
            }

            if (!string.IsNullOrWhiteSpace(segmentFilter))
            {
                query = query.Where(x => MatchesSegmentPairFilter(x.SegmentRoute, x.SegmentPm, segmentFilter));
            }

            if (!string.IsNullOrWhiteSpace(segmentPmFilter))
            {
                query = query.Where(x => MatchesSegmentPairFilter(x.SegmentPm, x.SegmentRoute, segmentPmFilter));
            }

            if (fromDate.HasValue || toDate.HasValue)
            {
                var dateStart = fromDate;
                var dateEnd = toDate;
                if (dateStart.HasValue && dateEnd.HasValue && dateStart.Value > dateEnd.Value)
                {
                    (dateStart, dateEnd) = (dateEnd, dateStart);
                }

                if (dateStart.HasValue)
                {
                    query = query.Where(x => x.DispatchDateTimeValue.Date >= dateStart.Value);
                }

                if (dateEnd.HasValue)
                {
                    query = query.Where(x => x.DispatchDateTimeValue.Date <= dateEnd.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(x =>
                    ContainsInsensitive(x.TtIoh, keyword) ||
                    ContainsInsensitive(x.StatusLink, keyword) ||
                    ContainsInsensitive(x.SegmentRoute, keyword) ||
                    ContainsInsensitive(x.SegmentPm, keyword) ||
                    ContainsInsensitive(x.RootCause, keyword) ||
                    ContainsInsensitive(x.CpLocation, keyword) ||
                    ContainsInsensitive(x.DispatchTime, keyword));
            }

            query = sort switch
            {
                "Dispatch Terlama" => query.OrderBy(x => x.DispatchDateTimeValue),
                "TT IOH A-Z" => query.OrderBy(x => x.TtIoh, StringComparer.OrdinalIgnoreCase),
                "TT IOH Z-A" => query.OrderByDescending(x => x.TtIoh, StringComparer.OrdinalIgnoreCase),
                "Status Link A-Z" => query.OrderBy(x => x.StatusLink, StringComparer.OrdinalIgnoreCase),
                "Status Link Z-A" => query.OrderByDescending(x => x.StatusLink, StringComparer.OrdinalIgnoreCase),
                "Segment Route A-Z" => query.OrderBy(x => x.SegmentRoute, StringComparer.OrdinalIgnoreCase),
                "Segment Route Z-A" => query.OrderByDescending(x => x.SegmentRoute, StringComparer.OrdinalIgnoreCase),
                _ => query.OrderByDescending(x => x.DispatchDateTimeValue)
            };

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var filtered = query.ToList();
            var totalFiltered = filtered.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalFiltered / (double)Math.Max(1, pageSize)));
            var normalizedCurrentPage = Math.Max(1, Math.Min(currentPage, totalPages));
            var skip = (normalizedCurrentPage - 1) * Math.Max(1, pageSize);
            var pageItems = filtered.Skip(skip).Take(Math.Max(1, pageSize)).ToList();
            var start = totalFiltered == 0 ? 0 : skip + 1;
            var end = skip + pageItems.Count;
            var totalOpen = allItems.Count(x => ContainsInsensitive(x.StatusLink, "open") || ContainsInsensitive(x.StatusLink, "down"));
            var distinctSegmentCount = allItems
                .Select(x =>
                {
                    var key = TryCanonicalSegmentPairKey(x.SegmentPm);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        return key;
                    }

                    return TryCanonicalSegmentPairKey(x.SegmentRoute);
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return new HistoryViewPreparedData(
                pageItems,
                allItems.Count,
                totalFiltered,
                totalPages,
                normalizedCurrentPage,
                start,
                end,
                totalOpen,
                distinctSegmentCount);
        }

        private void RenderPageButtons()
        {
            PageButtonsPanel.Children.Clear();
            if (_totalPages <= 0)
            {
                return;
            }

            var start = Math.Max(1, _currentPage - 2);
            var end = Math.Min(_totalPages, start + 4);
            start = Math.Max(1, end - 4);

            for (var p = start; p <= end; p++)
            {
                var pageButton = new Button
                {
                    Tag = p,
                    MinWidth = 40,
                    Content = p.ToString(CultureInfo.InvariantCulture),
                    Style = (Style)Application.Current.Resources["ShellSecondaryButtonStyle"]
                };

                if (p == _currentPage)
                {
                    pageButton.IsEnabled = false;
                }

                pageButton.Click += PageNumberButton_Click;
                PageButtonsPanel.Children.Add(pageButton);
            }
        }

        private string? GetStatusFilterValue()
        {
            if (StatusFilterComboBox?.SelectedItem is not string selected)
            {
                return null;
            }

            return string.Equals(selected, AllStatusFilterOption, StringComparison.OrdinalIgnoreCase)
                ? null
                : selected;
        }

        private string? GetSegmentFilterValue()
        {
            if (SegmentFilterComboBox is null)
            {
                return null;
            }

            if (SegmentFilterComboBox.SelectedItem is string selected &&
                !string.IsNullOrWhiteSpace(selected) &&
                !string.Equals(selected, AllSegmentFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return selected;
            }

            var typedText = SegmentFilterComboBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(typedText) ||
                string.Equals(typedText, AllSegmentFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return typedText;
        }

        private string? GetSegmentPmFilterValue()
        {
            if (SegmentPmFilterComboBox is null)
            {
                return null;
            }

            if (SegmentPmFilterComboBox.SelectedItem is string selected &&
                !string.IsNullOrWhiteSpace(selected) &&
                !string.Equals(selected, AllSegmentPmFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return selected;
            }

            var typedText = SegmentPmFilterComboBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(typedText) ||
                string.Equals(typedText, AllSegmentPmFilterOption, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return typedText;
        }


        private static bool MatchesSegmentPairFilter(string primaryValue, string secondaryValue, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            var filterKey = TryCanonicalSegmentPairKey(filter);
            if (string.IsNullOrWhiteSpace(filterKey))
            {
                return ContainsInsensitive(primaryValue, filter) || ContainsInsensitive(secondaryValue, filter);
            }

            return string.Equals(TryCanonicalSegmentPairKey(primaryValue), filterKey, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(TryCanonicalSegmentPairKey(secondaryValue), filterKey, StringComparison.OrdinalIgnoreCase);
        }

        private static string TryCanonicalSegmentPairKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "-", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var match = SegmentIdPairRegex.Match(value);
            if (!match.Success)
            {
                return string.Empty;
            }

            var strict = SegmentIdPairStrictRegex.Match(match.Value);
            if (!strict.Success)
            {
                return string.Empty;
            }

            var left = strict.Groups["a"].Value;
            var right = strict.Groups["b"].Value;
            return CompareNumericSegmentId(left, right) >= 0
                ? left + "|" + right
                : right + "|" + left;
        }

        private static int CompareNumericSegmentId(string left, string right)
        {
            if (left.Length != right.Length)
            {
                return left.Length.CompareTo(right.Length);
            }

            return string.Compare(left, right, StringComparison.Ordinal);
        }

        private static bool ContainsInsensitive(string source, string keyword)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(keyword))
            {
                return false;
            }

            return source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }


        private sealed class HistoryViewPreparedData
        {
            public HistoryViewPreparedData(
                List<HistoryTtItem> pageItems,
                int totalAll,
                int totalFiltered,
                int totalPages,
                int currentPage,
                int start,
                int end,
                int totalOpen,
                int distinctSegmentCount)
            {
                PageItems = pageItems;
                TotalAll = totalAll;
                TotalFiltered = totalFiltered;
                TotalPages = totalPages;
                CurrentPage = currentPage;
                Start = start;
                End = end;
                TotalOpen = totalOpen;
                DistinctSegmentCount = distinctSegmentCount;
            }

            public List<HistoryTtItem> PageItems { get; }
            public int TotalAll { get; }
            public int TotalFiltered { get; }
            public int TotalPages { get; }
            public int CurrentPage { get; }
            public int Start { get; }
            public int End { get; }
            public int TotalOpen { get; }
            public int DistinctSegmentCount { get; }
        }

        public sealed class HistoryTtItem
        {
            private static readonly Regex TtIohRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            public string RecordId { get; set; } = string.Empty;
            public string TtIoh { get; set; } = "-";
            public string StatusLink { get; set; } = "-";
            public string SegmentRoute { get; set; } = "-";
            public string SegmentPm { get; set; } = "-";
            public string RootCause { get; set; } = "-";
            public string CpLocation { get; set; } = "-";
            public string DispatchTime { get; set; } = "-";
            public DateTimeOffset DispatchDateTimeValue { get; set; }

            public static HistoryTtItem FromRecord(LocalFormRecord record)
            {
                return new HistoryTtItem
                {
                    RecordId = record.Id,
                    TtIoh = ResolveTtIoh(record),
                    StatusLink = Normalize(record.StatusLink),
                    SegmentRoute = Normalize(record.SegmentRoute),
                    SegmentPm = Normalize(record.SegmentPm),
                    RootCause = Normalize(record.RootCause),
                    CpLocation = Normalize(string.IsNullOrWhiteSpace(record.CutPoint) ? record.Coordinate : record.CutPoint),
                    DispatchDateTimeValue = record.DispatchDateTime,
                    DispatchTime = record.DispatchDateTime == default
                        ? "-"
                        : record.DispatchDateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)
                };
            }

            private static string Normalize(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            }

            private static string ResolveTtIoh(LocalFormRecord record)
            {
                var saved = Normalize(record.TtIoh);
                if (!string.Equals(saved, "-", StringComparison.Ordinal))
                {
                    return saved.ToUpperInvariant();
                }

                if (string.IsNullOrWhiteSpace(record.Title))
                {
                    return "-";
                }

                var match = TtIohRegex.Match(record.Title);
                return match.Success ? match.Value.ToUpperInvariant() : "-";
            }
        }

        private sealed class HistoryTtPreviewItem
        {
            public string TtIoh { get; set; } = "-";
            public string StatusLink { get; set; } = "-";
            public string SegmentRoute { get; set; } = "-";
            public string RootCause { get; set; } = "-";
            public string CpLocation { get; set; } = "-";
            public string DispatchTime { get; set; } = "-";

            public static HistoryTtPreviewItem FromRecord(LocalFormRecord record)
            {
                return new HistoryTtPreviewItem
                {
                    TtIoh = Normalize(record.TtIoh),
                    StatusLink = Normalize(record.StatusLink),
                    SegmentRoute = Normalize(record.SegmentRoute),
                    RootCause = Normalize(record.RootCause),
                    CpLocation = Normalize(string.IsNullOrWhiteSpace(record.CutPoint) ? record.Coordinate : record.CutPoint),
                    DispatchTime = record.DispatchDateTime == default
                        ? "-"
                        : record.DispatchDateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)
                };
            }

            private static string Normalize(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            }
        }
    }
}
