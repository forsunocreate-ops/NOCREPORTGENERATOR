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
        private List<SegmentPmMapService.SegmentSiteLinkPoint> _siteLinks = new();

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
            await TryFocusPendingCoordinateAsync();
        }

        private async Task LoadMapAsync()
        {
            try
            {
                await EnsureWebViewReadyAsync();
                var records = await LocalFormStorageService.GetCoordinateRecordsAsync();
                _all = records.Select(ToPoint).Where(x => x is not null).Cast<MapPoint>().ToList();
                await LoadSiteLinksAsync();
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
                TtIoh = NormalizeTtIohForMap(r.TtIoh),
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
            SiteMarkersToggleSwitch.IsOn = true;
            SiteLinesToggleSwitch.IsOn = true;
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
                SegmentRouteFilterComboBox.ItemsSource = merged;
                SegmentRouteFilterComboBox.SelectedItem = merged.FirstOrDefault(x =>
                    string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)) ?? AllSegmentOption;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("LiveMapPage.MergeSegmentRouteOptionsFromSites", ex);
            }
        }

        private async Task ApplySmartFilterAsync()
        {
            var query = SmartFilterAutoSuggestBox.Text;
            var segment = GetSegment();
            var filtered = FilterPoints(_all, query, QuickFilterComboBox.SelectedItem as string, segment, FromDatePicker.Date, ToDatePicker.Date, GetStatus(), GetSeverity());
            var filteredSiteLinks = FilterSiteLinks(_siteLinks, query, segment);
            _last = filtered;
            _payloadJson = JsonSerializer.Serialize(new MapPayload
            {
                Markers = filtered,
                Heatmap = HeatmapToggleSwitch.IsOn,
                SiteLinks = filteredSiteLinks,
                ShowSiteMarkers = SiteMarkersToggleSwitch.IsOn,
                ShowSiteLines = SiteLinesToggleSwitch.IsOn
            }, JsonOptions);
            MapFilterSummaryTextBlock.Text =
                $"{filtered.Count} marker TT, {filteredSiteLinks.Count} line site tampil | " +
                $"Data: {_all.Count} marker TT, {_siteLinks.Count} line site";
            await EnsureWebViewReadyAsync();
            if (!_mapLoaded) MapWebView.NavigateToString(BuildMapHtml()); else await PushToMapAsync();
        }

        private string? GetSegment() => SegmentRouteFilterComboBox.SelectedItem as string ?? SegmentRouteFilterComboBox.Text;
        private string? GetStatus() => StatusFilterComboBox.SelectedItem as string is string s && !s.Equals(AllStatusOption, StringComparison.OrdinalIgnoreCase) ? s : null;
        private string? GetSeverity() => SeverityFilterComboBox.SelectedItem as string is string s && !s.Equals(AllSeverityOption, StringComparison.OrdinalIgnoreCase) ? s : null;

        private static List<SegmentPmMapService.SegmentSiteLinkPoint> FilterSiteLinks(
            IReadOnlyList<SegmentPmMapService.SegmentSiteLinkPoint> src,
            string? query,
            string? segment)
        {
            IEnumerable<SegmentPmMapService.SegmentSiteLinkPoint> q = src;
            if (!string.IsNullOrWhiteSpace(segment) && !segment.Equals(AllSegmentOption, StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(x => Contains(x.RouteSegment, segment));
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
    map.addLayer(cluster);
    const siteMarkerLayer=L.layerGroup().addTo(map);
    const siteLineLayer=L.layerGroup().addTo(map);
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
    function render(data,on,links,showSiteMarkers,showSiteLines){
      cluster.clearLayers();
      siteMarkerLayer.clearLayers();
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
      if(Array.isArray(links)){
        for(const l of links){
          const aOk=Number.isFinite(Number(l.latitudeA))&&Number.isFinite(Number(l.longitudeA));
          const bOk=Number.isFinite(Number(l.latitudeB))&&Number.isFinite(Number(l.longitudeB));
          if(!aOk||!bOk) continue;
          const aLat=Number(l.latitudeA), aLon=Number(l.longitudeA), bLat=Number(l.latitudeB), bLon=Number(l.longitudeB);
          if(showSiteLines){
            const line=L.polyline([[aLat,aLon],[bLat,bLon]],{color:'#15aabf',weight:2,opacity:.86});
            line.bindPopup(`<div><b>${esc(l.routeSegment||'-')}</b><br/>${esc(l.siteAId||l.siteAName||'-')} -> ${esc(l.siteBId||l.siteBName||'-')}</div>`);
            siteLineLayer.addLayer(line);
          }
          if(showSiteMarkers){
            const aKey=`A|${aLat.toFixed(6)}|${aLon.toFixed(6)}|${esc(l.siteAId||'')}`;
            const bKey=`B|${bLat.toFixed(6)}|${bLon.toFixed(6)}|${esc(l.siteBId||'')}`;
            if(!markerDedup.has(aKey)){
              markerDedup.add(aKey);
              const ma=L.marker([aLat,aLon],{icon:siteIcon(),riseOnHover:true});
              ma.bindPopup(`<div><b>Site A</b><br/>${esc(l.siteAId||l.siteAName||'-')}<br/>${esc(l.routeSegment||'-')}</div>`);
              siteMarkerLayer.addLayer(ma);
            }
            if(!markerDedup.has(bKey)){
              markerDedup.add(bKey);
              const mb=L.marker([bLat,bLon],{icon:siteIcon(),riseOnHover:true});
              mb.bindPopup(`<div><b>Site B</b><br/>${esc(l.siteBId||l.siteBName||'-')}<br/>${esc(l.routeSegment||'-')}</div>`);
              siteMarkerLayer.addLayer(mb);
            }
          }
          bounds.push([aLat,aLon]); bounds.push([bLat,bLon]);
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
        if(Array.isArray(d)){render(d,false,[],false,false);return;}
        render(
          (d&&Array.isArray(d.markers))?d.markers:[],
          !!(d&&d.heatmap),
          (d&&Array.isArray(d.siteLinks))?d.siteLinks:[],
          !!(d&&d.showSiteMarkers),
          !!(d&&d.showSiteLines));
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
                SmartFilterAutoSuggestBox.Text = "tt:" + ttIoh;
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

        private async void MapCenterButton_Click(object sender, RoutedEventArgs e) => await ExecuteMapScriptSafeAsync("window.centerToIndonesia();");
        private void MapSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var text = sender.Text?.Trim() ?? string.Empty;
            if (TryParseCoordinate(text, out var lat, out var lon))
            {
                _ = ExecuteMapScriptSafeAsync($"window.centerToCoordinate({lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});");
            }
        }
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
        private async void SiteMarkersToggleSwitch_Toggled(object sender, RoutedEventArgs e) => await ApplySmartFilterAsync();
        private async void SiteLinesToggleSwitch_Toggled(object sender, RoutedEventArgs e) => await ApplySmartFilterAsync();

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
            SiteMarkersToggleSwitch.IsOn = true;
            SiteLinesToggleSwitch.IsOn = true;
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

        private sealed class MapPayload
        {
            public List<MapPoint> Markers { get; set; } = new();
            public bool Heatmap { get; set; }
            public List<SegmentPmMapService.SegmentSiteLinkPoint> SiteLinks { get; set; } = new();
            public bool ShowSiteMarkers { get; set; }
            public bool ShowSiteLines { get; set; }
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
            public string DispatchTime { get; set; } = "-";
            public DateTimeOffset DispatchAt { get; set; }
        }
    }
}

