using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using NOCREPORTGENERATOR.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed class AdvancedOpsPageController
    {
        private readonly Page _hostPage;
        private readonly OpsPageKind _pageKind;
        private readonly string _defaultModuleTag;
        private readonly TextBlock _summaryTextBlock;
        private readonly TextBlock _lastUpdatedTextBlock;
        private readonly TextBox _commandTextBox;
        private readonly ListView _leftListView;
        private readonly ListView _rightListView;
        private readonly TextBlock _kpi1LabelText;
        private readonly TextBlock _kpi1ValueText;
        private readonly TextBlock _kpi2LabelText;
        private readonly TextBlock _kpi2ValueText;
        private readonly TextBlock _kpi3LabelText;
        private readonly TextBlock _kpi3ValueText;
        private readonly LiveChartsCore.SkiaSharpView.WinUI.CartesianChart _trendChart;

        private readonly DispatcherQueueTimer _autoRefreshTimer;
        private IReadOnlyList<OpsListItem> _leftItems = Array.Empty<OpsListItem>();
        private IReadOnlyList<OpsListItem> _rightItems = Array.Empty<OpsListItem>();
        private string _selectedRecordId = string.Empty;
        private bool _isRefreshing;
        private bool _hasPendingRefresh;
        private bool _isSubscribed;
        private bool _isInitialized;
        private DateTimeOffset _lastRefreshAt;
        private DateTimeOffset _lastImportTriggeredRefreshAt = DateTimeOffset.MinValue;

        public AdvancedOpsPageController(
            Page hostPage,
            OpsPageKind pageKind,
            string defaultModuleTag,
            TextBlock summaryTextBlock,
            TextBlock lastUpdatedTextBlock,
            TextBox commandTextBox,
            ListView leftListView,
            ListView rightListView,
            TextBlock kpi1LabelText,
            TextBlock kpi1ValueText,
            TextBlock kpi2LabelText,
            TextBlock kpi2ValueText,
            TextBlock kpi3LabelText,
            TextBlock kpi3ValueText,
            LiveChartsCore.SkiaSharpView.WinUI.CartesianChart trendChart)
        {
            _hostPage = hostPage;
            _pageKind = pageKind;
            _defaultModuleTag = defaultModuleTag;
            _summaryTextBlock = summaryTextBlock;
            _lastUpdatedTextBlock = lastUpdatedTextBlock;
            _commandTextBox = commandTextBox;
            _leftListView = leftListView;
            _rightListView = rightListView;
            _kpi1LabelText = kpi1LabelText;
            _kpi1ValueText = kpi1ValueText;
            _kpi2LabelText = kpi2LabelText;
            _kpi2ValueText = kpi2ValueText;
            _kpi3LabelText = kpi3LabelText;
            _kpi3ValueText = kpi3ValueText;
            _trendChart = trendChart;

            _autoRefreshTimer = _hostPage.DispatcherQueue.CreateTimer();
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(15);
            _autoRefreshTimer.IsRepeating = true;
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        }

        public async Task OnLoadedAsync()
        {
            SubscribeEvents();
            if (!_autoRefreshTimer.IsRunning)
            {
                _autoRefreshTimer.Start();
            }

            if (!_isInitialized)
            {
                await RefreshAsync();
                _isInitialized = true;
                return;
            }

            if ((DateTimeOffset.Now - _lastRefreshAt).TotalSeconds > 20)
            {
                _ = RefreshAsync();
            }
            else
            {
                ApplyFilter();
            }
        }

        public void OnUnloaded()
        {
            UnsubscribeEvents();
            if (_autoRefreshTimer.IsRunning)
            {
                _autoRefreshTimer.Stop();
            }
        }

        public void OnRefreshClicked()
        {
            _ = RefreshAsync();
        }

        public void OnCommandTextChanged()
        {
            ApplyFilter();
        }

        public void OnListSelectionChanged(object sender)
        {
            if (sender is not ListView listView || listView.SelectedItem is not OpsListItem selected)
            {
                return;
            }

            _selectedRecordId = selected.RecordId;
            if (ReferenceEquals(listView, _leftListView))
            {
                _rightListView.SelectedItem = null;
            }
            else
            {
                _leftListView.SelectedItem = null;
            }
        }

        public async Task OnOpenActionClickedAsync()
        {
            var command = _commandTextBox.Text?.Trim() ?? string.Empty;
            if (_pageKind == OpsPageKind.ReportBuilder &&
                (string.IsNullOrWhiteSpace(command) ||
                 command.Equals("export", StringComparison.OrdinalIgnoreCase) ||
                 command.Equals("pdf", StringComparison.OrdinalIgnoreCase) ||
                 command.Contains("report", StringComparison.OrdinalIgnoreCase)))
            {
                await ExportReportAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(command))
            {
                if (string.Equals(command, "refresh", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(command, "reload", StringComparison.OrdinalIgnoreCase))
                {
                    await RefreshAsync();
                    return;
                }

                if (command.StartsWith("inc-", StringComparison.OrdinalIgnoreCase))
                {
                    await OpenByIncAsync(command);
                    return;
                }

                var module = CommandToModule(command);
                if (!string.IsNullOrWhiteSpace(module))
                {
                    NavigateToModule(module);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(_selectedRecordId))
            {
                _hostPage.Frame?.Navigate(typeof(TtDetailPage), _selectedRecordId);
                return;
            }

            NavigateToModule(_defaultModuleTag);
        }

        private async Task RefreshAsync()
        {
            if (_isRefreshing)
            {
                _hasPendingRefresh = true;
                return;
            }

            _isRefreshing = true;
            try
            {
                do
                {
                    _hasPendingRefresh = false;
                    var records = await LocalFormStorageService.GetAllAsync();
                    var recordsSnapshot = records.ToList();
                    var importStateSnapshot = ImportJobService.CurrentState;
                    var nowSnapshot = DateTimeOffset.Now;
                    var data = await Task.Run(() =>
                        AdvancedOpsInsightsService.Build(_pageKind, recordsSnapshot, importStateSnapshot, nowSnapshot));
                    _leftItems = data.LeftItems;
                    _rightItems = data.RightItems;
                    _summaryTextBlock.Text = data.Summary;
                    _kpi1LabelText.Text = data.Kpi1Label;
                    _kpi1ValueText.Text = data.Kpi1Value;
                    _kpi2LabelText.Text = data.Kpi2Label;
                    _kpi2ValueText.Text = data.Kpi2Value;
                    _kpi3LabelText.Text = data.Kpi3Label;
                    _kpi3ValueText.Text = data.Kpi3Value;
                    ApplyChart(data.Chart);
                    _lastUpdatedTextBlock.Text = "Updated " + DateTimeOffset.Now.ToString("dd-MM-yyyy HH:mm:ss");
                    _lastRefreshAt = DateTimeOffset.Now;
                    ApplyFilter();
                }
                while (_hasPendingRefresh);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError(_hostPage.GetType().Name + ".RefreshAsync", ex);
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private void ApplyChart(IReadOnlyList<OpsChartPoint> chart)
        {
            var labels = chart.Select(x => x.Label).ToArray();
            var values = chart.Select(x => x.Value).ToArray();

            _trendChart.Series = new ISeries[]
            {
                new ColumnSeries<int>
                {
                    Values = values,
                    Name = "TT",
                    Fill = new SolidColorPaint(new SKColor(44, 131, 209)),
                    Stroke = null
                }
            };

            _trendChart.XAxes = new[]
            {
                new Axis
                {
                    Labels = labels,
                    TextSize = 10,
                    LabelsRotation = 0,
                    MinStep = 1
                }
            };

            _trendChart.YAxes = new[]
            {
                new Axis
                {
                    MinLimit = 0,
                    TextSize = 10
                }
            };
        }

        private void SubscribeEvents()
        {
            if (_isSubscribed)
            {
                return;
            }

            LocalFormStorageService.RecordsChanged += LocalFormStorageService_RecordsChanged;
            ImportJobService.StateChanged += ImportJobService_StateChanged;
            _isSubscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!_isSubscribed)
            {
                return;
            }

            LocalFormStorageService.RecordsChanged -= LocalFormStorageService_RecordsChanged;
            ImportJobService.StateChanged -= ImportJobService_StateChanged;
            _isSubscribed = false;
        }

        private void LocalFormStorageService_RecordsChanged()
        {
            if (_hostPage.DispatcherQueue is null)
            {
                return;
            }

            _ = _hostPage.DispatcherQueue.TryEnqueue(async () => await RefreshAsync());
        }

        private void ImportJobService_StateChanged(ImportJobState state)
        {
            if (_hostPage.DispatcherQueue is null)
            {
                return;
            }

            _ = _hostPage.DispatcherQueue.TryEnqueue(() =>
            {
                _lastUpdatedTextBlock.Text = state.IsActive ? state.Message : _lastUpdatedTextBlock.Text;
                if (!state.IsActive)
                {
                    _ = RefreshAsync();
                    return;
                }

                var now = DateTimeOffset.Now;
                if ((now - _lastImportTriggeredRefreshAt).TotalSeconds < 5)
                {
                    return;
                }

                _lastImportTriggeredRefreshAt = now;
                _ = RefreshAsync();
            });
        }

        private void AutoRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (!_isRefreshing)
            {
                _ = RefreshAsync();
            }
        }

        private async Task ExportReportAsync()
        {
            try
            {
                if (App.MainAppWindow is null)
                {
                    return;
                }

                var picker = new FileSavePicker();
                picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
                picker.SuggestedFileName = "NOC_Report_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
                var hWnd = WindowNative.GetWindowHandle(App.MainAppWindow);
                InitializeWithWindow.Initialize(picker, hWnd);
                var file = await picker.PickSaveFileAsync();
                if (file is null)
                {
                    return;
                }

                var records = await LocalFormStorageService.GetAllAsync();
                await ReportExportService.ExportSummaryPdfAsync(file.Path, records, DateTimeOffset.Now);
                _lastUpdatedTextBlock.Text = "Exported PDF: " + file.Name;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError(_hostPage.GetType().Name + ".ExportReportAsync", ex);
            }
        }

        private async Task OpenByIncAsync(string inc)
        {
            var normalized = inc.Trim().ToUpperInvariant();
            var records = await LocalFormStorageService.GetAllAsync();
            var match = records.FirstOrDefault(x =>
                string.Equals((x.TtIoh ?? string.Empty).Trim().ToUpperInvariant(), normalized, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(x.Title) && x.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                _hostPage.Frame?.Navigate(typeof(TtDetailPage), match.Id);
                return;
            }

            NavigateToModule(_defaultModuleTag);
        }

        private static string CommandToModule(string command)
        {
            if (command.Contains("dashboard", StringComparison.OrdinalIgnoreCase)) return "dashboard";
            if (command.Contains("history", StringComparison.OrdinalIgnoreCase)) return "history-tt";
            if (command.Contains("map", StringComparison.OrdinalIgnoreCase)) return "live-map";
            if (command.Contains("setting", StringComparison.OrdinalIgnoreCase)) return "settings";
            if (command.Contains("create", StringComparison.OrdinalIgnoreCase)) return "create-tt";
            return string.Empty;
        }

        private void NavigateToModule(string tag)
        {
            if (App.MainAppWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToModule(tag);
            }
        }

        private void ApplyFilter()
        {
            var keyword = _commandTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(keyword))
            {
                _leftListView.ItemsSource = _leftItems;
                _rightListView.ItemsSource = _rightItems;
                return;
            }

            _leftListView.ItemsSource = FilterItems(_leftItems, keyword);
            _rightListView.ItemsSource = FilterItems(_rightItems, keyword);
        }

        private static IReadOnlyList<OpsListItem> FilterItems(IReadOnlyList<OpsListItem> source, string keyword)
        {
            return source.Where(x =>
                x.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                x.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }
}

