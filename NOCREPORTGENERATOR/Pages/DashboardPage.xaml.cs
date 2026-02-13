using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
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
using Windows.ApplicationModel.DataTransfer;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class DashboardPage : Page, INotifyPropertyChanged
    {
        private readonly List<LocalFormRecord> _allRecords = new();
        private readonly List<DashboardHistoryItem> _filteredItems = new();
        private DashboardHistoryItem? _selectedItem;
        private static readonly Regex TicketTitleRegex = new(@"\[(?<status>[^-\]]+)\s*-\s*(?<severity>[^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IncNumberRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private ISeries[] _dailyTrendSeries = Array.Empty<ISeries>();
        private Axis[] _dailyTrendXAxes = Array.Empty<Axis>();
        private Axis[] _dailyTrendYAxes = Array.Empty<Axis>();
        private ISeries[] _severitySeries = Array.Empty<ISeries>();

        public DashboardPage()
        {
            InitializeComponent();
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

        public ISeries[] SeveritySeries
        {
            get => _severitySeries;
            private set
            {
                _severitySeries = value;
                OnPropertyChanged();
            }
        }

        private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            TryInitializeWin2DHeroVisual();
            InitializeFilters();
            await RefreshDashboardAsync();
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
        }

        private void InitializeFilters()
        {
            StatusFilterComboBox.ItemsSource = new[]
            {
                "Semua Status",
                "Open (Critical/Major)",
                "Open Critical",
                "Open Major"
            };
            StatusFilterComboBox.SelectedIndex = 0;

            DateFilterComboBox.ItemsSource = new[]
            {
                "Semua Waktu",
                "Hari Ini",
                "7 Hari Terakhir"
            };
            DateFilterComboBox.SelectedIndex = 0;
        }

        private async System.Threading.Tasks.Task RefreshDashboardAsync()
        {
            try
            {
                var records = await LocalFormStorageService.GetAllAsync();
                _allRecords.Clear();
                _allRecords.AddRange(records);

                UpdateHeadlineStats();
                ApplyFilters();
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("DashboardPage.RefreshDashboardAsync", ex);
            }
        }

        private void UpdateHeadlineStats()
        {
            var today = DateTime.Today;
            var totalToday = _allRecords.Count(x => x.OccurDateTime.LocalDateTime.Date == today);
            var openCriticalMajor = _allRecords.Count(x =>
            {
                var (status, severity) = ParseStatusSeverity(x.Title);
                return status.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                       (severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                        severity.Equals("major", StringComparison.OrdinalIgnoreCase));
            });

            TotalTodayTextBlock.Text = totalToday.ToString(CultureInfo.InvariantCulture);
            OpenCriticalMajorTextBlock.Text = openCriticalMajor.ToString(CultureInfo.InvariantCulture);
            DraftLocalTextBlock.Text = _allRecords.Count.ToString(CultureInfo.InvariantCulture);
        }

        private void ApplyFilters()
        {
            var query = SearchTextBox.Text?.Trim() ?? string.Empty;
            var statusFilter = StatusFilterComboBox.SelectedItem as string ?? "Semua Status";
            var dateFilter = DateFilterComboBox.SelectedItem as string ?? "Semua Waktu";

            IEnumerable<LocalFormRecord> filtered = _allRecords;
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
            UpdateDetailStats(filteredRecords);
            UpdateCharts(filteredRecords);

            _filteredItems.Clear();
            _filteredItems.AddRange(filteredRecords.Select(ToHistoryItem));
            HistoryListView.ItemsSource = null;
            HistoryListView.ItemsSource = _filteredItems;
            HistoryCountTextBlock.Text = _filteredItems.Count + " item";

            if (_filteredItems.Count == 0)
            {
                SelectItem(null);
                return;
            }

            var selected = _selectedItem is null
                ? _filteredItems[0]
                : _filteredItems.FirstOrDefault(x => x.Record.Id.Equals(_selectedItem.Record.Id, StringComparison.OrdinalIgnoreCase));
            SelectItem(selected ?? _filteredItems[0]);
            HistoryListView.SelectedItem = selected ?? _filteredItems[0];
        }

        private void UpdateDetailStats(IReadOnlyList<LocalFormRecord> records)
        {
            var weekStart = DateTime.Today.AddDays(-6);
            var totalWeek = records.Count(x => x.OccurDateTime.LocalDateTime.Date >= weekStart);

            var openCritical = records.Count(x =>
            {
                var (status, severity) = ParseStatusSeverity(x.Title);
                return status.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                       severity.Equals("critical", StringComparison.OrdinalIgnoreCase);
            });

            var responseMinutes = records
                .Select(x => (x.DispatchDateTime - x.OccurDateTime).TotalMinutes)
                .Where(x => x >= 0)
                .ToList();
            var avgResponse = responseMinutes.Count == 0 ? 0 : (int)Math.Round(responseMinutes.Average(), MidpointRounding.AwayFromZero);

            TotalWeekTextBlock.Text = totalWeek.ToString(CultureInfo.InvariantCulture);
            OpenCriticalTextBlock.Text = openCritical.ToString(CultureInfo.InvariantCulture);
            AvgResponseTextBlock.Text = avgResponse.ToString(CultureInfo.InvariantCulture);
        }

        private void UpdateCharts(IReadOnlyList<LocalFormRecord> records)
        {
            var dayLabels = new List<string>();
            var dayValues = new List<int>();
            for (var i = 6; i >= 0; i--)
            {
                var date = DateTime.Today.AddDays(-i);
                dayLabels.Add(date.ToString("dd MMM", CultureInfo.InvariantCulture));
                dayValues.Add(records.Count(x => x.OccurDateTime.LocalDateTime.Date == date));
            }

            DailyTrendXAxes = new[]
            {
                new Axis
                {
                    Labels = dayLabels,
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
                    Values = dayValues,
                    Name = "Total TT",
                    Fill = new SolidColorPaint(new SKColor(36, 148, 229)),
                    Stroke = null
                }
            };

            var openCritical = 0;
            var openMajor = 0;
            var other = 0;

            foreach (var record in records)
            {
                var (status, severity) = ParseStatusSeverity(record.Title);
                if (!status.Equals("open", StringComparison.OrdinalIgnoreCase))
                {
                    other++;
                    continue;
                }

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
                    other++;
                }
            }

            SeveritySeries = new ISeries[]
            {
                new PieSeries<int>
                {
                    Values = new[] { openCritical },
                    Name = "Open Critical",
                    Fill = new SolidColorPaint(new SKColor(226, 56, 88)),
                    DataLabelsSize = 12,
                    DataLabelsPosition = PolarLabelsPosition.Middle,
                    DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString(CultureInfo.InvariantCulture)
                },
                new PieSeries<int>
                {
                    Values = new[] { openMajor },
                    Name = "Open Major",
                    Fill = new SolidColorPaint(new SKColor(255, 156, 70)),
                    DataLabelsSize = 12,
                    DataLabelsPosition = PolarLabelsPosition.Middle,
                    DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString(CultureInfo.InvariantCulture)
                },
                new PieSeries<int>
                {
                    Values = new[] { other },
                    Name = "Other",
                    Fill = new SolidColorPaint(new SKColor(128, 144, 160)),
                    DataLabelsSize = 12,
                    DataLabelsPosition = PolarLabelsPosition.Middle,
                    DataLabelsFormatter = point => point.Coordinate.PrimaryValue.ToString(CultureInfo.InvariantCulture)
                }
            };
        }

        private static IEnumerable<LocalFormRecord> ApplyStatusFilter(IEnumerable<LocalFormRecord> data, string statusFilter)
        {
            return statusFilter switch
            {
                "Open (Critical/Major)" => data.Where(x =>
                {
                    var (status, severity) = ParseStatusSeverity(x.Title);
                    return status.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                           (severity.Equals("critical", StringComparison.OrdinalIgnoreCase) ||
                            severity.Equals("major", StringComparison.OrdinalIgnoreCase));
                }),
                "Open Critical" => data.Where(x =>
                {
                    var (status, severity) = ParseStatusSeverity(x.Title);
                    return status.Equals("open", StringComparison.OrdinalIgnoreCase) &&
                           severity.Equals("critical", StringComparison.OrdinalIgnoreCase);
                }),
                "Open Major" => data.Where(x =>
                {
                    var (status, severity) = ParseStatusSeverity(x.Title);
                    return status.Equals("open", StringComparison.OrdinalIgnoreCase) &&
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
                _ => data
            };
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
            var (status, severity) = ParseStatusSeverity(record.Title);
            var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "-" : status;
            var normalizedSeverity = string.IsNullOrWhiteSpace(severity) ? "-" : severity;
            var inc = GetIncNumber(record.Title);

            return new DashboardHistoryItem(
                record,
                string.IsNullOrWhiteSpace(record.Name) ? (string.IsNullOrWhiteSpace(inc) ? "Draft" : inc) : record.Name,
                record.Title,
                $"{record.SavedAt:dd-MM-yyyy HH:mm} | {normalizedStatus} - {normalizedSeverity}");
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
            ApplyFilters();
        }

        private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void DateFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
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

                var icon = new FontIcon
                {
                    Glyph = "\uE9D2",
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
    }
}
