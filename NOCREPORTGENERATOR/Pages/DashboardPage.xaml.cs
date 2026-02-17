using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using FluentSymbol = FluentIcons.Common.Symbol;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Models;
using NOCREPORTGENERATOR.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Navigation;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class DashboardPage : Page, INotifyPropertyChanged
    {
        private const int MaxHistoryListItems = 1000;
        private readonly List<LocalFormRecord> _allRecords = new();
        private readonly List<DashboardHistoryItem> _filteredItems = new();
        private DashboardHistoryItem? _selectedItem;
        private static readonly Regex TicketTitleRegex = new(@"\[(?<status>[^-\]]+)\s*-\s*(?<severity>[^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IncNumberRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private ISeries[] _dailyTrendSeries = Array.Empty<ISeries>();
        private Axis[] _dailyTrendXAxes = Array.Empty<Axis>();
        private Axis[] _dailyTrendYAxes = Array.Empty<Axis>();
        private ISeries[] _linkStatusSeries = Array.Empty<ISeries>();
        private ISeries[] _slaRiskSeries = Array.Empty<ISeries>();
        private ISeries[] _picWorkloadSeries = Array.Empty<ISeries>();
        private Axis[] _picWorkloadXAxes = Array.Empty<Axis>();
        private Axis[] _picWorkloadYAxes = Array.Empty<Axis>();
        private ISeries[] _rootCauseSeries = Array.Empty<ISeries>();
        private Axis[] _rootCauseXAxes = Array.Empty<Axis>();
        private Axis[] _rootCauseYAxes = Array.Empty<Axis>();
        private bool _isDashboardInitialized;
        private bool _isStorageChangeSubscribed;
        private bool _isImportStateSubscribed;
        private bool _isRefreshingDashboard;
        private bool _hasPendingDashboardRefresh;
        private CancellationTokenSource? _filterRefreshCts;
        private CancellationTokenSource? _searchDebounceCts;
        private ImportJobState _lastImportState = ImportJobState.Inactive;

        public DashboardPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            DataContext = this;
            Loaded += DashboardPage_Loaded;
            InitializeChartDefaults();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ISeries[] DailyTrendSeries
        {
            get => _dailyTrendSeries;
            private set
            {
                _dailyTrendSeries = value;
                OnPropertyChanged();
            }
        }

        public Axis[] DailyTrendXAxes
        {
            get => _dailyTrendXAxes;
            private set
            {
                _dailyTrendXAxes = value;
                OnPropertyChanged();
            }
        }

        public Axis[] DailyTrendYAxes
        {
            get => _dailyTrendYAxes;
            private set
            {
                _dailyTrendYAxes = value;
                OnPropertyChanged();
            }
        }

        public ISeries[] LinkStatusSeries
        {
            get => _linkStatusSeries;
            private set
            {
                _linkStatusSeries = value;
                OnPropertyChanged();
            }
        }

        public ISeries[] SlaRiskSeries
        {
            get => _slaRiskSeries;
            private set
            {
                _slaRiskSeries = value;
                OnPropertyChanged();
            }
        }

        public ISeries[] PicWorkloadSeries
        {
            get => _picWorkloadSeries;
            private set
            {
                _picWorkloadSeries = value;
                OnPropertyChanged();
            }
        }

        public Axis[] PicWorkloadXAxes
        {
            get => _picWorkloadXAxes;
            private set
            {
                _picWorkloadXAxes = value;
                OnPropertyChanged();
            }
        }

        public Axis[] PicWorkloadYAxes
        {
            get => _picWorkloadYAxes;
            private set
            {
                _picWorkloadYAxes = value;
                OnPropertyChanged();
            }
        }

        public ISeries[] RootCauseSeries
        {
            get => _rootCauseSeries;
            private set
            {
                _rootCauseSeries = value;
                OnPropertyChanged();
            }
        }

        public Axis[] RootCauseXAxes
        {
            get => _rootCauseXAxes;
            private set
            {
                _rootCauseXAxes = value;
                OnPropertyChanged();
            }
        }

        public Axis[] RootCauseYAxes
        {
            get => _rootCauseYAxes;
            private set
            {
                _rootCauseYAxes = value;
                OnPropertyChanged();
            }
        }

        private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeToStorageChange();
            SubscribeToImportStateChange();

            if (_isDashboardInitialized)
            {
                return;
            }

            TryInitializeWin2DHeroVisual();
            InitializeFilters();
            await RefreshDashboardAsync();
            _isDashboardInitialized = true;
        }

        private void InitializeChartDefaults()
        {
            DailyTrendXAxes = new[]
            {
                new Axis
                {
                    LabelsRotation = 0,
                    TextSize = 12
                }
            };

            DailyTrendYAxes = new[]
            {
                new Axis
                {
                    MinLimit = 0,
                    TextSize = 12
                }
            };

            PicWorkloadXAxes = new[] { new Axis { LabelsRotation = 0, TextSize = 11, MinStep = 1 } };
            PicWorkloadYAxes = new[] { new Axis { MinLimit = 0, TextSize = 11 } };
            RootCauseXAxes = new[] { new Axis { LabelsRotation = 0, TextSize = 11, MinStep = 1 } };
            RootCauseYAxes = new[] { new Axis { MinLimit = 0, TextSize = 11 } };
            ApplyImportStateToUi(_lastImportState);
        }

        private void InitializeFilters()
        {
            StatusFilterComboBox.ItemsSource = new[]
            {
                "Semua Status",
                "Open",
                "Closed",
                "Cancel",
                "Open (Critical/Major)",
                "Open Critical",
                "Open Major"
            };
            StatusFilterComboBox.SelectedIndex = 0;

            DateFilterComboBox.ItemsSource = new[]
            {
                "Semua Waktu",
                "Hari Ini",
                "7 Hari Terakhir",
                "30 Hari Terakhir"
            };
            DateFilterComboBox.SelectedIndex = 0;
        }

        private async Task RefreshDashboardAsync()
        {
            if (_isRefreshingDashboard)
            {
                _hasPendingDashboardRefresh = true;
                return;
            }

            _isRefreshingDashboard = true;
            try
            {
                do
                {
                    _hasPendingDashboardRefresh = false;
                    var records = await LocalFormStorageService.GetAllAsync();
                    _allRecords.Clear();
                    _allRecords.AddRange(records);

                    UpdateHeadlineStats();
                    UpdateDataQuality(_allRecords);
                    await ApplyFiltersAsync();
                }
                while (_hasPendingDashboardRefresh);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("DashboardPage.RefreshDashboardAsync", ex);
            }
            finally
            {
                _isRefreshingDashboard = false;
            }
        }

        private void UpdateHeadlineStats()
        {
            var today = DateTime.Today;
            var totalToday = _allRecords.Count(x => x.OccurDateTime.LocalDateTime.Date == today);
            TotalTodayTextBlock.Text = totalToday.ToString(CultureInfo.InvariantCulture);
            DraftLocalTextBlock.Text = _allRecords.Count.ToString(CultureInfo.InvariantCulture);
            var lastSaved = _allRecords.Count == 0 ? (DateTimeOffset?)null : _allRecords.Max(x => x.SavedAt);
            DataFreshnessTextBlock.Text = lastSaved.HasValue
                ? "Data freshness: " + lastSaved.Value.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)
                : "Data freshness: -";
        }

        private void RequestFilterRefresh(bool debounce)
        {
            if (debounce)
            {
                DebounceApplyFilters();
                return;
            }

            _ = ApplyFiltersAsync();
        }

        private void DebounceApplyFilters(int delayMs = 180)
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
                        _ = ApplyFiltersAsync();
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

        private async Task ApplyFiltersAsync()
        {
            var cts = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _filterRefreshCts, cts);
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
                var token = cts.Token;
                var query = SearchTextBox.Text?.Trim() ?? string.Empty;
                var statusFilter = StatusFilterComboBox.SelectedItem as string ?? "Semua Status";
                var dateFilter = DateFilterComboBox.SelectedItem as string ?? "Semua Waktu";
                var selectedRecordId = _selectedItem?.Record.Id ?? string.Empty;
                var recordsSnapshot = _allRecords.ToList();

                var prepared = await Task.Run(
                    () => PrepareDashboardData(
                        recordsSnapshot,
                        query,
                        statusFilter,
                        dateFilter,
                        selectedRecordId,
                        token));

                if (prepared is null || token.IsCancellationRequested)
                {
                    return;
                }

                ApplyOperationalMetrics(prepared.OperationalMetrics);
                ApplyCharts(prepared.Chart);
                TopSegmentListView.ItemsSource = prepared.TopSegments;
                PriorityQueueListView.ItemsSource = prepared.PriorityQueue;

                _filteredItems.Clear();
                _filteredItems.AddRange(prepared.HistoryItems);
                HistoryListView.ItemsSource = null;
                HistoryListView.ItemsSource = _filteredItems;
                HistoryCountTextBlock.Text = prepared.HistoryCountText;

                if (_filteredItems.Count == 0)
                {
                    SelectItem(null);
                    return;
                }

                DashboardHistoryItem? selected = null;
                if (!string.IsNullOrWhiteSpace(prepared.SelectedRecordId))
                {
                    selected = _filteredItems.FirstOrDefault(x =>
                        x.Record.Id.Equals(prepared.SelectedRecordId, StringComparison.OrdinalIgnoreCase));
                }

                selected ??= _filteredItems[0];
                SelectItem(selected);
                HistoryListView.SelectedItem = selected;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("DashboardPage.ApplyFiltersAsync", ex);
            }
            finally
            {
                if (ReferenceEquals(_filterRefreshCts, cts))
                {
                    cts.Dispose();
                    _filterRefreshCts = null;
                }
                else
                {
                    cts.Dispose();
                }
            }
        }

        private static IEnumerable<LocalFormRecord> ApplyStatusFilter(IEnumerable<LocalFormRecord> data, string statusFilter)
        {
            return statusFilter switch
            {
                "Open" => data.Where(x => GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase)),
                "Closed" => data.Where(x => GetRecordStatus(x).Equals("closed", StringComparison.OrdinalIgnoreCase)),
                "Cancel" => data.Where(x => GetRecordStatus(x).Equals("cancel", StringComparison.OrdinalIgnoreCase)),
                "Open (Critical/Major)" => data.Where(x =>
                {
                    var severity = GetSeverity(x);
                    return GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase) &&
                           (severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                            severity.Equals("major", StringComparison.OrdinalIgnoreCase));
                }),
                "Open Critical" => data.Where(x =>
                {
                    var severity = GetSeverity(x);
                    return GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase) &&
                           severity.Equals("critical", StringComparison.OrdinalIgnoreCase);
                }),
                "Open Major" => data.Where(x =>
                {
                    var severity = GetSeverity(x);
                    return GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase) &&
                           severity.Equals("major", StringComparison.OrdinalIgnoreCase);
                }),
                _ => data
            };
        }

        private static IEnumerable<LocalFormRecord> ApplyDateFilter(IEnumerable<LocalFormRecord> data, string dateFilter)
        {
            var today = DateTime.Today;
            return dateFilter switch
            {
                "Hari Ini" => data.Where(x => x.OccurDateTime.LocalDateTime.Date == today),
                "7 Hari Terakhir" => data.Where(x => x.OccurDateTime.LocalDateTime.Date >= today.AddDays(-7)),
                "30 Hari Terakhir" => data.Where(x => x.OccurDateTime.LocalDateTime.Date >= today.AddDays(-30)),
                _ => data
            };
        }

        private static DashboardPreparedData? PrepareDashboardData(
            IReadOnlyList<LocalFormRecord> allRecords,
            string query,
            string statusFilter,
            string dateFilter,
            string selectedRecordId,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            IEnumerable<LocalFormRecord> filtered = allRecords;
            filtered = ApplyStatusFilter(filtered, statusFilter);
            filtered = ApplyDateFilter(filtered, dateFilter);

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(x =>
                    ContainsInsensitive(x.Title, query) ||
                    ContainsInsensitive(x.Name, query) ||
                    ContainsInsensitive(x.SegmentRoute, query) ||
                    ContainsInsensitive(x.SystemKey, query) ||
                    ContainsInsensitive(x.Pic, query) ||
                    ContainsInsensitive(GetIncNumber(x.Title), query));
            }

            var filteredRecords = filtered.ToList();
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var now = DateTimeOffset.Now;
            var operationalMetrics = BuildOperationalMetricsSnapshot(filteredRecords, now);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var chart = BuildChartSnapshot(filteredRecords, now);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var insights = BuildInsightSnapshot(filteredRecords, now);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var historyItems = filteredRecords
                .Take(MaxHistoryListItems)
                .Select(ToHistoryItem)
                .ToList();

            var historyCountText = filteredRecords.Count > MaxHistoryListItems
                ? historyItems.Count + " / " + filteredRecords.Count + " item"
                : historyItems.Count + " item";

            var selectedId = string.IsNullOrWhiteSpace(selectedRecordId) && historyItems.Count > 0
                ? historyItems[0].Record.Id
                : selectedRecordId;

            return new DashboardPreparedData(
                operationalMetrics,
                chart,
                insights.TopSegments,
                insights.PriorityQueue,
                historyItems,
                historyCountText,
                selectedId);
        }

        private static DashboardOperationalMetricsSnapshot BuildOperationalMetricsSnapshot(
            IReadOnlyList<LocalFormRecord> records,
            DateTimeOffset now)
        {
            var openAll = 0;
            var openCritical = 0;
            var openMajor = 0;
            var openMinor = 0;
            var closed = 0;
            var cancel = 0;
            var aging24h = 0;
            var aging72h = 0;

            foreach (var record in records)
            {
                var status = GetRecordStatus(record);
                var severity = GetSeverity(record);

                if (status.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    openAll++;
                    if (severity.Equals("critical", StringComparison.OrdinalIgnoreCase))
                    {
                        openCritical++;
                    }
                    else if (severity.Equals("major", StringComparison.OrdinalIgnoreCase))
                    {
                        openMajor++;
                    }
                    else
                    {
                        openMinor++;
                    }

                    var ageHours = Math.Max(0, (now - record.OccurDateTime).TotalHours);
                    if (ageHours >= 24)
                    {
                        aging24h++;
                    }

                    if (ageHours >= 72)
                    {
                        aging72h++;
                    }
                }
                else if (status.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    closed++;
                }
                else if (status.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    cancel++;
                }
            }

            var mttaMinutes = records
                .Select(x => (x.DispatchDateTime - x.OccurDateTime).TotalMinutes)
                .Where(x => x >= 0)
                .DefaultIfEmpty(0)
                .Average();

            var mttrMinutes = records
                .Select(x => (x.FinishDateTime == default ? x.DispatchDateTime : x.FinishDateTime) - x.DispatchDateTime)
                .Select(x => x.TotalMinutes)
                .Where(x => x >= 0)
                .DefaultIfEmpty(0)
                .Average();

            var closedWithinSla = 0;
            var closedSample = 0;
            foreach (var record in records.Where(x => GetRecordStatus(x).Equals("closed", StringComparison.OrdinalIgnoreCase)))
            {
                var threshold = GetSlaThresholdHours(GetSeverity(record));
                var finish = record.FinishDateTime == default ? record.DispatchDateTime : record.FinishDateTime;
                var durationHours = (finish - record.OccurDateTime).TotalHours;
                if (durationHours >= 0)
                {
                    closedSample++;
                    if (durationHours <= threshold)
                    {
                        closedWithinSla++;
                    }
                }
            }

            var rootCausePool = records
                .Select(x => NormalizeRootCause(x.RootCause))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            var repeated = rootCausePool
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Count() > 1)
                .Sum(x => x.Count());
            var repeatRate = rootCausePool.Count == 0 ? 0 : repeated * 100d / rootCausePool.Count;

            var last7Days = Enumerable.Range(0, 7)
                .Select(i => now.LocalDateTime.Date.AddDays(-i))
                .OrderBy(x => x)
                .ToList();
            var dayCounts = last7Days
                .Select(day => records.Count(x => x.OccurDateTime.LocalDateTime.Date == day))
                .ToList();
            var todayCount = dayCounts.Count == 0 ? 0 : dayCounts[^1];
            var baseline = dayCounts.Count <= 1 ? 0 : dayCounts.Take(dayCounts.Count - 1).Average();
            var forecast = (int)Math.Round(dayCounts.DefaultIfEmpty(0).Average(), MidpointRounding.AwayFromZero);
            var anomalyText = BuildAnomalyText(todayCount, baseline);

            return new DashboardOperationalMetricsSnapshot(
                openAll,
                openCritical,
                openMajor,
                openMinor,
                closed,
                cancel,
                (int)Math.Round(mttaMinutes, MidpointRounding.AwayFromZero),
                (int)Math.Round(mttrMinutes, MidpointRounding.AwayFromZero),
                closedSample == 0 ? 0 : (closedWithinSla * 100d / closedSample),
                repeatRate,
                aging24h,
                aging72h,
                forecast,
                anomalyText);
        }

        private static DashboardChartSnapshot BuildChartSnapshot(
            IReadOnlyList<LocalFormRecord> records,
            DateTimeOffset now)
        {
            var dayLabels = new List<string>();
            var dayValues = new List<int>();
            for (var i = 6; i >= 0; i--)
            {
                var date = now.LocalDateTime.Date.AddDays(-i);
                dayLabels.Add(date.ToString("dd MMM", CultureInfo.InvariantCulture));
                dayValues.Add(records.Count(x => x.OccurDateTime.LocalDateTime.Date == date));
            }

            var open = 0;
            var closed = 0;
            var cancel = 0;
            foreach (var record in records)
            {
                var normalizedStatus = GetRecordStatus(record);
                if (normalizedStatus.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    open++;
                }
                else if (normalizedStatus.Equals("closed", StringComparison.OrdinalIgnoreCase))
                {
                    closed++;
                }
                else if (normalizedStatus.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    cancel++;
                }
            }

            var onTrack = 0;
            var atRisk = 0;
            var breached = 0;
            foreach (var record in records.Where(x => GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase)))
            {
                var threshold = GetSlaThresholdHours(GetSeverity(record));
                var ageHours = Math.Max(0, (now - record.OccurDateTime).TotalHours);
                if (ageHours >= threshold)
                {
                    breached++;
                }
                else if (ageHours >= threshold * 0.8)
                {
                    atRisk++;
                }
                else
                {
                    onTrack++;
                }
            }

            var topPic = records
                .Where(x => GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => NormalizeText(x.Pic), StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            var topRoot = records
                .Select(x => NormalizeRootCause(x.RootCause))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            return new DashboardChartSnapshot(
                dayLabels,
                dayValues,
                open,
                closed,
                cancel,
                onTrack,
                atRisk,
                breached,
                topPic.Select(x => Shorten(x.Key, 12)).ToList(),
                topPic.Select(x => x.Count()).ToList(),
                topRoot.Select(x => Shorten(x.Key, 16)).ToList(),
                topRoot.Select(x => x.Count()).ToList());
        }

        private static DashboardInsightSnapshot BuildInsightSnapshot(
            IReadOnlyList<LocalFormRecord> records,
            DateTimeOffset now)
        {
            var topSegments = records
                .Where(x => !string.IsNullOrWhiteSpace(x.SegmentRoute))
                .GroupBy(x => x.SegmentRoute.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .Select(x => new RankedItem(Shorten(x.Key, 34), x.Count().ToString(CultureInfo.InvariantCulture)))
                .ToList();

            var priority = records
                .Where(x => GetRecordStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                {
                    var severity = GetSeverity(x);
                    var threshold = GetSlaThresholdHours(severity);
                    var age = Math.Max(0, (now - x.OccurDateTime).TotalHours);
                    var risk = age >= threshold ? "Breached" : age >= threshold * 0.8 ? "At Risk" : "On Track";
                    var riskWeight = risk.Equals("Breached", StringComparison.OrdinalIgnoreCase) ? 3 : risk.Equals("At Risk", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                    var severityWeight = severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ? 3 : severity.Equals("major", StringComparison.OrdinalIgnoreCase) ? 2 : severity.Equals("minor", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    return new { Record = x, Severity = severity, Risk = risk, RiskWeight = riskWeight, SeverityWeight = severityWeight, Age = age };
                })
                .OrderByDescending(x => x.RiskWeight)
                .ThenByDescending(x => x.SeverityWeight)
                .ThenByDescending(x => x.Age)
                .Take(20)
                .Select(x => new PriorityQueueItem(
                    string.IsNullOrWhiteSpace(x.Record.TtIoh) ? GetIncNumber(x.Record.Title) : x.Record.TtIoh,
                    string.IsNullOrWhiteSpace(x.Record.SegmentRoute) ? "-" : x.Record.SegmentRoute,
                    (string.IsNullOrWhiteSpace(x.Severity) ? "unknown" : x.Severity) + " | " + x.Risk + " | Age " + ((int)Math.Round(x.Age, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "h"))
                .ToList();

            return new DashboardInsightSnapshot(topSegments, priority);
        }

        private void ApplyOperationalMetrics(DashboardOperationalMetricsSnapshot metrics)
        {
            OpenAllTextBlock.Text = metrics.OpenAll.ToString(CultureInfo.InvariantCulture);
            OpenCriticalTextBlock.Text = metrics.OpenCritical.ToString(CultureInfo.InvariantCulture);
            OpenMajorTextBlock.Text = metrics.OpenMajor.ToString(CultureInfo.InvariantCulture);
            OpenMinorTextBlock.Text = metrics.OpenMinor.ToString(CultureInfo.InvariantCulture);
            ClosedTextBlock.Text = metrics.Closed.ToString(CultureInfo.InvariantCulture);
            CancelTextBlock.Text = metrics.Cancel.ToString(CultureInfo.InvariantCulture);
            MttaTextBlock.Text = metrics.MttaMinutes.ToString(CultureInfo.InvariantCulture);
            MttrTextBlock.Text = metrics.MttrMinutes.ToString(CultureInfo.InvariantCulture);
            SlaComplianceTextBlock.Text = metrics.SlaCompliance.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            RepeatIncidentTextBlock.Text = metrics.RepeatIncidentRate.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            Aging24hTextBlock.Text = metrics.Aging24h.ToString(CultureInfo.InvariantCulture);
            Aging72hTextBlock.Text = metrics.Aging72h.ToString(CultureInfo.InvariantCulture);
            ForecastTextBlock.Text = metrics.Forecast.ToString(CultureInfo.InvariantCulture) + " TT";
            AnomalyTextBlock.Text = metrics.AnomalyText;
        }

        private void ApplyCharts(DashboardChartSnapshot chart)
        {
            DailyTrendXAxes = new[]
            {
                new Axis
                {
                    Labels = chart.DayLabels.ToList(),
                    LabelsRotation = 0,
                    TextSize = 11,
                    MinStep = 1
                }
            };

            DailyTrendYAxes = new[]
            {
                new Axis
                {
                    MinLimit = 0,
                    TextSize = 11
                }
            };

            DailyTrendSeries = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Values = chart.DayValues,
                    Name = "Total TT",
                    Fill = new SolidColorPaint(new SKColor(52, 152, 219)),
                    Stroke = null
                },
                new LineSeries<int>
                {
                    Values = chart.DayValues,
                    Name = "Baseline",
                    Fill = null,
                    Stroke = new SolidColorPaint(new SKColor(36, 87, 148), 2),
                    GeometrySize = 6
                }
            };

            LinkStatusSeries = new ISeries[]
            {
                BuildPie("Open", chart.Open, new SKColor(236, 99, 118)),
                BuildPie("Closed", chart.Closed, new SKColor(79, 176, 112)),
                BuildPie("Cancel", chart.Cancel, new SKColor(255, 179, 71))
            };

            SlaRiskSeries = new ISeries[]
            {
                BuildPie("On Track", chart.OnTrack, new SKColor(80, 200, 120)),
                BuildPie("At Risk", chart.AtRisk, new SKColor(255, 190, 92)),
                BuildPie("Breached", chart.Breached, new SKColor(235, 87, 87))
            };

            PicWorkloadXAxes = new[]
            {
                new Axis
                {
                    Labels = chart.TopPicLabels.ToList(),
                    LabelsRotation = 0,
                    TextSize = 11,
                    MinStep = 1
                }
            };
            PicWorkloadYAxes = new[] { new Axis { MinLimit = 0, TextSize = 11 } };
            PicWorkloadSeries = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Values = chart.TopPicValues,
                    Name = "Open TT",
                    Fill = new SolidColorPaint(new SKColor(67, 160, 71)),
                    Stroke = null
                }
            };

            RootCauseXAxes = new[]
            {
                new Axis
                {
                    Labels = chart.TopRootCauseLabels.ToList(),
                    LabelsRotation = 0,
                    TextSize = 11,
                    MinStep = 1
                }
            };
            RootCauseYAxes = new[] { new Axis { MinLimit = 0, TextSize = 11 } };
            RootCauseSeries = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Values = chart.TopRootCauseValues,
                    Name = "Incident",
                    Fill = new SolidColorPaint(new SKColor(106, 90, 205)),
                    Stroke = null
                }
            };
        }

        private void UpdateDataQuality(IReadOnlyList<LocalFormRecord> records)
        {
            var total = Math.Max(1, records.Count);
            var missingSystem = records.Count(x => string.IsNullOrWhiteSpace(x.SystemKey));
            var missingSegment = records.Count(x => string.IsNullOrWhiteSpace(x.SegmentRoute));
            var missingPic = records.Count(x => string.IsNullOrWhiteSpace(x.Pic));

            MissingSystemKeyTextBlock.Text = "Missing System Key: " + missingSystem + " (" + (missingSystem * 100d / total).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
            MissingSegmentTextBlock.Text = "Missing Segment Route: " + missingSegment + " (" + (missingSegment * 100d / total).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
            MissingPicTextBlock.Text = "Missing PIC: " + missingPic + " (" + (missingPic * 100d / total).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
        }

        private static PieSeries<int> BuildPie(string name, int value, SKColor color)
        {
            return new PieSeries<int>
            {
                Values = new[] { value },
                Name = name,
                Fill = new SolidColorPaint(color),
                DataLabelsSize = 12,
                DataLabelsPosition = PolarLabelsPosition.Middle,
                DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString(CultureInfo.InvariantCulture)
            };
        }

        private static string GetSeverity(LocalFormRecord record)
        {
            var (_, severity) = ParseStatusSeverity(record.Title);
            return severity.Trim().ToLowerInvariant();
        }

        private static double GetSlaThresholdHours(string severity)
        {
            return severity switch
            {
                "critical" => 2,
                "major" => 4,
                "minor" => 8,
                _ => 12
            };
        }

        private static string NormalizeRootCause(string? rootCause)
        {
            if (string.IsNullOrWhiteSpace(rootCause))
            {
                return string.Empty;
            }

            var clean = rootCause.Trim();
            return clean.Equals("-", StringComparison.Ordinal) ? string.Empty : clean;
        }

        private static string BuildAnomalyText(int todayCount, double baseline)
        {
            if (baseline <= 0)
            {
                return "Normal";
            }

            var pct = ((todayCount - baseline) * 100d) / baseline;
            if (pct >= 40)
            {
                return "Spike +" + pct.ToString("0", CultureInfo.InvariantCulture) + "%";
            }

            if (pct <= -30)
            {
                return "Drop " + pct.ToString("0", CultureInfo.InvariantCulture) + "%";
            }

            return "Normal";
        }

        private static string NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, Math.Max(1, maxLength - 1)) + "…";
        }

        private static bool ContainsInsensitive(string source, string keyword)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(keyword))
            {
                return false;
            }

            return source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private static DashboardHistoryItem ToHistoryItem(LocalFormRecord record)
        {
            var normalizedStatus = GetRecordStatus(record);
            var (_, severity) = ParseStatusSeverity(record.Title);
            var statusText = string.IsNullOrWhiteSpace(normalizedStatus) ? "-" : normalizedStatus;
            var normalizedSeverity = string.IsNullOrWhiteSpace(severity) ? "-" : severity;
            var inc = GetIncNumber(record.Title);

            return new DashboardHistoryItem(
                record,
                string.IsNullOrWhiteSpace(record.Name) ? (string.IsNullOrWhiteSpace(inc) ? "Draft" : inc) : record.Name,
                record.Title,
                $"{record.SavedAt:dd-MM-yyyy HH:mm} | {statusText} - {normalizedSeverity}");
        }

        private static (string Status, string Severity) ParseStatusSeverity(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return (string.Empty, string.Empty);
            }

            var match = TicketTitleRegex.Match(title);
            if (!match.Success)
            {
                return (string.Empty, string.Empty);
            }

            return (
                match.Groups["status"].Value.Trim(),
                match.Groups["severity"].Value.Trim());
        }

        private static string NormalizeLinkStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return string.Empty;
            }

            var normalized = status.Trim().ToLowerInvariant();
            if (normalized is "close" or "closed")
            {
                return "closed";
            }

            if (normalized is "cancel" or "cancelled" or "canceled")
            {
                return "cancel";
            }

            return normalized;
        }

        private static string GetRecordStatus(LocalFormRecord record)
        {
            var fromStatusLink = NormalizeLinkStatus(record.StatusLink);
            if (!string.IsNullOrWhiteSpace(fromStatusLink))
            {
                return fromStatusLink;
            }

            var (statusFromTitle, _) = ParseStatusSeverity(record.Title);
            return NormalizeLinkStatus(statusFromTitle);
        }

        private static string GetIncNumber(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var match = IncNumberRegex.Match(title);
            return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        }

        private void SelectItem(DashboardHistoryItem? item)
        {
            _selectedItem = item;
            if (item is null)
            {
                SelectedBadgeTextBlock.Text = "Belum dipilih";
                SelectedTemplateTextBox.Text = string.Empty;
                CopyTemplateButton.IsEnabled = false;
                OpenInCreateTtButton.IsEnabled = false;
                return;
            }

            SelectedBadgeTextBlock.Text = item.MetaText;
            SelectedTemplateTextBox.Text = BuildTemplatePreview(item.Record);
            CopyTemplateButton.IsEnabled = true;
            OpenInCreateTtButton.IsEnabled = true;
        }

        private static string BuildTemplatePreview(LocalFormRecord record)
        {
            var segment = record.ShowSegmentRoute && !string.IsNullOrWhiteSpace(record.SegmentRoute)
                ? "Segment Route = " + record.SegmentRoute + Environment.NewLine
                : string.Empty;
            var system = record.ShowSystemKey && !string.IsNullOrWhiteSpace(record.SystemKey)
                ? "System Key = " + record.SystemKey + Environment.NewLine
                : string.Empty;
            var coordinate = string.IsNullOrWhiteSpace(record.Coordinate)
                ? string.Empty
                : "Coordinate = " + record.Coordinate + Environment.NewLine;

            return
                record.Title + Environment.NewLine +
                "Occur Time = " + record.OccurDateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) + Environment.NewLine +
                "Dispacth Time = " + record.DispatchDateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) + Environment.NewLine +
                "PIC = " + record.Pic + Environment.NewLine +
                "Rootcause = " + record.RootCause + Environment.NewLine +
                "Cut Point = " + record.CutPoint + Environment.NewLine +
                segment +
                system +
                coordinate +
                "Update Progress" + Environment.NewLine +
                record.UpdateProgress;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDashboardAsync();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RequestFilterRefresh(debounce: true);
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RequestFilterRefresh(debounce: false);
        }

        private void DateFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RequestFilterRefresh(debounce: false);
        }

        private void HistoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectItem(HistoryListView.SelectedItem as DashboardHistoryItem);
        }

        private void CopyTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItem is null)
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(BuildTemplatePreview(_selectedItem.Record));
            Clipboard.SetContent(package);
        }

        private void OpenInCreateTtButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItem is null)
            {
                return;
            }

            PendingDraftTransferService.SetPending(_selectedItem.Record);

            if (App.MainAppWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToModule("create-tt");
                return;
            }

            Frame?.Navigate(typeof(CreateTtPage));
        }

        private void DashboardHeroGlowCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var center = new System.Numerics.Vector2((float)sender.ActualWidth / 2, (float)sender.ActualHeight / 2);
            var radius = (float)Math.Min(sender.ActualWidth, sender.ActualHeight) / 2;
            var ds = args.DrawingSession;

            ds.Clear(Colors.Transparent);

            using var outerGlow = new CanvasRadialGradientBrush(sender, new[]
            {
                new CanvasGradientStop { Position = 0f, Color = ColorHelper.FromArgb(180, 90, 219, 255) },
                new CanvasGradientStop { Position = 1f, Color = ColorHelper.FromArgb(0, 90, 219, 255) }
            });
            ds.FillCircle(center, radius, outerGlow);
            ds.DrawCircle(center, radius * 0.72f, ColorHelper.FromArgb(220, 231, 248, 255), 2.2f);
            ds.FillCircle(center, radius * 0.18f, ColorHelper.FromArgb(255, 255, 255, 255));
        }

        private void TryInitializeWin2DHeroVisual()
        {
            if (DashboardHeroVisualHost.Child is not null)
            {
                return;
            }

            try
            {
                var canvas = new CanvasControl
                {
                    Width = 56,
                    Height = 56
                };
                canvas.Draw += DashboardHeroGlowCanvas_Draw;

                var icon = new FluentIcons.WinUI.SymbolIcon
                {
                    Symbol = FluentSymbol.Board,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ShellHeroPrimaryTextBrush"]
                };

                var hostGrid = new Grid();
                hostGrid.Children.Add(canvas);
                hostGrid.Children.Add(icon);

                DashboardHeroVisualHost.Child = hostGrid;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("DashboardPage.TryInitializeWin2DHeroVisual", ex);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SubscribeToStorageChange()
        {
            if (_isStorageChangeSubscribed)
            {
                return;
            }

            LocalFormStorageService.RecordsChanged += LocalFormStorageService_RecordsChanged;
            _isStorageChangeSubscribed = true;
        }

        private void LocalFormStorageService_RecordsChanged()
        {
            if (DispatcherQueue is null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await RefreshDashboardAsync();
            });
        }

        private void SubscribeToImportStateChange()
        {
            if (_isImportStateSubscribed)
            {
                return;
            }

            ImportJobService.StateChanged += ImportJobService_StateChanged;
            _isImportStateSubscribed = true;
        }

        private void ImportJobService_StateChanged(ImportJobState state)
        {
            _lastImportState = state;
            if (DispatcherQueue is null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ApplyImportStateToUi(state);
            });
        }

        private void ApplyImportStateToUi(ImportJobState state)
        {
            ImportProgressBar.IsIndeterminate = state.IsIndeterminate;
            ImportProgressBar.Value = state.IsIndeterminate ? 0 : Math.Max(0, Math.Min(100, state.Percent));
            ImportStateTextBlock.Text = state.IsActive
                ? state.Message + (state.IsIndeterminate ? string.Empty : " (" + state.Percent.ToString("0", CultureInfo.InvariantCulture) + "%)")
                : (string.IsNullOrWhiteSpace(state.Message) ? "Idle" : state.Message);
        }

        private sealed class DashboardPreparedData
        {
            public DashboardPreparedData(
                DashboardOperationalMetricsSnapshot operationalMetrics,
                DashboardChartSnapshot chart,
                IReadOnlyList<RankedItem> topSegments,
                IReadOnlyList<PriorityQueueItem> priorityQueue,
                IReadOnlyList<DashboardHistoryItem> historyItems,
                string historyCountText,
                string selectedRecordId)
            {
                OperationalMetrics = operationalMetrics;
                Chart = chart;
                TopSegments = topSegments;
                PriorityQueue = priorityQueue;
                HistoryItems = historyItems;
                HistoryCountText = historyCountText;
                SelectedRecordId = selectedRecordId;
            }

            public DashboardOperationalMetricsSnapshot OperationalMetrics { get; }
            public DashboardChartSnapshot Chart { get; }
            public IReadOnlyList<RankedItem> TopSegments { get; }
            public IReadOnlyList<PriorityQueueItem> PriorityQueue { get; }
            public IReadOnlyList<DashboardHistoryItem> HistoryItems { get; }
            public string HistoryCountText { get; }
            public string SelectedRecordId { get; }
        }

        private sealed class DashboardOperationalMetricsSnapshot
        {
            public DashboardOperationalMetricsSnapshot(
                int openAll,
                int openCritical,
                int openMajor,
                int openMinor,
                int closed,
                int cancel,
                int mttaMinutes,
                int mttrMinutes,
                double slaCompliance,
                double repeatIncidentRate,
                int aging24h,
                int aging72h,
                int forecast,
                string anomalyText)
            {
                OpenAll = openAll;
                OpenCritical = openCritical;
                OpenMajor = openMajor;
                OpenMinor = openMinor;
                Closed = closed;
                Cancel = cancel;
                MttaMinutes = mttaMinutes;
                MttrMinutes = mttrMinutes;
                SlaCompliance = slaCompliance;
                RepeatIncidentRate = repeatIncidentRate;
                Aging24h = aging24h;
                Aging72h = aging72h;
                Forecast = forecast;
                AnomalyText = anomalyText;
            }

            public int OpenAll { get; }
            public int OpenCritical { get; }
            public int OpenMajor { get; }
            public int OpenMinor { get; }
            public int Closed { get; }
            public int Cancel { get; }
            public int MttaMinutes { get; }
            public int MttrMinutes { get; }
            public double SlaCompliance { get; }
            public double RepeatIncidentRate { get; }
            public int Aging24h { get; }
            public int Aging72h { get; }
            public int Forecast { get; }
            public string AnomalyText { get; }
        }

        private sealed class DashboardChartSnapshot
        {
            public DashboardChartSnapshot(
                IReadOnlyList<string> dayLabels,
                IReadOnlyList<int> dayValues,
                int open,
                int closed,
                int cancel,
                int onTrack,
                int atRisk,
                int breached,
                IReadOnlyList<string> topPicLabels,
                IReadOnlyList<int> topPicValues,
                IReadOnlyList<string> topRootCauseLabels,
                IReadOnlyList<int> topRootCauseValues)
            {
                DayLabels = dayLabels;
                DayValues = dayValues;
                Open = open;
                Closed = closed;
                Cancel = cancel;
                OnTrack = onTrack;
                AtRisk = atRisk;
                Breached = breached;
                TopPicLabels = topPicLabels;
                TopPicValues = topPicValues;
                TopRootCauseLabels = topRootCauseLabels;
                TopRootCauseValues = topRootCauseValues;
            }

            public IReadOnlyList<string> DayLabels { get; }
            public IReadOnlyList<int> DayValues { get; }
            public int Open { get; }
            public int Closed { get; }
            public int Cancel { get; }
            public int OnTrack { get; }
            public int AtRisk { get; }
            public int Breached { get; }
            public IReadOnlyList<string> TopPicLabels { get; }
            public IReadOnlyList<int> TopPicValues { get; }
            public IReadOnlyList<string> TopRootCauseLabels { get; }
            public IReadOnlyList<int> TopRootCauseValues { get; }
        }

        private sealed class DashboardInsightSnapshot
        {
            public DashboardInsightSnapshot(
                IReadOnlyList<RankedItem> topSegments,
                IReadOnlyList<PriorityQueueItem> priorityQueue)
            {
                TopSegments = topSegments;
                PriorityQueue = priorityQueue;
            }

            public IReadOnlyList<RankedItem> TopSegments { get; }
            public IReadOnlyList<PriorityQueueItem> PriorityQueue { get; }
        }

        private sealed class DashboardHistoryItem
        {
            public DashboardHistoryItem(LocalFormRecord record, string headerText, string subHeaderText, string metaText)
            {
                Record = record;
                HeaderText = headerText;
                SubHeaderText = string.IsNullOrWhiteSpace(subHeaderText) ? "-" : subHeaderText;
                MetaText = metaText;
            }

            public LocalFormRecord Record { get; }
            public string HeaderText { get; }
            public string SubHeaderText { get; }
            public string MetaText { get; }
        }

        private sealed class RankedItem
        {
            public RankedItem(string label, string value)
            {
                Label = label;
                Value = value;
            }

            public string Label { get; }
            public string Value { get; }
        }

        private sealed class PriorityQueueItem
        {
            public PriorityQueueItem(string ticket, string segment, string meta)
            {
                Ticket = string.IsNullOrWhiteSpace(ticket) ? "-" : ticket;
                Segment = string.IsNullOrWhiteSpace(segment) ? "-" : segment;
                Meta = meta;
            }

            public string Ticket { get; }
            public string Segment { get; }
            public string Meta { get; }
        }
    }
}
