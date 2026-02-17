using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NOCREPORTGENERATOR.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class LiveMapPage : Page
    {
        private static readonly Regex NumberRegex = new(@"[-+]?\d+(?:[.,]\d+)?", RegexOptions.Compiled);
        private static readonly Regex DmsDirectionRegex = new(@"[NSEW]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TitleRegex = new(@"\[(?<status>[^-\]]+)\s*-\s*(?<severity>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TtIohNormalizeRegex = new(@"INC\W*(\d{8})\W*(\d{8})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SegmentIdPairRegex = new(@"\b\d{5,8}\s*[-/]\s*\d{5,8}\b", RegexOptions.Compiled);
        private static readonly Regex DateLikeRegex = new(@"\b(?:\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[-/]\d{1,2}[-/]\d{2,4})\b", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly string[] SmartKeywords = { "tt:", "seg:", "spm:", "cp:", "from:", "to:", "date:", "status:", "sev:" };
        private const string AllSegmentOption = "Semua Segment";
        private const string AllSegmentPmOption = "Semua Segment PM";
        private const string AllStatusOption = "Semua Status";
        private const string AllSeverityOption = "Semua Severity";
        private const string LiveMapSnapshotVersion = "1";
        private static readonly string AppDataDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOCREPORTGENERATOR");
        private static readonly string LocalFormsDbPath = Path.Combine(AppDataDirectoryPath, "tt_forms.db");
        private static readonly string SegmentPmDbPath = Path.Combine(AppDataDirectoryPath, "segmentpm_map.db");
        private static readonly string LiveMapSnapshotFilePath = Path.Combine(AppDataDirectoryPath, "live_map_snapshot.json");
        private static readonly TimeSpan OverlayIdleTimeout = TimeSpan.FromSeconds(5);

        private bool _webViewReady;
        private bool _mapLoaded;
        private bool _filterReady;
        private bool _segmentReady;
        private bool _segmentPmReady;
        private bool _sourceDataDirty = true;
        private bool _hasLoadedDataOnce;
        private SourceDataSignature _loadedSourceSignature = new();
        private bool _useRoadRoute;
        private string _payloadJson = "{\"markers\":[],\"heatmap\":false}";
        private List<MapPoint> _all = new();
        private List<MapPoint> _last = new();
        private List<SegmentPmMapService.SegmentSiteLinkPoint> _siteLinks = new();
        private Dictionary<string, IReadOnlyList<string>> _segmentPmByRoute = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _knownSegmentPmOptions = new();
        private CancellationTokenSource? _renderCts;
        private CancellationTokenSource? _smartFilterDebounceCts;
        private CancellationTokenSource? _snapshotSaveDebounceCts;
        private readonly DispatcherTimer _overlayIdleTimer = new();
        private bool _overlayAutoHidden;
        private bool _suppressFilterEvents;

        public LiveMapPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            LocalFormStorageService.RecordsChanged += LocalFormStorageService_RecordsChanged;
            InitializeOverlaySearchAutoHide();
            Loaded += LiveMapPage_Loaded;
        }

        private void InitializeOverlaySearchAutoHide()
        {
            _overlayIdleTimer.Stop();
            _overlayIdleTimer.Interval = OverlayIdleTimeout;
            _overlayIdleTimer.Tick -= OverlayIdleTimer_Tick;
            _overlayIdleTimer.Tick += OverlayIdleTimer_Tick;

            ShowOverlaySearchBar(resetTimer: false);
        }

        private void OverlayIdleTimer_Tick(object? sender, object e)
        {
            _overlayIdleTimer.Stop();

            if (OverlaySmartFilterAutoSuggestBox.FocusState != FocusState.Unfocused || OverlaySmartFilterAutoSuggestBox.IsSuggestionListOpen)
            {
                RegisterOverlayActivity();
                return;
            }

            HideOverlaySearchBar();
        }

        private void RegisterOverlayActivity()
        {
            ShowOverlaySearchBar(resetTimer: false);
            RestartOverlayIdleTimer();
        }

        private void RestartOverlayIdleTimer()
        {
            _overlayIdleTimer.Stop();
            _overlayIdleTimer.Start();
        }

        private void ShowOverlaySearchBar(bool resetTimer = true)
        {
            if (!_overlayAutoHidden && MapOverlaySearchBorder.Visibility == Visibility.Visible)
            {
                if (resetTimer)
                {
                    RestartOverlayIdleTimer();
                }
                return;
            }

            MapOverlaySearchBorder.Visibility = Visibility.Visible;
            MapOverlayRevealButton.Visibility = Visibility.Collapsed;
            _overlayAutoHidden = false;

            if (resetTimer)
            {
                RestartOverlayIdleTimer();
            }
        }

        private void HideOverlaySearchBar()
        {
            if (_overlayAutoHidden && MapOverlaySearchBorder.Visibility != Visibility.Visible)
            {
                return;
            }

            if (MapRenderProgressBorder.Visibility == Visibility.Visible)
            {
                RestartOverlayIdleTimer();
                return;
            }

            MapOverlaySearchBorder.Visibility = Visibility.Collapsed;
            MapOverlayRevealButton.Visibility = Visibility.Visible;
            _overlayAutoHidden = true;
        }

        private async void LiveMapPage_Loaded(object sender, RoutedEventArgs e)
        {
            RegisterOverlayActivity();
            EnsureFilterOptions();
            SetSmartQueryText(SmartFilterAutoSuggestBox.Text ?? string.Empty);
            await EnsureSegmentRouteFilterOptionsAsync();
            await LoadMapAsync();
            await TryFocusPendingCoordinateAsync();
        }

        private async Task LoadMapAsync()
        {
            try
            {
                await EnsureWebViewReadyAsync();

                if (!_hasLoadedDataOnce)
                {
                    var restored = await TryRestoreSnapshotAsync();
                    if (restored.Restored)
                    {
                        _hasLoadedDataOnce = true;
                        _sourceDataDirty = !restored.IsFresh;
                        await EnsureSegmentPmFilterOptionsAsync();

                        if (!_mapLoaded)
                        {
                            MapWebView.NavigateToString(BuildMapHtml());
                        }
                        else
                        {
                            await PushToMapAsync();
                        }

                        if (restored.IsFresh)
                        {
                            return;
                        }
                    }
                }

                if (_hasLoadedDataOnce && !_sourceDataDirty)
                {
                    var currentSignature = GetCurrentSourceSignature();
                    if (!IsSameSourceSignature(_loadedSourceSignature, currentSignature))
                    {
                        _sourceDataDirty = true;
                    }
                }

                if (_hasLoadedDataOnce && !_sourceDataDirty)
                {
                    await EnsureSegmentPmFilterOptionsAsync();
                    if (_mapLoaded)
                    {
                        await PushToMapAsync();
                    }
                    return;
                }

                MapFilterSummaryTextBlock.Text = _hasLoadedDataOnce
                    ? "Menyegarkan data Live Map..."
                    : "Memuat data Live Map...";

                var records = await LocalFormStorageService.GetCoordinateRecordsAsync();
                _all = records.Select(ToPoint).Where(x => x is not null).Cast<MapPoint>().ToList();
                await LoadSiteLinksAsync();
                await EnsureSegmentPmFilterOptionsAsync();
                MergeSegmentPmOptionsFromPoints(_all);
                await ApplySmartFilterAsync();

                _hasLoadedDataOnce = true;
                _sourceDataDirty = false;
                _loadedSourceSignature = GetCurrentSourceSignature();
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.LoadMapAsync", ex);
            }
        }

        private void LocalFormStorageService_RecordsChanged()
        {
            _sourceDataDirty = true;
        }

        private async Task EnsureWebViewReadyAsync()
        {
            if (_webViewReady) return;
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            MapWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            MapWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            _webViewReady = true;
        }

        private async void CoreWebView2_WebMessageReceived(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                if (doc.RootElement.TryGetProperty("openDetail", out var openDetail) && openDetail.ValueKind == JsonValueKind.String)
                {
                    var tt = NormalizeTtIohForMap(openDetail.GetString());
                    if (string.IsNullOrWhiteSpace(tt) || string.Equals(tt, "-", StringComparison.Ordinal))
                    {
                        return;
                    }

                    var records = await LocalFormStorageService.GetAllAsync();
                    var target = records
                        .FirstOrDefault(x => string.Equals(NormalizeTtIohForMap(x.TtIoh), tt, StringComparison.OrdinalIgnoreCase));
                    if (target is not null)
                    {
                        Frame?.Navigate(typeof(TtDetailPage), target.Id);
                    }
                    return;
                }

                if (!doc.RootElement.TryGetProperty("openMaps", out var openMaps) || openMaps.ValueKind != JsonValueKind.String)
                {
                    return;
                }

                var value = openMaps.GetString() ?? string.Empty;
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                {
                    return;
                }

                await Launcher.LaunchUriAsync(uri);
            }
            catch
            {
            }
        }

        private async void CoreWebView2_NavigationCompleted(Microsoft.Web.WebView2.Core.CoreWebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) return;
            _mapLoaded = true;
            await PushToMapAsync();
        }

        private async Task PushToMapAsync()
        {
            await EnsureWebViewReadyAsync();
            MapWebView.CoreWebView2?.PostWebMessageAsJson(_payloadJson);
        }

        private MapPoint? ToPoint(LocalFormStorageService.MapCoordinateRecord r)
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Coordinate)) return null;
            if (!TryParseCoordinate(r.Coordinate, out var lat, out var lon)) return null;
            var m = TitleRegex.Match(r.Title ?? string.Empty);
            var status = m.Success ? m.Groups["status"].Value.Trim() : "Unknown";
            var severity = m.Success ? m.Groups["severity"].Value.Trim() : "Unknown";
            var normalizedSegmentPm = NormalizeSegmentPmForUi(r.SegmentPm);
            return new MapPoint
            {
                Latitude = lat,
                Longitude = lon,
                TtIoh = NormalizeTtIohForMap(r.TtIoh),
                Title = string.IsNullOrWhiteSpace(r.Title) ? "-" : r.Title.Trim(),
                Status = string.IsNullOrWhiteSpace(status) ? "Unknown" : status,
                Severity = string.IsNullOrWhiteSpace(severity) ? "Unknown" : severity,
                CutPoint = string.IsNullOrWhiteSpace(r.CutPoint) ? "-" : r.CutPoint.Trim(),
                SegmentRoute = string.IsNullOrWhiteSpace(r.SegmentRoute) ? "-" : r.SegmentRoute.Trim(),
                SegmentPm = string.IsNullOrWhiteSpace(normalizedSegmentPm) ? "-" : normalizedSegmentPm,
                DispatchAt = r.DispatchDateTime == default ? DateTimeOffset.MinValue : r.DispatchDateTime,
                DispatchTime = r.DispatchDateTime == default ? "-" : r.DispatchDateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)
            };
        }

        private void EnsureFilterOptions()
        {
            if (_filterReady) return;
            _suppressFilterEvents = true;
            try
            {
                QuickFilterComboBox.ItemsSource = new[] { "Semua Waktu", "Hari Ini", "7 Hari Terakhir", "30 Hari Terakhir" };
                QuickFilterComboBox.SelectedIndex = 0;
                StatusFilterComboBox.ItemsSource = new[] { AllStatusOption, "Open", "Close", "Monitoring", "Unknown" };
                StatusFilterComboBox.SelectedIndex = 0;
                SeverityFilterComboBox.ItemsSource = new[] { AllSeverityOption, "Critical", "Major", "Minor", "Unknown" };
                SeverityFilterComboBox.SelectedIndex = 0;
                HeatmapToggleSwitch.IsOn = false;
                SiteMarkersToggleSwitch.IsOn = false;
                SiteLinesToggleSwitch.IsOn = false;
                RoadRouteToggleSwitch.IsOn = false;
                _useRoadRoute = false;
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            ApplyCompactFilterMode(CompactFilterModeCheckBox.IsChecked == true);
            _filterReady = true;
        }

        private async Task EnsureSegmentRouteFilterOptionsAsync()
        {
            if (_segmentReady) return;
            _suppressFilterEvents = true;
            try
            {
                var lookup = await DatabaseLinkLookupService.FindSegmentLookupBySystemKeyAsync(string.Empty);
                var items = new List<string> { AllSegmentOption };
                items.AddRange(lookup.Segments);
                SegmentRouteFilterComboBox.ItemsSource = items;
            }
            catch
            {
                SegmentRouteFilterComboBox.ItemsSource = new[] { AllSegmentOption };
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            SegmentRouteFilterComboBox.SelectedIndex = 0;
            _segmentReady = true;
        }

        private async Task EnsureSegmentPmFilterOptionsAsync()
        {
            if (_segmentPmReady) return;
            _suppressFilterEvents = true;
            try
            {
                if (_segmentPmByRoute.Count == 0)
                {
                    try
                    {
                        var map = await SegmentPmMapService.GetSegmentPmByRouteFromWorkbookAsync(password: "no");
                        _segmentPmByRoute = map.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        _segmentPmByRoute = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                    }
                }

                var items = new List<string> { AllSegmentPmOption };
                items.AddRange(_segmentPmByRoute.Values.SelectMany(x => x));
                _knownSegmentPmOptions = items
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, AllSegmentPmOption, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                items = items
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Prepend(AllSegmentPmOption)
                    .ToList();

                SegmentPmFilterComboBox.ItemsSource = items;
                SegmentPmFilterComboBox.SelectedIndex = 0;
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            _segmentPmReady = true;
            MergeSegmentPmOptionsFromPoints(_all);
        }

        private void MergeSegmentPmOptionsFromPoints(IReadOnlyList<MapPoint> points)
        {
            if (!_segmentPmReady)
            {
                return;
            }

            var current = (SegmentPmFilterComboBox.ItemsSource as IEnumerable<string>) ?? new[] { AllSegmentPmOption };
            var merged = current
                .Concat(points.Select(x => x.SegmentPm))
                .Where(x => !string.IsNullOrWhiteSpace(x) && !string.Equals(x, "-", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!merged.Contains(AllSegmentPmOption, StringComparer.OrdinalIgnoreCase))
            {
                merged.Insert(0, AllSegmentPmOption);
            }
            else
            {
                merged = merged
                    .Where(x => !string.Equals(x, AllSegmentPmOption, StringComparison.OrdinalIgnoreCase))
                    .Prepend(AllSegmentPmOption)
                    .ToList();
            }

            var selected = SegmentPmFilterComboBox.SelectedItem as string;
            _suppressFilterEvents = true;
            try
            {
                SegmentPmFilterComboBox.ItemsSource = merged;
                SegmentPmFilterComboBox.SelectedItem = merged.FirstOrDefault(x =>
                    string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)) ?? AllSegmentPmOption;
            }
            finally
            {
                _suppressFilterEvents = false;
            }
        }

        private string NormalizeSegmentPmForUi(string? rawValue)
        {
            var text = rawValue?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "-", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var known = TryMatchKnownSegmentPm(text);
            if (!string.IsNullOrWhiteSpace(known))
            {
                return known;
            }

            var dateMatch = DateLikeRegex.Match(text);
            if (dateMatch.Success && dateMatch.Index > 0)
            {
                text = text[..dateMatch.Index].TrimEnd();
            }

            var idPairMatch = SegmentIdPairRegex.Match(text);
            if (idPairMatch.Success)
            {
                return idPairMatch.Value.Replace("/", "-", StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
            }

            var hardSplitIndex = text.IndexOf('&');
            if (hardSplitIndex > 0)
            {
                text = text[..hardSplitIndex].Trim();
            }

            while (text.EndsWith(")", StringComparison.Ordinal) || text.EndsWith("]", StringComparison.Ordinal) || text.EndsWith(".", StringComparison.Ordinal))
            {
                text = text[..^1].TrimEnd();
            }

            return text;
        }

        private string TryMatchKnownSegmentPm(string source)
        {
            if (_knownSegmentPmOptions.Count == 0)
            {
                return string.Empty;
            }

            var normalizedSource = NormalizeSegmentPmLookupToken(source);
            if (string.IsNullOrWhiteSpace(normalizedSource))
            {
                return string.Empty;
            }

            var best = string.Empty;
            var bestLength = -1;
            foreach (var option in _knownSegmentPmOptions)
            {
                var normalizedOption = NormalizeSegmentPmLookupToken(option);
                if (normalizedOption.Length < 4)
                {
                    continue;
                }

                if (!normalizedSource.Contains(normalizedOption, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalizedOption.Length > bestLength)
                {
                    best = option;
                    bestLength = normalizedOption.Length;
                }
            }

            return best;
        }

        private static string NormalizeSegmentPmLookupToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value
                .Trim()
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();
            return new string(chars);
        }

        private async Task LoadSiteLinksAsync()
        {
            try
            {
                _siteLinks = (await SegmentPmMapService.GetLinksAsync()).ToList();
                MergeSegmentRouteOptionsFromSites(_siteLinks);
            }
            catch (Exception ex)
            {
                _siteLinks = new List<SegmentPmMapService.SegmentSiteLinkPoint>();
                DeveloperDiagnostics.LogError("LiveMapPage.LoadSiteLinksAsync", ex);
            }
        }

        private void MergeSegmentRouteOptionsFromSites(IReadOnlyList<SegmentPmMapService.SegmentSiteLinkPoint> links)
        {
            try
            {
                var current = (SegmentRouteFilterComboBox.ItemsSource as IEnumerable<string>) ?? new[] { AllSegmentOption };
                var merged = current
                    .Concat(links.Select(x => x.RouteSegment))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)                    .ToList();

                if (!merged.Contains(AllSegmentOption, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Insert(0, AllSegmentOption);
                }
                else
                {
                    merged = merged
                        .Where(x => !string.Equals(x, AllSegmentOption, StringComparison.OrdinalIgnoreCase))
                        .Prepend(AllSegmentOption)                        .ToList();
                }

                var selected = SegmentRouteFilterComboBox.SelectedItem as string;
                _suppressFilterEvents = true;
                try
                {
                    SegmentRouteFilterComboBox.ItemsSource = merged;
                    SegmentRouteFilterComboBox.SelectedItem = merged.FirstOrDefault(x =>
                        string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)) ?? AllSegmentOption;
                }
                finally
                {
                    _suppressFilterEvents = false;
                }
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.MergeSegmentRouteOptionsFromSites", ex);
            }
        }

        private async Task ApplySmartFilterAsync()
        {
            var cts = BeginRenderRequest();
            var token = cts.Token;
            SetMapRenderProgressVisible(true, 5, "Menyiapkan filter Live Map...", true);

            try
            {
                var query = GetSmartQueryText();
                var segment = GetSegment();
                var segmentPm = GetSegmentPm();
                var quick = QuickFilterComboBox.SelectedItem as string;
                var from = FromDatePicker.Date;
                var to = ToDatePicker.Date;
                var status = GetStatus();
                var severity = GetSeverity();
                var heatmapOn = HeatmapToggleSwitch.IsOn;
                var showSiteMarkers = SiteMarkersToggleSwitch.IsOn;
                var showSiteLines = SiteLinesToggleSwitch.IsOn;
                var useRoadRouteToggle = _useRoadRoute;

                var allSnapshot = _all.ToList();
                var siteLinksSnapshot = _siteLinks.ToList();
                var segmentPmByRouteSnapshot = _segmentPmByRoute.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                var progress = new Progress<RenderProgressInfo>(x =>
                {
                    SetMapRenderProgressVisible(true, x.Percent, x.Message, false);
                });

                var prepared = await Task.Run(() =>
                    PrepareRenderData(
                        allSnapshot,
                        siteLinksSnapshot,
                        segmentPmByRouteSnapshot,
                        query,
                        quick,
                        segment,
                        segmentPm,
                        from,
                        to,
                        status,
                        severity,
                        useRoadRouteToggle,
                        heatmapOn,
                        showSiteMarkers,
                        showSiteLines,
                        progress,
                        token));

                if (prepared is null || token.IsCancellationRequested)
                {
                    return;
                }

                _last = prepared.FilteredMarkers;
                _payloadJson = prepared.PayloadJson;
                MapFilterSummaryTextBlock.Text = prepared.SummaryText;
                ScheduleSnapshotSave();

                await EnsureWebViewReadyAsync();
                if (!_mapLoaded)
                {
                    MapWebView.NavigateToString(BuildMapHtml());
                }
                else
                {
                    await PushToMapAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.ApplySmartFilterAsync", ex);
            }
            finally
            {
                EndRenderRequest(cts);
            }
        }

        private CancellationTokenSource BeginRenderRequest()
        {
            var next = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _renderCts, next);
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

            return next;
        }

        private void EndRenderRequest(CancellationTokenSource owner)
        {
            if (!ReferenceEquals(_renderCts, owner))
            {
                return;
            }

            owner.Dispose();
            _renderCts = null;
            SetMapRenderProgressVisible(false, 0, string.Empty, false);
        }

        private void DebounceSmartFilterApply(int delayMs = 180)
        {
            _smartFilterDebounceCts?.Cancel();
            _smartFilterDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _smartFilterDebounceCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs);
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    _ = DispatcherQueue.TryEnqueue(async () =>
                    {
                        await ApplySmartFilterAsync();
                    });
                }
                finally
                {
                    if (ReferenceEquals(_smartFilterDebounceCts, cts))
                    {
                        cts.Dispose();
                        _smartFilterDebounceCts = null;
                    }
                }
            });
        }

        private void SetMapRenderProgressVisible(bool visible, double percent, string message, bool indeterminate)
        {
            MapRenderProgressBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            MapRenderProgressTextBlock.Text = string.IsNullOrWhiteSpace(message) ? "Memproses data Live Map..." : message;
            MapRenderProgressBar.IsIndeterminate = indeterminate;
            if (!indeterminate)
            {
                var safe = Math.Max(0, Math.Min(100, percent));
                MapRenderProgressBar.Value = safe;
            }
            else
            {
                MapRenderProgressBar.Value = 0;
            }
        }

        private void ScheduleSnapshotSave(int delayMs = 280)
        {
            _snapshotSaveDebounceCts?.Cancel();
            _snapshotSaveDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _snapshotSaveDebounceCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, cts.Token);
                    await SaveSnapshotAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogError("LiveMapPage.ScheduleSnapshotSave", ex);
                }
                finally
                {
                    if (ReferenceEquals(_snapshotSaveDebounceCts, cts))
                    {
                        cts.Dispose();
                        _snapshotSaveDebounceCts = null;
                    }
                }
            });
        }

        private async Task SaveSnapshotAsync(CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = BuildSnapshot();
                Directory.CreateDirectory(AppDataDirectoryPath);
                await using var stream = new FileStream(LiveMapSnapshotFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.SaveSnapshotAsync", ex);
            }
        }

        private LiveMapSnapshot BuildSnapshot()
        {
            var signature = GetCurrentSourceSignature();
            var segmentPmByRoute = _segmentPmByRoute.ToDictionary(
                x => x.Key,
                x => (x.Value ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            return new LiveMapSnapshot
            {
                Version = LiveMapSnapshotVersion,
                SavedAtUtcTicks = DateTimeOffset.UtcNow.Ticks,
                TtFormsDbLastWriteUtcTicks = signature.TtFormsDbLastWriteUtcTicks,
                SegmentPmDbLastWriteUtcTicks = signature.SegmentPmDbLastWriteUtcTicks,
                MapPoints = _all.ToList(),
                SiteLinks = _siteLinks.ToList(),
                SegmentPmByRoute = segmentPmByRoute,
                KnownSegmentPmOptions = _knownSegmentPmOptions
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                LastPayloadJson = _payloadJson,
                LastSummaryText = MapFilterSummaryTextBlock.Text ?? string.Empty
            };
        }

        private async Task<(bool Restored, bool IsFresh)> TryRestoreSnapshotAsync()
        {
            if (!File.Exists(LiveMapSnapshotFilePath))
            {
                return (false, false);
            }

            try
            {
                await using var stream = new FileStream(LiveMapSnapshotFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var snapshot = await JsonSerializer.DeserializeAsync<LiveMapSnapshot>(stream, JsonOptions);
                if (snapshot is null || !string.Equals(snapshot.Version, LiveMapSnapshotVersion, StringComparison.Ordinal))
                {
                    return (false, false);
                }

                if ((snapshot.MapPoints?.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(snapshot.LastPayloadJson))
                {
                    return (false, false);
                }

                _all = snapshot.MapPoints ?? new List<MapPoint>();
                _siteLinks = snapshot.SiteLinks ?? new List<SegmentPmMapService.SegmentSiteLinkPoint>();
                _segmentPmByRoute = (snapshot.SegmentPmByRoute ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(
                        x => x.Key,
                        x => (IReadOnlyList<string>)(x.Value ?? new List<string>())
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => v.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        StringComparer.OrdinalIgnoreCase);
                _knownSegmentPmOptions = (snapshot.KnownSegmentPmOptions ?? new List<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!string.IsNullOrWhiteSpace(snapshot.LastPayloadJson))
                {
                    _payloadJson = snapshot.LastPayloadJson;
                }
                else
                {
                    _payloadJson = "{\"markers\":[],\"heatmap\":false}";
                }

                _last = _all.ToList();
                MergeSegmentRouteOptionsFromSites(_siteLinks);

                var current = GetCurrentSourceSignature();
                _loadedSourceSignature = current;
                var isFresh =
                    snapshot.TtFormsDbLastWriteUtcTicks == current.TtFormsDbLastWriteUtcTicks &&
                    snapshot.SegmentPmDbLastWriteUtcTicks == current.SegmentPmDbLastWriteUtcTicks;

                if (!string.IsNullOrWhiteSpace(snapshot.LastSummaryText))
                {
                    MapFilterSummaryTextBlock.Text = snapshot.LastSummaryText + (isFresh ? " | Cache: fresh" : " | Cache: stale");
                }
                else
                {
                    MapFilterSummaryTextBlock.Text = $"{_all.Count} marker TT (cache), {_siteLinks.Count} line site";
                }

                return (true, isFresh);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.TryRestoreSnapshotAsync", ex);
                return (false, false);
            }
        }

        private static SourceDataSignature GetCurrentSourceSignature()
        {
            return new SourceDataSignature
            {
                TtFormsDbLastWriteUtcTicks = GetFileLastWriteUtcTicksSafe(LocalFormsDbPath),
                SegmentPmDbLastWriteUtcTicks = GetFileLastWriteUtcTicksSafe(SegmentPmDbPath)
            };
        }

        private static bool IsSameSourceSignature(SourceDataSignature left, SourceDataSignature right)
        {
            return left.TtFormsDbLastWriteUtcTicks == right.TtFormsDbLastWriteUtcTicks &&
                   left.SegmentPmDbLastWriteUtcTicks == right.SegmentPmDbLastWriteUtcTicks;
        }

        private static long GetFileLastWriteUtcTicksSafe(string path)
        {
            try
            {
                return File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path).Ticks
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static RenderPreparedData? PrepareRenderData(
            IReadOnlyList<MapPoint> allSnapshot,
            IReadOnlyList<SegmentPmMapService.SegmentSiteLinkPoint> siteLinksSnapshot,
            IReadOnlyDictionary<string, IReadOnlyList<string>> segmentPmByRouteSnapshot,
            string? query,
            string? quick,
            string? segment,
            string? segmentPm,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? status,
            string? severity,
            bool useRoadRouteToggle,
            bool heatmapOn,
            bool showSiteMarkers,
            bool showSiteLines,
            IProgress<RenderProgressInfo>? progress,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            progress?.Report(new RenderProgressInfo(18, "Memfilter marker TT..."));
            var filtered = FilterPoints(allSnapshot, query, quick, segment, segmentPm, from, to, status, severity);

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            progress?.Report(new RenderProgressInfo(42, "Memfilter line site..."));
            var filteredSiteLinks = FilterSiteLinks(siteLinksSnapshot, segmentPmByRouteSnapshot, query, segment, segmentPm);
            var useRoadRoute = useRoadRouteToggle && HasSpecificSegmentPm(segmentPm);

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            Dictionary<string, List<GeoCoordinatePayload>> cpWaypointsBySegment;
            if (useRoadRoute)
            {
                progress?.Report(new RenderProgressInfo(66, "Menyusun waypoint CP..."));
                cpWaypointsBySegment = BuildCpWaypointsBySegment(allSnapshot, filteredSiteLinks);
            }
            else
            {
                progress?.Report(new RenderProgressInfo(66, "Mode straight-line aktif..."));
                cpWaypointsBySegment = new Dictionary<string, List<GeoCoordinatePayload>>(StringComparer.OrdinalIgnoreCase);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            progress?.Report(new RenderProgressInfo(84, "Menyusun payload render..."));
            var payloadJson = JsonSerializer.Serialize(new MapPayload
            {
                Markers = filtered,
                Heatmap = heatmapOn,
                SiteLinks = filteredSiteLinks,
                ShowSiteMarkers = showSiteMarkers,
                ShowSiteLines = showSiteLines,
                UseRoadRoute = useRoadRoute,
                CpWaypointsBySegment = cpWaypointsBySegment
            }, JsonOptions);

            var lineMode = useRoadRoute ? "road-route" : "straight-line";
            var summaryText =
                $"{filtered.Count} marker TT, {filteredSiteLinks.Count} line site tampil | " +
                $"Data: {allSnapshot.Count} marker TT, {siteLinksSnapshot.Count} line site | Mode: {lineMode}";

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            progress?.Report(new RenderProgressInfo(96, "Mengirim data ke peta..."));
            return new RenderPreparedData(filtered, payloadJson, summaryText);
        }

        private string GetSmartQueryText()
        {
            var overlayText = OverlaySmartFilterAutoSuggestBox?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(overlayText))
            {
                return overlayText;
            }

            return SmartFilterAutoSuggestBox.Text ?? string.Empty;
        }

        private void SetSmartQueryText(string value)
        {
            var safe = value ?? string.Empty;
            SmartFilterAutoSuggestBox.Text = safe;
            if (!string.Equals(OverlaySmartFilterAutoSuggestBox.Text, safe, StringComparison.Ordinal))
            {
                OverlaySmartFilterAutoSuggestBox.Text = safe;
            }
        }

        private string? GetSegment() => SegmentRouteFilterComboBox.SelectedItem as string ?? SegmentRouteFilterComboBox.Text;
        private string? GetSegmentPm() => SegmentPmFilterComboBox.SelectedItem as string ?? SegmentPmFilterComboBox.Text;
        private string? GetStatus() => StatusFilterComboBox.SelectedItem as string is string s && !s.Equals(AllStatusOption, StringComparison.OrdinalIgnoreCase) ? s : null;
        private string? GetSeverity() => SeverityFilterComboBox.SelectedItem as string is string s && !s.Equals(AllSeverityOption, StringComparison.OrdinalIgnoreCase) ? s : null;
        private static bool HasSpecificSegmentPm(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, AllSegmentPmOption, StringComparison.OrdinalIgnoreCase);

        private static List<SegmentPmMapService.SegmentSiteLinkPoint> FilterSiteLinks(
            IReadOnlyList<SegmentPmMapService.SegmentSiteLinkPoint> src,
            IReadOnlyDictionary<string, IReadOnlyList<string>> segmentPmByRoute,
            string? query,
            string? segment,
            string? segmentPm)
        {
            IEnumerable<SegmentPmMapService.SegmentSiteLinkPoint> q = src;
            if (!string.IsNullOrWhiteSpace(segment) && !segment.Equals(AllSegmentOption, StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(x => Contains(x.RouteSegment, segment));
            }
            if (HasSpecificSegmentPm(segmentPm))
            {
                q = q.Where(x => RouteHasSegmentPm(x.RouteSegment, segmentPm!, segmentPmByRoute));
            }

            var text = (query ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in terms)
                {
                    var i = t.IndexOf(':');
                    if (i <= 0 || i == t.Length - 1)
                    {
                        q = q.Where(x =>
                            Contains(x.RouteSegment, t) ||
                            Contains(x.UniqueId, t) ||
                            Contains(x.SiteAId, t) ||
                            Contains(x.SiteBId, t) ||
                            Contains(x.SiteAName, t) ||
                            Contains(x.SiteBName, t));
                        continue;
                    }

                    var k = t[..i].Trim().ToLowerInvariant();
                    var v = t[(i + 1)..].Trim();
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        continue;
                    }

                    q = k switch
                    {
                        "seg" => q.Where(x => Contains(x.RouteSegment, v)),
                        "site" => q.Where(x => Contains(x.SiteAId, v) || Contains(x.SiteBId, v) || Contains(x.SiteAName, v) || Contains(x.SiteBName, v)),
                        "uid" => q.Where(x => Contains(x.UniqueId, v)),
                        _ => q.Where(x => Contains(x.RouteSegment, v) || Contains(x.SiteAId, v) || Contains(x.SiteBId, v))
                    };
                }
            }

            return q.ToList();
        }

        private static bool RouteHasSegmentPm(
            string? routeSegment,
            string segmentPm,
            IReadOnlyDictionary<string, IReadOnlyList<string>> segmentPmByRoute)
        {
            if (string.IsNullOrWhiteSpace(routeSegment) || string.IsNullOrWhiteSpace(segmentPm))
            {
                return false;
            }

            return segmentPmByRoute.TryGetValue(routeSegment, out var values) &&
                values.Any(x => string.Equals(x, segmentPm, StringComparison.OrdinalIgnoreCase));
        }

        private static List<MapPoint> FilterPoints(IReadOnlyList<MapPoint> src, string? query, string? quick, string? segment, string? segmentPm, DateTimeOffset? from, DateTimeOffset? to, string? status, string? severity)
        {
            IEnumerable<MapPoint> q = src;
            if (!string.IsNullOrWhiteSpace(segment) && !segment.Equals(AllSegmentOption, StringComparison.OrdinalIgnoreCase)) q = q.Where(x => Contains(x.SegmentRoute, segment));
            if (HasSpecificSegmentPm(segmentPm)) q = q.Where(x => Contains(x.SegmentPm, segmentPm!));
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => Contains(x.Status, status));
            if (!string.IsNullOrWhiteSpace(severity)) q = q.Where(x => Contains(x.Severity, severity));
            var today = DateTimeOffset.Now.Date;
            q = quick switch { "Hari Ini" => q.Where(x => x.DispatchAt.Date == today), "7 Hari Terakhir" => q.Where(x => x.DispatchAt.Date >= today.AddDays(-7)), "30 Hari Terakhir" => q.Where(x => x.DispatchAt.Date >= today.AddDays(-30)), _ => q };
            if (from.HasValue) q = q.Where(x => x.DispatchAt.Date >= from.Value.Date);
            if (to.HasValue) q = q.Where(x => x.DispatchAt.Date <= to.Value.Date);
            var text = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return q.ToList();
            var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in terms)
            {
                var i = t.IndexOf(':');
                if (i <= 0 || i == t.Length - 1)
                {
                    q = q.Where(x => Contains(x.TtIoh, t) || Contains(x.Title, t) || Contains(x.SegmentRoute, t) || Contains(x.SegmentPm, t) || Contains(x.CutPoint, t) || Contains(x.Status, t) || Contains(x.Severity, t) || Contains(x.DispatchTime, t));
                    continue;
                }
                var k = t[..i].Trim().ToLowerInvariant();
                var v = t[(i + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(v)) continue;
                q = k switch
                {
                    "tt" => q.Where(x => Contains(x.TtIoh, v)),
                    "seg" => q.Where(x => Contains(x.SegmentRoute, v)),
                    "spm" => q.Where(x => Contains(x.SegmentPm, v)),
                    "cp" => q.Where(x => Contains(x.CutPoint, v)),
                    "status" => q.Where(x => Contains(x.Status, v)),
                    "sev" => q.Where(x => Contains(x.Severity, v)),
                    "from" when TryParseDate(v, out var d1) => q.Where(x => x.DispatchAt.Date >= d1.Date),
                    "to" when TryParseDate(v, out var d2) => q.Where(x => x.DispatchAt.Date <= d2.Date),
                    "date" when TryParseDate(v, out var d3) => q.Where(x => x.DispatchAt.Date == d3.Date),
                    _ => q.Where(x => Contains(x.TtIoh, v) || Contains(x.SegmentRoute, v) || Contains(x.CutPoint, v))
                };
            }
            return q.ToList();
        }

        private static bool TryParseDate(string value, out DateTimeOffset parsed)
        {
            parsed = default;
            var formats = new[] { "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy/MM/dd" };
            foreach (var f in formats) if (DateTimeOffset.TryParseExact(value, f, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed)) return true;
            return DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed) || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed);
        }

        private static bool Contains(string source, string key) => !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(key) && source.Contains(key, StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, List<GeoCoordinatePayload>> BuildCpWaypointsBySegment(
            IReadOnlyList<MapPoint> points,
            IReadOnlyList<SegmentPmMapService.SegmentSiteLinkPoint> links)
        {
            var result = new Dictionary<string, List<GeoCoordinatePayload>>(StringComparer.OrdinalIgnoreCase);
            if (points.Count == 0 || links.Count == 0)
            {
                return result;
            }

            var activeSegments = links
                .Select(x => NormalizeSegmentToken(x.RouteSegment))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (activeSegments.Count == 0)
            {
                return result;
            }

            var grouped = points
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.SegmentRoute) &&
                    activeSegments.Contains(NormalizeSegmentToken(x.SegmentRoute)) &&
                    !string.IsNullOrWhiteSpace(x.CutPoint) &&
                    !string.Equals(x.CutPoint, "-", StringComparison.Ordinal) &&
                    x.Latitude is >= -90 and <= 90 &&
                    x.Longitude is >= -180 and <= 180)
                .GroupBy(x => NormalizeSegmentToken(x.SegmentRoute), StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped)
            {
                var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var waypoints = group
                    .OrderByDescending(x => x.DispatchAt)
                    .Select(x =>
                    {
                        var key =
                            x.Latitude.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                            x.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
                        if (!dedup.Add(key))
                        {
                            return null;
                        }

                        return new GeoCoordinatePayload
                        {
                            Latitude = x.Latitude,
                            Longitude = x.Longitude
                        };
                    })
                    .Where(x => x is not null)
                    .Cast<GeoCoordinatePayload>()
                    .ToList();

                if (waypoints.Count > 0)
                {
                    result[group.Key] = waypoints;
                }
            }

            return result;
        }

        private static string NormalizeSegmentToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
        }

        private List<string> BuildSmartSuggestions(string? text)
        {
            var input = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(input)) return new() { "tt:", "seg:", "cp:", "status:Open", "sev:Critical", "from:" + DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            var idx = input.LastIndexOf(' ');
            var head = idx >= 0 ? input[..(idx + 1)] : string.Empty;
            var token = idx >= 0 ? input[(idx + 1)..] : input;
            var c = token.IndexOf(':');
            if (c <= 0) return SmartKeywords.Where(k => k.StartsWith(token, StringComparison.OrdinalIgnoreCase) || token.Length <= 1).Select(k => head + k).Take(12).ToList();
            var key = token[..c].Trim().ToLowerInvariant();
            var val = token[(c + 1)..].Trim();
            IEnumerable<string> vals = key switch
            {
                "tt" => _all.Select(x => x.TtIoh),
                "seg" => _all.Select(x => x.SegmentRoute),
                "spm" => _all.Select(x => x.SegmentPm),
                "cp" => _all.Select(x => x.CutPoint),
                "status" => _all.Select(x => x.Status),
                "sev" => _all.Select(x => x.Severity),
                _ => Enumerable.Empty<string>()
            };
            if (!string.IsNullOrWhiteSpace(val)) vals = vals.Where(x => x.Contains(val, StringComparison.OrdinalIgnoreCase));
            return vals.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).Select(x => head + key + ":" + x).ToList();
        }

        private static bool TryParseCoordinate(string text, out double lat, out double lon)
        {
            lat = 0; lon = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            if (CoordinateFormatService.TryParseAnyToDecimal(t, out lat, out lon) && IsValid(lat, lon))
            {
                return true;
            }

            var split = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length >= 2 && TryParseFlexibleDouble(split[0], out lat) && TryParseFlexibleDouble(split[1], out lon) && IsValid(lat, lon))
            {
                return true;
            }

            if (split.Length >= 2 && TryParseDmsToken(split[0], out lat) && TryParseDmsToken(split[1], out lon) && IsValid(lat, lon))
            {
                return true;
            }

            return false;
        }

        private static bool TryParseFlexibleDouble(string text, out double value)
        {
            value = 0;
            var c = (text ?? string.Empty).Trim();
            return double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(c, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(c.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseDmsToken(string token, out double value)
        {
            value = 0;
            var u = (token ?? string.Empty).Trim().ToUpperInvariant();
            if (!DmsDirectionRegex.IsMatch(u)) return false;
            var m = NumberRegex.Matches(u);
            if (m.Count == 0 || !TryParseFlexibleDouble(m[0].Value, out var d)) return false;
            var mm = m.Count > 1 && TryParseFlexibleDouble(m[1].Value, out var minute) ? minute : 0d;
            var ss = m.Count > 2 && TryParseFlexibleDouble(m[2].Value, out var second) ? second : 0d;
            value = Math.Abs(d) + mm / 60d + ss / 3600d;
            if (u.Contains("S") || u.Contains("W")) value *= -1d;
            return true;
        }

        private static bool IsValid(double lat, double lon) => lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

        private static string BuildMapHtml() => """
<!doctype html>
<html>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1'>
  <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
  <link rel='stylesheet' href='https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.css'/>
  <link rel='stylesheet' href='https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.Default.css'/>
  <style>
    html,body,#map{height:100%;margin:0}
    .leaflet-pane .leaflet-marker-icon.map-marker-3d{background:transparent;border:0}
    .marker3d-wrap{position:relative;width:26px;height:34px;transform-style:preserve-3d;animation:markerFloat 2.8s ease-in-out infinite}
    .marker3d-shadow{position:absolute;left:3px;bottom:1px;width:20px;height:7px;border-radius:50%;background:rgba(9,18,34,.28);filter:blur(1.2px);transform:translateZ(-1px)}
    .marker3d-stem{position:absolute;left:11px;bottom:5px;width:4px;height:14px;border-radius:5px;background:linear-gradient(180deg,#ffffff,#d9e8ff);box-shadow:0 0 0 1px rgba(12,28,56,.14)}
    .marker3d-core{position:absolute;left:5px;top:2px;width:16px;height:16px;border-radius:50%;background:radial-gradient(circle at 30% 28%,#ffffff 0%,#d7ebff 24%,#6eb4ff 62%,#205fb5 100%);box-shadow:0 3px 10px rgba(16,74,151,.44), inset 0 1px 0 rgba(255,255,255,.85)}
    .marker3d-glow{position:absolute;left:-1px;top:-4px;width:24px;height:24px;border-radius:50%;background:radial-gradient(circle,#8fd0ff 0%,rgba(143,208,255,.16) 54%,rgba(143,208,255,0) 75%);animation:markerGlow 1.9s ease-out infinite}
    .marker3d-pulse{position:absolute;left:1px;top:-2px;width:22px;height:22px;border-radius:50%;border:2px solid rgba(73,165,255,.52);animation:markerPulse 1.7s ease-out infinite}
    .marker3d-wrap.status-critical .marker3d-core{background:radial-gradient(circle at 30% 28%,#fff5f5 0%,#ffd7d7 24%,#ff7f7f 62%,#be1f3b 100%);box-shadow:0 3px 11px rgba(183,34,67,.44), inset 0 1px 0 rgba(255,255,255,.78)}
    .marker3d-wrap.status-critical .marker3d-glow{background:radial-gradient(circle,#ff9fb3 0%,rgba(255,159,179,.18) 56%,rgba(255,159,179,0) 76%)}
    .marker3d-wrap.status-critical .marker3d-pulse{border-color:rgba(255,117,145,.58)}
    .marker3d-wrap.status-major .marker3d-core{background:radial-gradient(circle at 30% 28%,#fff8ef 0%,#ffe0ba 24%,#ffb15a 62%,#c46d1e 100%);box-shadow:0 3px 11px rgba(193,111,30,.42), inset 0 1px 0 rgba(255,255,255,.78)}
    .marker3d-wrap.status-major .marker3d-glow{background:radial-gradient(circle,#ffc98a 0%,rgba(255,201,138,.2) 56%,rgba(255,201,138,0) 76%)}
    .marker3d-wrap.status-major .marker3d-pulse{border-color:rgba(255,177,94,.58)}
    .marker3d-wrap.status-cancel .marker3d-core{background:radial-gradient(circle at 30% 28%,#f6f8fc 0%,#e2e8f2 24%,#aab7c7 62%,#647587 100%);box-shadow:0 3px 9px rgba(52,72,96,.36), inset 0 1px 0 rgba(255,255,255,.78)}
    .marker3d-wrap.status-cancel .marker3d-glow{background:radial-gradient(circle,#b4c3d8 0%,rgba(180,195,216,.18) 56%,rgba(180,195,216,0) 76%)}
    .marker3d-wrap.status-cancel .marker3d-pulse{border-color:rgba(170,183,199,.54)}
    .leaflet-marker-icon.map-marker-3d:hover .marker3d-wrap{animation-duration:1.3s;transform:scale(1.08)}
    @keyframes markerPulse{0%{transform:scale(.6);opacity:.85}70%{transform:scale(1.45);opacity:.06}100%{transform:scale(1.5);opacity:0}}
    @keyframes markerGlow{0%,100%{opacity:.74}50%{opacity:.18}}
    @keyframes markerFloat{0%,100%{transform:translateY(0)}50%{transform:translateY(-3px)}}
    .leaflet-popup-content{margin:10px 12px;font:12px 'Segoe UI',sans-serif}
    .tt-card{min-width:270px;max-width:320px}
    .tt-ioh{display:inline-block;margin-bottom:8px;padding:2px 8px;border-radius:999px;background:#eef6ff;border:1px solid #b9d9ff;color:#1e5fa8;font-size:12px;font-weight:700;text-decoration:none}
    .tt-ioh:hover{text-decoration:underline}
    .tt-grid{display:grid;grid-template-columns:92px 1fr;gap:4px 8px;margin-bottom:8px}
    .tt-label{color:#5b6e81}
    .tt-value{color:#20374d;font-weight:600}
    .tt-pill{display:inline-block;background:#e9f3ff;color:#1c4d79;border:1px solid #b8d8ff;border-radius:999px;padding:2px 8px;font-size:11px;margin-bottom:8px}
    .tt-actions{display:flex;gap:8px}
    .tt-btn{display:inline-flex;align-items:center;justify-content:center;padding:6px 10px;border-radius:8px;border:1px solid #1e5fa8;background:#1f6fca;color:#ffffff !important;-webkit-text-fill-color:#ffffff;text-decoration:none;font-size:12px;font-weight:700;cursor:pointer;line-height:1}
    .tt-btn:visited,.tt-btn:hover,.tt-btn:active{color:#ffffff !important;-webkit-text-fill-color:#ffffff;text-decoration:none}
    .tt-btn:hover{filter:brightness(0.96)}
    .site-marker{background:transparent;border:0}
    .site-dot{width:12px;height:12px;border-radius:50%;background:#12b886;border:2px solid #ffffff;box-shadow:0 0 0 1px rgba(18,184,134,.55),0 1px 6px rgba(0,0,0,.28)}
    .site-line-a{color:#15aabf}
    .leaflet-overlay-pane path.road-route-halo{stroke:#0b4870 !important;stroke-width:9px !important;stroke-opacity:.45 !important;stroke-linecap:round;stroke-linejoin:round;filter:drop-shadow(0 0 5px rgba(11,72,112,.45));pointer-events:none}
    .leaflet-overlay-pane path.road-route-core{stroke:#12b8e6 !important;stroke-width:5px !important;stroke-opacity:.96 !important;stroke-linecap:round;stroke-linejoin:round;filter:drop-shadow(0 0 4px rgba(18,184,230,.46))}
    .leaflet-overlay-pane path.road-route-animated{stroke:#ecffff !important;stroke-width:3px !important;stroke-opacity:.95 !important;stroke-dasharray:14 12;stroke-dashoffset:0;stroke-linecap:round;stroke-linejoin:round;animation:roadFlow 1.25s linear infinite;pointer-events:none}
    .leaflet-top.leaflet-left{margin-top:56px}
    @keyframes roadFlow{from{stroke-dashoffset:0}to{stroke-dashoffset:-52}}
  </style>
</head>
<body>
  <div id='map'></div>
  <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
  <script src='https://unpkg.com/leaflet.markercluster@1.5.3/dist/leaflet.markercluster.js'></script>
  <script src='https://unpkg.com/leaflet.heat@0.2.0/dist/leaflet-heat.js'></script>
  <script>
    const map=L.map('map',{preferCanvas:true}).setView([-2.5489,118.0149],5);
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; OpenStreetMap contributors'}).addTo(map);
    const cluster=L.markerClusterGroup({chunkedLoading:true,chunkDelay:35,chunkInterval:120,showCoverageOnHover:false,spiderfyOnMaxZoom:true});
    const siteCluster=L.markerClusterGroup({chunkedLoading:true,chunkDelay:20,chunkInterval:80,showCoverageOnHover:false,spiderfyOnMaxZoom:true,disableClusteringAtZoom:14});
    map.addLayer(cluster);
    map.addLayer(siteCluster);
    const siteLineLayer=L.layerGroup().addTo(map);
    const roadRouteRenderer=L.svg({padding:0.5});
    let heat=null;

    const esc = v => String(v ?? '-').replace(/[&<>"']/g, m => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[m] || m));
    function openMaps(url){
      if(window.chrome && window.chrome.webview){
        window.chrome.webview.postMessage({openMaps:url});
        return false;
      }
      window.open(url,'_blank');
      return false;
    }
    function openDetail(tt){
      if(window.chrome && window.chrome.webview){
        window.chrome.webview.postMessage({openDetail:tt});
        return false;
      }
      return false;
    }
    function popupHtml(i){
      const lat=Number(i.latitude);
      const lon=Number(i.longitude);
      const c=(Number.isFinite(lat)&&Number.isFinite(lon)) ? lat.toFixed(6)+', '+lon.toFixed(6) : '-';
      const mapsUrl='https://www.google.com/maps?q='+encodeURIComponent(lat+','+lon);
      return `<div class='tt-card'>
        <a class='tt-ioh' href='#' onclick='return openDetail("${esc(i.ttIoh||"-")}")'>${esc(i.ttIoh||'-')}</a>
          <div class='tt-pill'>${esc(i.status||'-')} / ${esc(i.severity||'-')}</div>
        <div class='tt-grid'>
          <div class='tt-label'>Segment</div><div class='tt-value'>${esc(i.segmentRoute||'-')}</div>
          <div class='tt-label'>Segment PM</div><div class='tt-value'>${esc(i.segmentPm||'-')}</div>
          <div class='tt-label'>Cut Point</div><div class='tt-value'>${esc(i.cutPoint||'-')}</div>
          <div class='tt-label'>Dispatch</div><div class='tt-value'>${esc(i.dispatchTime||'-')}</div>
          <div class='tt-label'>Coordinate</div><div class='tt-value'>${esc(c)}</div>
        </div>
        <div class='tt-actions'>
          <a class='tt-btn' href='#' onclick='return openMaps("${mapsUrl}")'>Google Maps</a>
        </div>
      </div>`;
    }
    function markerClass(i){
      const s=String(i.severity||'').toLowerCase();
      const st=String(i.status||'').toLowerCase();
      if(s.includes('critical')) return 'status-critical';
      if(s.includes('major')) return 'status-major';
      if(st.includes('cancel')) return 'status-cancel';
      return 'status-open';
    }
    function create3DIcon(i){
      const cls=markerClass(i);
      const html=`<div class="marker3d-wrap ${cls}"><div class="marker3d-shadow"></div><div class="marker3d-stem"></div><div class="marker3d-glow"></div><div class="marker3d-pulse"></div><div class="marker3d-core"></div></div>`;
      return L.divIcon({className:'map-marker-3d',html,iconSize:[26,34],iconAnchor:[13,30],popupAnchor:[0,-22]});
    }
    function siteIcon(){
      return L.divIcon({className:'site-marker',html:'<div class=\"site-dot\"></div>',iconSize:[12,12],iconAnchor:[6,6],popupAnchor:[0,-6]});
    }
    function normalizeSiteToken(v){
      return String(v || '').trim().toUpperCase();
    }
    function buildSiteKey(siteId, siteName, lat, lon){
      const id=normalizeSiteToken(siteId) || normalizeSiteToken(siteName);
      if(id) return id;
      return `${Number(lat).toFixed(6)}|${Number(lon).toFixed(6)}`;
    }
    function buildLineKey(route, aLat, aLon, bLat, bLon){
      const p1=`${Number(aLat).toFixed(6)},${Number(aLon).toFixed(6)}`;
      const p2=`${Number(bLat).toFixed(6)},${Number(bLon).toFixed(6)}`;
      const ordered=p1 <= p2 ? `${p1}|${p2}` : `${p2}|${p1}`;
      return `${normalizeSiteToken(route)}|${ordered}`;
    }
    function buildRoadKey(aLat, aLon, bLat, bLon, waypoints){
      const p1=`${Number(aLat).toFixed(6)},${Number(aLon).toFixed(6)}`;
      const p2=`${Number(bLat).toFixed(6)},${Number(bLon).toFixed(6)}`;
      const wp = Array.isArray(waypoints) && waypoints.length > 0
        ? waypoints.map(w => `${Number(w.latitude).toFixed(6)},${Number(w.longitude).toFixed(6)}`).join(';')
        : 'none';
      const edge = p1 <= p2 ? `${p1}|${p2}` : `${p2}|${p1}`;
      return `${edge}|${wp}`;
    }
    function linePopupHtml(l){
      return `<div><b>${esc(l.routeSegment||'-')}</b><br/>${esc(l.siteAId||l.siteAName||'-')} -> ${esc(l.siteBId||l.siteBName||'-')}</div>`;
    }
    function drawLine(path, l, roadStyle){
      if(roadStyle){
        const halo=L.polyline(path,{renderer:roadRouteRenderer,className:'road-route-halo',color:'#0b4870',weight:9,opacity:.45,lineCap:'round',lineJoin:'round'});
        const core=L.polyline(path,{renderer:roadRouteRenderer,className:'road-route-core',color:'#12b8e6',weight:5,opacity:.96,lineCap:'round',lineJoin:'round'});
        const flow=L.polyline(path,{renderer:roadRouteRenderer,className:'road-route-animated',color:'#ecffff',weight:3,opacity:.95,dashArray:'14 12',lineCap:'round',lineJoin:'round'});
        core.bindPopup(linePopupHtml(l));
        siteLineLayer.addLayer(halo);
        siteLineLayer.addLayer(core);
        siteLineLayer.addLayer(flow);
        return;
      }
      const line=L.polyline(path,{color:'#15aabf',weight:3,opacity:.9,lineCap:'round',lineJoin:'round'});
      line.bindPopup(linePopupHtml(l));
      siteLineLayer.addLayer(line);
    }
    const roadPathCache = new Map();
    const MAX_ROAD_LINE_COUNT = 80;
    function normalizeSegmentKey(v){
      return String(v || '').trim().replace(/\s+/g,' ').toUpperCase();
    }
    function getSegmentWaypoints(routeSegment, cpBySegment){
      if(!cpBySegment || typeof cpBySegment !== 'object'){
        return [];
      }
      const key = normalizeSegmentKey(routeSegment);
      if(!key){
        return [];
      }
      const arr = cpBySegment[key];
      return Array.isArray(arr) ? arr : [];
    }
    function collectWaypointsForLine(waypoints, aLat, aLon, bLat, bLon){
      if(!Array.isArray(waypoints) || waypoints.length === 0){
        return [];
      }
      const dx = bLon - aLon;
      const dy = bLat - aLat;
      const len2 = (dx*dx) + (dy*dy);
      const scored = [];
      for(const w of waypoints){
        const wLat = Number(w && w.latitude);
        const wLon = Number(w && w.longitude);
        if(!Number.isFinite(wLat) || !Number.isFinite(wLon)){
          continue;
        }
        let t = 0.5;
        let perp = Math.hypot(wLon - aLon, wLat - aLat);
        if(len2 > 1e-12){
          t = ((wLon - aLon) * dx + (wLat - aLat) * dy) / len2;
          const projLon = aLon + (t * dx);
          const projLat = aLat + (t * dy);
          perp = Math.hypot(wLon - projLon, wLat - projLat);
        }
        const endpointPenalty = Math.min(
          Math.hypot(wLat-aLat, wLon-aLon),
          Math.hypot(wLat-bLat, wLon-bLon));
        const score = perp + (0.15 * endpointPenalty);
        scored.push({ latitude:wLat, longitude:wLon, t, score });
      }
      return scored
        .sort((a,b) => (a.t - b.t) || (a.score - b.score))
        .sort((a,b) => a.t - b.t)
        .map(x => ({ latitude:x.latitude, longitude:x.longitude }));
    }
    async function fetchRoadPath(aLat, aLon, bLat, bLon, waypoints){
      const key = buildRoadKey(aLat, aLon, bLat, bLon, waypoints);
      if(roadPathCache.has(key)){
        return roadPathCache.get(key);
      }
      const fallback = [[aLat,aLon],[bLat,bLon]];
      const waypointCoord = Array.isArray(waypoints) && waypoints.length > 0
        ? ';' + waypoints.map(w => `${Number(w.longitude)},${Number(w.latitude)}`).join(';')
        : '';
      const url = `https://router.project-osrm.org/route/v1/driving/${aLon},${aLat}${waypointCoord};${bLon},${bLat}?overview=full&geometries=geojson`;
      try{
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 4500);
        const response = await fetch(url, { signal: controller.signal });
        clearTimeout(timeoutId);
        if(!response.ok){
          roadPathCache.set(key, fallback);
          return fallback;
        }
        const body = await response.json();
        const coords = body && body.routes && body.routes[0] && body.routes[0].geometry && Array.isArray(body.routes[0].geometry.coordinates)
          ? body.routes[0].geometry.coordinates
          : null;
        if(!coords || coords.length < 2){
          roadPathCache.set(key, fallback);
          return fallback;
        }
        const latLonPath = coords.map(c => [Number(c[1]), Number(c[0])]).filter(p => Number.isFinite(p[0]) && Number.isFinite(p[1]));
        const path = latLonPath.length >= 2 ? latLonPath : fallback;
        roadPathCache.set(key, path);
        return path;
      }catch{
        roadPathCache.set(key, fallback);
        return fallback;
      }
    }
    let renderVersion = 0;
    async function render(data,on,links,showSiteMarkers,showSiteLines,useRoadRoute,cpBySegment){
      const currentRenderVersion = ++renderVersion;
      cluster.clearLayers();
      siteCluster.clearLayers();
      siteLineLayer.clearLayers();
      if(heat){map.removeLayer(heat);heat=null;}
      const hp=[];
      const bounds=[];
      for(const i of data){
        if(typeof i.latitude!=='number'||typeof i.longitude!=='number') continue;
        const m=L.marker([i.latitude,i.longitude],{icon:create3DIcon(i),riseOnHover:true});
        m.bindPopup(popupHtml(i),{maxWidth:340});
        cluster.addLayer(m);
        bounds.push([i.latitude,i.longitude]);
        const severity=String(i.severity||'').toLowerCase();
        const weight=severity.includes('critical') ? 1 : severity.includes('major') ? 0.82 : severity.includes('minor') ? 0.68 : 0.6;
        hp.push([i.latitude,i.longitude,weight]);
      }
      const markerDedup=new Set();
      const lineDedup=new Set();
      const lineItems=[];
      if(Array.isArray(links)){
        for(const l of links){
          const aOk=Number.isFinite(Number(l.latitudeA))&&Number.isFinite(Number(l.longitudeA));
          const bOk=Number.isFinite(Number(l.latitudeB))&&Number.isFinite(Number(l.longitudeB));
          if(!aOk||!bOk) continue;
          const aLat=Number(l.latitudeA), aLon=Number(l.longitudeA), bLat=Number(l.latitudeB), bLon=Number(l.longitudeB);
          if(showSiteLines){
            const lineKey=buildLineKey(l.routeSegment, aLat, aLon, bLat, bLon);
            if(!lineDedup.has(lineKey)){
              lineDedup.add(lineKey);
              lineItems.push({ l, aLat, aLon, bLat, bLon });
            }
          }
          if(showSiteMarkers){
            const aKey=buildSiteKey(l.siteAId, l.siteAName, aLat, aLon);
            const bKey=buildSiteKey(l.siteBId, l.siteBName, bLat, bLon);
            if(!markerDedup.has(aKey)){
              markerDedup.add(aKey);
              const ma=L.marker([aLat,aLon],{icon:siteIcon(),riseOnHover:true});
              ma.bindPopup(`<div><b>Site A</b><br/>${esc(l.siteAId||l.siteAName||'-')}<br/>${esc(l.routeSegment||'-')}</div>`);
              siteCluster.addLayer(ma);
            }
            if(!markerDedup.has(bKey)){
              markerDedup.add(bKey);
              const mb=L.marker([bLat,bLon],{icon:siteIcon(),riseOnHover:true});
              mb.bindPopup(`<div><b>Site B</b><br/>${esc(l.siteBId||l.siteBName||'-')}<br/>${esc(l.routeSegment||'-')}</div>`);
              siteCluster.addLayer(mb);
            }
          }
          bounds.push([aLat,aLon]); bounds.push([bLat,bLon]);
        }
      }
      if(showSiteLines){
        if(useRoadRoute){
          const routedCount = Math.min(lineItems.length, MAX_ROAD_LINE_COUNT);
          const routed = lineItems.slice(0, routedCount);
          const straight = lineItems.slice(routedCount);
          const paths = await Promise.all(routed.map(item => {
            const waypoints = getSegmentWaypoints(item.l.routeSegment, cpBySegment);
            const selectedWaypoints = collectWaypointsForLine(waypoints, item.aLat, item.aLon, item.bLat, item.bLon);
            return fetchRoadPath(item.aLat, item.aLon, item.bLat, item.bLon, selectedWaypoints);
          }));
          if(currentRenderVersion !== renderVersion){
            return;
          }
          for(let i=0;i<routed.length;i++){
            drawLine(paths[i], routed[i].l, true);
          }
          for(const item of straight){
            drawLine([[item.aLat,item.aLon],[item.bLat,item.bLon]], item.l, false);
          }
        }else{
          for(const item of lineItems){
            drawLine([[item.aLat,item.aLon],[item.bLat,item.bLon]], item.l, false);
          }
        }
      }
      if(on&&hp.length>0){heat=L.heatLayer(hp,{radius:18,blur:15,minOpacity:0.3,maxZoom:13}).addTo(map);}
      if(bounds.length>0){
        const b=L.latLngBounds(bounds);
        if(b.isValid()) map.fitBounds(b.pad(0.08));
      }
    }

    if(window.chrome&&window.chrome.webview){
      window.chrome.webview.addEventListener('message',e=>{
        const d=e.data;
        if(Array.isArray(d)){render(d,false,[],false,false,false,{});return;}
        render(
          (d&&Array.isArray(d.markers))?d.markers:[],
          !!(d&&d.heatmap),
          (d&&Array.isArray(d.siteLinks))?d.siteLinks:[],
          !!(d&&d.showSiteMarkers),
          !!(d&&d.showSiteLines),
          !!(d&&d.useRoadRoute),
          (d&&typeof d.cpWaypointsBySegment === 'object' && d.cpWaypointsBySegment) ? d.cpWaypointsBySegment : {});
      });
    }
    window.centerToIndonesia=()=>map.setView([-2.5489,118.0149],5);
    window.centerToCoordinate=(lat,lon)=>map.setView([lat,lon],13);
  </script>
</body>
</html>
""";

        private async Task ExecuteMapScriptSafeAsync(string script)
        {
            try { await EnsureWebViewReadyAsync(); await MapWebView.ExecuteScriptAsync(script); } catch (Exception ex) { DeveloperDiagnostics.LogError("LiveMapPage.ExecuteMapScriptSafeAsync", ex); }
        }

        private async Task TryFocusPendingCoordinateAsync()
        {
            var pending = PendingMapFocusService.Consume();
            if (pending is null)
            {
                return;
            }

            var lat = pending.Latitude;
            var lon = pending.Longitude;
            var ttIoh = NormalizeTtIohForMap(pending.TtIoh);
            if (!string.IsNullOrWhiteSpace(ttIoh) && !string.Equals(ttIoh, "-", StringComparison.Ordinal))
            {
                SetSmartQueryText("tt:" + ttIoh);
                await ApplySmartFilterAsync();
            }

            await ExecuteMapScriptSafeAsync($"window.centerToCoordinate({lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});");
        }

        private static string NormalizeTtIohForMap(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "-";
            }

            var match = TtIohNormalizeRegex.Match(text);
            if (match.Success)
            {
                return "INC-" + match.Groups[1].Value + "-" + match.Groups[2].Value;
            }

            return text.ToUpperInvariant();
        }

        private async void MapCenterButton_Click(object sender, RoutedEventArgs e)
        {
            RegisterOverlayActivity();
            await ExecuteMapScriptSafeAsync("window.centerToIndonesia();");
        }

        private void MapSurface_PointerActivity(object sender, PointerRoutedEventArgs e) => RegisterOverlayActivity();
        private void MapOverlaySearchBorder_PointerActivity(object sender, PointerRoutedEventArgs e) => RegisterOverlayActivity();

        private void OverlaySmartFilterAutoSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ShowOverlaySearchBar(resetTimer: false);
            _overlayIdleTimer.Stop();
        }

        private void OverlaySmartFilterAutoSuggestBox_LostFocus(object sender, RoutedEventArgs e) => RegisterOverlayActivity();

        private void MapOverlayRevealButton_Click(object sender, RoutedEventArgs e)
        {
            ShowOverlaySearchBar();
            _ = OverlaySmartFilterAutoSuggestBox.Focus(FocusState.Keyboard);
        }

        private void MapSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            RegisterOverlayActivity();
            var text = sender.Text?.Trim() ?? string.Empty;
            if (TryParseCoordinate(text, out var lat, out var lon))
            {
                _ = ExecuteMapScriptSafeAsync($"window.centerToCoordinate({lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});");
            }
        }
        private void SmartFilterAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                sender.ItemsSource = BuildSmartSuggestions(sender.Text);
                DebounceSmartFilterApply();
            }
        }
        private async void SmartFilterAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => await ApplySmartFilterAsync();
        private async void SmartFilterAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) { if (args.SelectedItem is string s) sender.Text = s; await ApplySmartFilterAsync(); }
        private void OverlaySmartFilterAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            RegisterOverlayActivity();
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                sender.ItemsSource = BuildSmartSuggestions(sender.Text);
                SmartFilterAutoSuggestBox.Text = sender.Text;
                DebounceSmartFilterApply();
            }
        }
        private async void OverlaySmartFilterAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            RegisterOverlayActivity();
            SmartFilterAutoSuggestBox.Text = sender.Text;
            await ApplySmartFilterAsync();
        }
        private async void OverlaySmartFilterAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            RegisterOverlayActivity();
            if (args.SelectedItem is string s)
            {
                sender.Text = s;
            }

            SmartFilterAutoSuggestBox.Text = sender.Text;
            await ApplySmartFilterAsync();
        }
        private async void QuickFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void SegmentRouteFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void SegmentPmFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private void CompactFilterModeCheckBox_Checked(object sender, RoutedEventArgs e) => ApplyCompactFilterMode(CompactFilterModeCheckBox.IsChecked == true);
        private async void ApplyFilterButton_Click(object sender, RoutedEventArgs e) => await ApplySmartFilterAsync();
        private async void FromDatePicker_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs args) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void ToDatePicker_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs args) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void SeverityFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void HeatmapToggleSwitch_Toggled(object sender, RoutedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void SiteMarkersToggleSwitch_Toggled(object sender, RoutedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void SiteLinesToggleSwitch_Toggled(object sender, RoutedEventArgs e) { if (_suppressFilterEvents) return; await ApplySmartFilterAsync(); }
        private async void RoadRouteToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressFilterEvents)
            {
                return;
            }

            _useRoadRoute = RoadRouteToggleSwitch.IsOn;
            await ApplySmartFilterAsync();
        }

        private void ApplyCompactFilterMode(bool compact)
        {
            PrimaryTopFilterGrid.ColumnSpacing = compact ? 6 : 8;
            PrimaryToggleGrid.ColumnSpacing = compact ? 6 : 10;
            PrimaryFilterRootGrid.RowSpacing = compact ? 4 : 6;
            MapActionPanel.Spacing = compact ? 4 : 6;

            PrimaryFromDateColumn.Width = new GridLength(compact ? 118 : 150);
            PrimaryToDateColumn.Width = new GridLength(compact ? 118 : 150);
            PrimaryStatusColumn.Width = new GridLength(compact ? 108 : 130);
            PrimarySeverityColumn.Width = new GridLength(compact ? 108 : 130);

            FromLabelTextBlock.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ToLabelTextBlock.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ImportSegmentPmTextBlock.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ExportCsvTextBlock.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

            HeatmapLabelTextBlock.Text = compact ? "Heat" : "Heatmap";
            SiteMarkerLabelTextBlock.Text = compact ? "Marker" : "Site Marker";
            SiteLineLabelTextBlock.Text = compact ? "Line" : "Site Line";
            RoadRouteLabelTextBlock.Text = compact ? "Route" : "Road Route";

            if (compact && AdvancedSmartFilterExpander.IsExpanded)
            {
                AdvancedSmartFilterExpander.IsExpanded = false;
            }
        }

        private async void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _suppressFilterEvents = true;
            try
            {
                SetSmartQueryText(string.Empty);
                SegmentRouteFilterComboBox.SelectedIndex = 0;
                SegmentPmFilterComboBox.SelectedIndex = 0;
                QuickFilterComboBox.SelectedIndex = 0;
                StatusFilterComboBox.SelectedIndex = 0;
                SeverityFilterComboBox.SelectedIndex = 0;
                if (FromDatePicker.Date.HasValue) FromDatePicker.Date = null;
                if (ToDatePicker.Date.HasValue) ToDatePicker.Date = null;
                HeatmapToggleSwitch.IsOn = false;
                SiteMarkersToggleSwitch.IsOn = false;
                SiteLinesToggleSwitch.IsOn = false;
                RoadRouteToggleSwitch.IsOn = false;
                _useRoadRoute = false;
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            await ApplySmartFilterAsync();
        }

        private async void ExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_last.Count == 0 || App.MainAppWindow is null) return;
                var picker = new FileSavePicker();
                picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
                picker.SuggestedFileName = "livemap_filtered_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                var sb = new StringBuilder("TT IOH,Title,Status,Severity,Segment Route,Cut Point,Dispatch Time,Latitude,Longitude\r\n");
                foreach (var p in _last) sb.AppendLine($"{Esc(p.TtIoh)},{Esc(p.Title)},{Esc(p.Status)},{Esc(p.Severity)},{Esc(p.SegmentRoute)},{Esc(p.CutPoint)},{Esc(p.DispatchTime)},{p.Latitude.ToString(CultureInfo.InvariantCulture)},{p.Longitude.ToString(CultureInfo.InvariantCulture)}");
                await FileIO.WriteTextAsync(file, sb.ToString());
            }
            catch (Exception ex) { DeveloperDiagnostics.LogError("LiveMapPage.ExportCsvButton_Click", ex); }
        }

        private async void ImportSegmentPmButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ImportSegmentPmButton.IsEnabled = false;
                MapFilterSummaryTextBlock.Text = "Mengimpor SEGMENTPM ke SQL cache...";
                var result = await SegmentPmMapService.ImportFromWorkbookAsync(password: "no");
                await LoadSiteLinksAsync();
                await ApplySmartFilterAsync();
                MapFilterSummaryTextBlock.Text =
                    $"Import SEGMENTPM selesai: {result.InsertedLinks} link ({result.ProcessedRows} baris)";
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.ImportSegmentPmButton_Click", ex);
                MapFilterSummaryTextBlock.Text = "Import SEGMENTPM gagal. Cek developer log.";
            }
            finally
            {
                ImportSegmentPmButton.IsEnabled = true;
            }
        }

        private static string Esc(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')) ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        private sealed class LiveMapSnapshot
        {
            public string Version { get; set; } = LiveMapSnapshotVersion;
            public long SavedAtUtcTicks { get; set; }
            public long TtFormsDbLastWriteUtcTicks { get; set; }
            public long SegmentPmDbLastWriteUtcTicks { get; set; }
            public List<MapPoint> MapPoints { get; set; } = new();
            public List<SegmentPmMapService.SegmentSiteLinkPoint> SiteLinks { get; set; } = new();
            public Dictionary<string, List<string>> SegmentPmByRoute { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> KnownSegmentPmOptions { get; set; } = new();
            public string LastPayloadJson { get; set; } = string.Empty;
            public string LastSummaryText { get; set; } = string.Empty;
        }

        private sealed class SourceDataSignature
        {
            public long TtFormsDbLastWriteUtcTicks { get; set; }
            public long SegmentPmDbLastWriteUtcTicks { get; set; }
        }

        private sealed class MapPayload
        {
            public List<MapPoint> Markers { get; set; } = new();
            public bool Heatmap { get; set; }
            public List<SegmentPmMapService.SegmentSiteLinkPoint> SiteLinks { get; set; } = new();
            public bool ShowSiteMarkers { get; set; }
            public bool ShowSiteLines { get; set; }
            public bool UseRoadRoute { get; set; }
            public Dictionary<string, List<GeoCoordinatePayload>> CpWaypointsBySegment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class GeoCoordinatePayload
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
        }

        private sealed class RenderPreparedData
        {
            public RenderPreparedData(List<MapPoint> filteredMarkers, string payloadJson, string summaryText)
            {
                FilteredMarkers = filteredMarkers;
                PayloadJson = payloadJson;
                SummaryText = summaryText;
            }

            public List<MapPoint> FilteredMarkers { get; }
            public string PayloadJson { get; }
            public string SummaryText { get; }
        }

        private sealed class RenderProgressInfo
        {
            public RenderProgressInfo(double percent, string message)
            {
                Percent = percent;
                Message = message;
            }

            public double Percent { get; }
            public string Message { get; }
        }

        private sealed class MapPoint
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public string TtIoh { get; set; } = "-";
            public string Title { get; set; } = "-";
            public string Status { get; set; } = "Unknown";
            public string Severity { get; set; } = "Unknown";
            public string CutPoint { get; set; } = "-";
            public string SegmentRoute { get; set; } = "-";
            public string SegmentPm { get; set; } = "-";
            public string DispatchTime { get; set; } = "-";
            public DateTimeOffset DispatchAt { get; set; }
        }
    }
}

