using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class LiveMapPage : Page
    {
        private static readonly Regex NumberRegex = new(@"[-+]?\d+(?:[.,]\d+)?", RegexOptions.Compiled);
        private static readonly Regex DmsDirectionRegex = new(@"[NSEW]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TitleRegex = new(@"\[(?<status>[^-\]]+)\s*-\s*(?<severity>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly string[] SmartKeywords = { "tt:", "seg:", "cp:", "from:", "to:", "date:", "status:", "sev:" };
        private const string AllSegmentOption = "Semua Segment";
        private const string AllStatusOption = "Semua Status";
        private const string AllSeverityOption = "Semua Severity";

        private bool _webViewReady;
        private bool _mapLoaded;
        private bool _filterReady;
        private bool _segmentReady;
        private string _payloadJson = "{\"markers\":[],\"heatmap\":false}";
        private List<MapPoint> _all = new();
        private List<MapPoint> _last = new();

        public LiveMapPage()
        {
            InitializeComponent();
            Loaded += LiveMapPage_Loaded;
        }

        private async void LiveMapPage_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureFilterOptions();
            await EnsureSegmentRouteFilterOptionsAsync();
            await LoadMapAsync();
        }

        private async Task LoadMapAsync()
        {
            try
            {
                await EnsureWebViewReadyAsync();
                var records = await LocalFormStorageService.GetCoordinateRecordsAsync();
                _all = records.Select(ToPoint).Where(x => x is not null).Cast<MapPoint>().ToList();
                await ApplySmartFilterAsync();
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.LoadMapAsync", ex);
            }
        }

        private async Task EnsureWebViewReadyAsync()
        {
            if (_webViewReady) return;
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            MapWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _webViewReady = true;
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

        private static MapPoint? ToPoint(LocalFormStorageService.MapCoordinateRecord r)
        {
            if (r is null || string.IsNullOrWhiteSpace(r.Coordinate)) return null;
            if (!TryParseCoordinate(r.Coordinate, out var lat, out var lon)) return null;
            var m = TitleRegex.Match(r.Title ?? string.Empty);
            var status = m.Success ? m.Groups["status"].Value.Trim() : "Unknown";
            var severity = m.Success ? m.Groups["severity"].Value.Trim() : "Unknown";
            return new MapPoint
            {
                Latitude = lat,
                Longitude = lon,
                TtIoh = string.IsNullOrWhiteSpace(r.TtIoh) ? "-" : r.TtIoh.Trim(),
                Title = string.IsNullOrWhiteSpace(r.Title) ? "-" : r.Title.Trim(),
                Status = string.IsNullOrWhiteSpace(status) ? "Unknown" : status,
                Severity = string.IsNullOrWhiteSpace(severity) ? "Unknown" : severity,
                CutPoint = string.IsNullOrWhiteSpace(r.CutPoint) ? "-" : r.CutPoint.Trim(),
                SegmentRoute = string.IsNullOrWhiteSpace(r.SegmentRoute) ? "-" : r.SegmentRoute.Trim(),
                DispatchAt = r.DispatchDateTime == default ? DateTimeOffset.MinValue : r.DispatchDateTime,
                DispatchTime = r.DispatchDateTime == default ? "-" : r.DispatchDateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture)
            };
        }

        private void EnsureFilterOptions()
        {
            if (_filterReady) return;
            QuickFilterComboBox.ItemsSource = new[] { "Semua Waktu", "Hari Ini", "7 Hari Terakhir", "30 Hari Terakhir" };
            QuickFilterComboBox.SelectedIndex = 0;
            StatusFilterComboBox.ItemsSource = new[] { AllStatusOption, "Open", "Close", "Monitoring", "Unknown" };
            StatusFilterComboBox.SelectedIndex = 0;
            SeverityFilterComboBox.ItemsSource = new[] { AllSeverityOption, "Critical", "Major", "Minor", "Unknown" };
            SeverityFilterComboBox.SelectedIndex = 0;
            HeatmapToggleSwitch.IsOn = false;
            _filterReady = true;
        }

        private async Task EnsureSegmentRouteFilterOptionsAsync()
        {
            if (_segmentReady) return;
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
            SegmentRouteFilterComboBox.SelectedIndex = 0;
            _segmentReady = true;
        }

        private async Task ApplySmartFilterAsync()
        {
            var filtered = FilterPoints(_all, SmartFilterAutoSuggestBox.Text, QuickFilterComboBox.SelectedItem as string, GetSegment(), FromDatePicker.Date, ToDatePicker.Date, GetStatus(), GetSeverity());
            _last = filtered;
            _payloadJson = JsonSerializer.Serialize(new MapPayload { Markers = filtered, Heatmap = HeatmapToggleSwitch.IsOn }, JsonOptions);
            MapFilterSummaryTextBlock.Text = $"{filtered.Count} marker tampil dari {_all.Count} data koordinat";
            await EnsureWebViewReadyAsync();
            if (!_mapLoaded) MapWebView.NavigateToString(BuildMapHtml()); else await PushToMapAsync();
        }

        private string? GetSegment() => SegmentRouteFilterComboBox.SelectedItem as string ?? SegmentRouteFilterComboBox.Text;
        private string? GetStatus() => StatusFilterComboBox.SelectedItem as string is string s && !s.Equals(AllStatusOption, StringComparison.OrdinalIgnoreCase) ? s : null;
        private string? GetSeverity() => SeverityFilterComboBox.SelectedItem as string is string s && !s.Equals(AllSeverityOption, StringComparison.OrdinalIgnoreCase) ? s : null;

        private static List<MapPoint> FilterPoints(IReadOnlyList<MapPoint> src, string? query, string? quick, string? segment, DateTimeOffset? from, DateTimeOffset? to, string? status, string? severity)
        {
            IEnumerable<MapPoint> q = src;
            if (!string.IsNullOrWhiteSpace(segment) && !segment.Equals(AllSegmentOption, StringComparison.OrdinalIgnoreCase)) q = q.Where(x => Contains(x.SegmentRoute, segment));
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
                    q = q.Where(x => Contains(x.TtIoh, t) || Contains(x.Title, t) || Contains(x.SegmentRoute, t) || Contains(x.CutPoint, t) || Contains(x.Status, t) || Contains(x.Severity, t) || Contains(x.DispatchTime, t));
                    continue;
                }
                var k = t[..i].Trim().ToLowerInvariant();
                var v = t[(i + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(v)) continue;
                q = k switch
                {
                    "tt" => q.Where(x => Contains(x.TtIoh, v)),
                    "seg" => q.Where(x => Contains(x.SegmentRoute, v)),
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
            var split = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length >= 2 && TryParseFlexibleDouble(split[0], out lat) && TryParseFlexibleDouble(split[1], out lon) && IsValid(lat, lon)) return true;
            if (split.Length >= 2 && TryParseDmsToken(split[0], out lat) && TryParseDmsToken(split[1], out lon) && IsValid(lat, lon)) return true;
            var m = NumberRegex.Matches(t);
            if (m.Count < 2) return false;
            for (var i = 0; i < m.Count - 1; i++) if (TryParseFlexibleDouble(m[i].Value, out var la) && TryParseFlexibleDouble(m[i + 1].Value, out var lo) && IsValid(la, lo)) { lat = la; lon = lo; return true; }
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

        private static string BuildMapHtml() => @"<!doctype html><html><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'><link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/><link rel='stylesheet' href='https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.css'/><link rel='stylesheet' href='https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.Default.css'/><style>html,body,#map{height:100%;margin:0} .leaflet-popup-content{font:12px 'Segoe UI',sans-serif;}</style></head><body><div id='map'></div><script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script><script src='https://unpkg.com/leaflet.markercluster@1.5.3/dist/leaflet.markercluster.js'></script><script src='https://unpkg.com/leaflet.heat@0.2.0/dist/leaflet-heat.js'></script><script>const map=L.map('map',{preferCanvas:true}).setView([-2.5489,118.0149],5);L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; OpenStreetMap contributors'}).addTo(map);const cluster=L.markerClusterGroup({chunkedLoading:true,chunkDelay:35,chunkInterval:120,showCoverageOnHover:false,spiderfyOnMaxZoom:true});map.addLayer(cluster);let heat=null;function render(data,on){cluster.clearLayers();if(heat){map.removeLayer(heat);heat=null;}const hp=[];for(const i of data){if(typeof i.latitude!=='number'||typeof i.longitude!=='number')continue;const m=L.marker([i.latitude,i.longitude]);m.bindPopup('<b>'+(i.ttIoh||'-')+'</b><br/>Status/Severity: '+(i.status||'-')+' / '+(i.severity||'-')+'<br/>Cut Point: '+(i.cutPoint||'-')+'<br/>Segment: '+(i.segmentRoute||'-')+'<br/>Dispatch: '+(i.dispatchTime||'-'));cluster.addLayer(m);hp.push([i.latitude,i.longitude,0.6]);}if(on&&hp.length>0){heat=L.heatLayer(hp,{radius:18,blur:15,minOpacity:0.3,maxZoom:13}).addTo(map);}if(cluster.getLayers().length>0){const b=cluster.getBounds();if(b.isValid())map.fitBounds(b.pad(0.08));}}if(window.chrome&&window.chrome.webview){window.chrome.webview.addEventListener('message',e=>{const d=e.data;if(Array.isArray(d)){render(d,false);return;}render((d&&Array.isArray(d.markers))?d.markers:[],!!(d&&d.heatmap));});}window.centerToIndonesia=()=>map.setView([-2.5489,118.0149],5);window.centerToCoordinate=(lat,lon)=>map.setView([lat,lon],13);</script></body></html>";

        private async Task ExecuteMapScriptSafeAsync(string script)
        {
            try { await EnsureWebViewReadyAsync(); await MapWebView.ExecuteScriptAsync(script); } catch (Exception ex) { DeveloperDiagnostics.LogError("LiveMapPage.ExecuteMapScriptSafeAsync", ex); }
        }

        private async void MapCenterButton_Click(object sender, RoutedEventArgs e) => await ExecuteMapScriptSafeAsync("window.centerToIndonesia();");
        private void MapSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) { var t = sender.Text?.Trim() ?? string.Empty; var s = t.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); if (s.Length >= 2 && TryParseFlexibleDouble(s[0], out var lat) && TryParseFlexibleDouble(s[1], out var lon)) _ = ExecuteMapScriptSafeAsync($"window.centerToCoordinate({lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});"); }
        private async void SmartFilterAutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) sender.ItemsSource = BuildSmartSuggestions(sender.Text); await ApplySmartFilterAsync(); }
        private async void SmartFilterAutoSuggestBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => await ApplySmartFilterAsync();
        private async void SmartFilterAutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) { if (args.SelectedItem is string s) sender.Text = s; await ApplySmartFilterAsync(); }
        private async void QuickFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ApplySmartFilterAsync();
        private async void SegmentRouteFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ApplySmartFilterAsync();
        private async void ApplyFilterButton_Click(object sender, RoutedEventArgs e) => await ApplySmartFilterAsync();
        private async void FromDatePicker_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs args) => await ApplySmartFilterAsync();
        private async void ToDatePicker_DateChanged(object sender, CalendarDatePickerDateChangedEventArgs args) => await ApplySmartFilterAsync();
        private async void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ApplySmartFilterAsync();
        private async void SeverityFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ApplySmartFilterAsync();
        private async void HeatmapToggleSwitch_Toggled(object sender, RoutedEventArgs e) => await ApplySmartFilterAsync();

        private async void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            SmartFilterAutoSuggestBox.Text = string.Empty;
            SegmentRouteFilterComboBox.SelectedIndex = 0;
            QuickFilterComboBox.SelectedIndex = 0;
            StatusFilterComboBox.SelectedIndex = 0;
            SeverityFilterComboBox.SelectedIndex = 0;
            if (FromDatePicker.Date.HasValue) FromDatePicker.Date = null;
            if (ToDatePicker.Date.HasValue) ToDatePicker.Date = null;
            HeatmapToggleSwitch.IsOn = false;
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

        private static string Esc(string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')) ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        private sealed class MapPayload { public List<MapPoint> Markers { get; set; } = new(); public bool Heatmap { get; set; } }
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
            public string DispatchTime { get; set; } = "-";
            public DateTimeOffset DispatchAt { get; set; }
        }
    }
}
