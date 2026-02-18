using ExcelDataReader;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class SegmentPmMapService
    {
        private static readonly string[] SheetNameMustContainAny =
        {
            "fiberassetmaster",
            "newlink"
        };
        private static readonly string[] HeaderScoreHints =
        {
            "routesegment",
            "siteida",
            "siteidb",
            "lata",
            "longa",
            "latb",
            "longb"
        };
        private static readonly string[] RoutePrefixes = { "routesegment2025", "routesegment2024", "routesegment", "route" };
        private static readonly string[] UniqueIdPrefixes = { "uniqueid", "uid" };
        private static readonly string[] SiteAIdPrefixes = { "siteida", "sitecodea", "sitea" };
        private static readonly string[] SiteANamePrefixes = { "sitenamea", "namaa", "siteaname" };
        private static readonly string[] LonAPrefixes = { "longa", "lnga", "longitudea", "lona" };
        private static readonly string[] LatAPrefixes = { "lata", "latitudea" };
        private static readonly string[] SiteBIdPrefixes = { "siteidb", "sitecodeb", "siteb" };
        private static readonly string[] SiteBNamePrefixes = { "sitenameb", "namab", "sitebname" };
        private static readonly string[] LonBPrefixes = { "longb", "lngb", "longitudeb", "lonb" };
        private static readonly string[] LatBPrefixes = { "latb", "latitudeb" };
        private static readonly SemaphoreSlim DbGate = new(1, 1);
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOCREPORTGENERATOR");
        private static readonly string DbPath = Path.Combine(DirectoryPath, "segmentpm_map.db");
        private static readonly string ConnectionString = "Data Source=" + DbPath + ";Cache=Shared;Pooling=True";
        private static bool _initialized;

        public static async Task<SegmentPmImportResult> ImportFromWorkbookAsync(
            string? filePath = null,
            string password = "no",
            CancellationToken cancellationToken = default)
        {
            var resolvedPath = ResolveSourcePath(filePath);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                throw new FileNotFoundException("File SEGMENTPM tidak ditemukan.", resolvedPath);
            }

            await EnsureInitializedAsync();

            var parsed = await Task.Run(
                () => ParseWorkbookLinks(resolvedPath, password, cancellationToken),
                cancellationToken);

            await SaveLinksAsync(parsed.Links, resolvedPath, cancellationToken);

            return new SegmentPmImportResult
            {
                SourcePath = resolvedPath,
                ProcessedSheets = parsed.ProcessedSheets,
                ProcessedRows = parsed.ProcessedRows,
                InsertedLinks = parsed.Links.Count
            };
        }

        private static ParsedImportLinks ParseWorkbookLinks(string resolvedPath, string password, CancellationToken cancellationToken)
        {
            var allLinks = new List<SegmentLinkRecord>();
            var parsedRows = 0;
            var sheetProcessed = 0;

            using var stream = File.Open(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
            {
                Password = password,
                FallbackEncoding = Encoding.GetEncoding(1252),
                LeaveOpen = false
            });

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sheetName = reader.Name ?? string.Empty;
                if (!IsTargetSheet(sheetName))
                {
                    continue;
                }

                var linkCountBeforeSheet = allLinks.Count;
                var rows = ReadAllRows(reader);
                if (rows.Count == 0)
                {
                    continue;
                }

                var headerRow = GuessHeaderRow(rows);
                var headerMap = BuildHeaderMap(rows[headerRow]);

                for (var rowIndex = headerRow + 1; rowIndex < rows.Count; rowIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = rows[rowIndex];
                    parsedRows++;
                    foreach (var link in ExtractLinksFromRow(sheetName, rowIndex + 1, row, headerMap))
                    {
                        allLinks.Add(link);
                    }
                }

                var addedOnSheet = allLinks.Count - linkCountBeforeSheet;
                DeveloperDiagnostics.LogInfo(
                    "SegmentPM import sheet '" + sheetName +
                    "' parsed rows=" + Math.Max(0, rows.Count - (headerRow + 1)).ToString(CultureInfo.InvariantCulture) +
                    ", links=" + addedOnSheet.ToString(CultureInfo.InvariantCulture));
                sheetProcessed++;
            }
            while (reader.NextResult());

            var deduped = allLinks
                .Where(x => !string.IsNullOrWhiteSpace(x.RouteSegment))
                .GroupBy(x => x.DedupKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            DeveloperDiagnostics.LogInfo(
                "SegmentPM import complete: raw links=" + allLinks.Count.ToString(CultureInfo.InvariantCulture) +
                ", deduped links=" + deduped.Count.ToString(CultureInfo.InvariantCulture) +
                ", sheets=" + sheetProcessed.ToString(CultureInfo.InvariantCulture));

            return new ParsedImportLinks(deduped, sheetProcessed, parsedRows);
        }

        public static async Task<IReadOnlyList<SegmentSiteLinkPoint>> GetLinksAsync()
        {
            await EnsureInitializedAsync();
            await DbGate.WaitAsync();
            try
            {
                var list = new List<SegmentSiteLinkPoint>();
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT route_segment, unique_id, site_a_id, site_a_name, lat_a, lon_a, site_b_id, site_b_name, lat_b, lon_b " +
                    "FROM segmentpm_links ORDER BY route_segment, unique_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new SegmentSiteLinkPoint
                    {
                        RouteSegment = r.IsDBNull(0) ? string.Empty : r.GetString(0),
                        UniqueId = r.IsDBNull(1) ? string.Empty : r.GetString(1),
                        SiteAId = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                        SiteAName = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                        LatitudeA = r.IsDBNull(4) ? 0d : r.GetDouble(4),
                        LongitudeA = r.IsDBNull(5) ? 0d : r.GetDouble(5),
                        SiteBId = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                        SiteBName = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                        LatitudeB = r.IsDBNull(8) ? 0d : r.GetDouble(8),
                        LongitudeB = r.IsDBNull(9) ? 0d : r.GetDouble(9)
                    });
                }

                return list;
            }
            finally
            {
                DbGate.Release();
            }
        }

        public static Task<IReadOnlyList<string>> GetSegmentPmOptionsFromWorkbookAsync(
            string? filePath = null,
            string password = "no",
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                var resolvedPath = ResolveSourcePath(filePath);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    return (IReadOnlyList<string>)Array.Empty<string>();
                }

                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var stream = File.Open(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
                {
                    Password = password,
                    FallbackEncoding = Encoding.GetEncoding(1252),
                    LeaveOpen = false
                });

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sheetName = reader.Name ?? string.Empty;
                    if (!IsTargetSheet(sheetName))
                    {
                        continue;
                    }

                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Column H (1-based) => index 7 (0-based)
                        var text = reader.FieldCount > 7 ? reader.GetValue(7)?.ToString()?.Trim() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "-", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        values.Add(text);
                    }
                }
                while (reader.NextResult());

                return (IReadOnlyList<string>)values
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, cancellationToken);
        }

        public static Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetSegmentPmByRouteFromWorkbookAsync(
            string? filePath = null,
            string password = "no",
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                var resolvedPath = ResolveSourcePath(filePath);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    return (IReadOnlyDictionary<string, IReadOnlyList<string>>)new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                }

                var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                using var stream = File.Open(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
                {
                    Password = password,
                    FallbackEncoding = Encoding.GetEncoding(1252),
                    LeaveOpen = false
                });

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sheetName = reader.Name ?? string.Empty;
                    if (!IsTargetSheet(sheetName))
                    {
                        continue;
                    }

                    var rows = ReadAllRows(reader);
                    if (rows.Count == 0)
                    {
                        continue;
                    }

                    var headerRow = GuessHeaderRow(rows);
                    var headerMap = BuildHeaderMap(rows[headerRow]);
                    var routeIndex = FindFirstIndexByPrefixes(headerMap, RoutePrefixes);
                    if (!routeIndex.HasValue)
                    {
                        continue;
                    }

                    for (var rowIndex = headerRow + 1; rowIndex < rows.Count; rowIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = rows[rowIndex];
                        if (routeIndex.Value < 0 || routeIndex.Value >= row.Length)
                        {
                            continue;
                        }

                        var route = row[routeIndex.Value]?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(route) || string.Equals(route, "-", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // Column H (1-based) => index 7 (0-based)
                        var segmentPm = row.Length > 7 ? row[7]?.Trim() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(segmentPm) || string.Equals(segmentPm, "-", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!map.TryGetValue(route, out var values))
                        {
                            values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            map[route] = values;
                        }

                        values.Add(segmentPm);
                    }
                }
                while (reader.NextResult());

                return (IReadOnlyDictionary<string, IReadOnlyList<string>>)map.ToDictionary(
                    x => x.Key,
                    x => (IReadOnlyList<string>)x.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            }, cancellationToken);
        }

        private static async Task SaveLinksAsync(List<SegmentLinkRecord> links, string sourcePath, CancellationToken cancellationToken)
        {
            await DbGate.WaitAsync(cancellationToken);
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var tx = connection.BeginTransaction();

                using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = tx;
                    clear.CommandText = "DELETE FROM segmentpm_links;";
                    clear.ExecuteNonQuery();
                }

                using (var clearMeta = connection.CreateCommand())
                {
                    clearMeta.Transaction = tx;
                    clearMeta.CommandText = "DELETE FROM segmentpm_meta;";
                    clearMeta.ExecuteNonQuery();
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO segmentpm_links " +
                    "(route_segment, unique_id, site_a_id, site_a_name, lat_a, lon_a, site_b_id, site_b_name, lat_b, lon_b, source_sheet, source_row) " +
                    "VALUES ($route_segment, $unique_id, $site_a_id, $site_a_name, $lat_a, $lon_a, $site_b_id, $site_b_name, $lat_b, $lon_b, $source_sheet, $source_row);";

                var pRoute = insert.CreateParameter(); pRoute.ParameterName = "$route_segment"; insert.Parameters.Add(pRoute);
                var pUid = insert.CreateParameter(); pUid.ParameterName = "$unique_id"; insert.Parameters.Add(pUid);
                var pA = insert.CreateParameter(); pA.ParameterName = "$site_a_id"; insert.Parameters.Add(pA);
                var pAName = insert.CreateParameter(); pAName.ParameterName = "$site_a_name"; insert.Parameters.Add(pAName);
                var pLatA = insert.CreateParameter(); pLatA.ParameterName = "$lat_a"; insert.Parameters.Add(pLatA);
                var pLonA = insert.CreateParameter(); pLonA.ParameterName = "$lon_a"; insert.Parameters.Add(pLonA);
                var pB = insert.CreateParameter(); pB.ParameterName = "$site_b_id"; insert.Parameters.Add(pB);
                var pBName = insert.CreateParameter(); pBName.ParameterName = "$site_b_name"; insert.Parameters.Add(pBName);
                var pLatB = insert.CreateParameter(); pLatB.ParameterName = "$lat_b"; insert.Parameters.Add(pLatB);
                var pLonB = insert.CreateParameter(); pLonB.ParameterName = "$lon_b"; insert.Parameters.Add(pLonB);
                var pSheet = insert.CreateParameter(); pSheet.ParameterName = "$source_sheet"; insert.Parameters.Add(pSheet);
                var pRow = insert.CreateParameter(); pRow.ParameterName = "$source_row"; insert.Parameters.Add(pRow);

                foreach (var link in links)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pRoute.Value = link.RouteSegment;
                    pUid.Value = link.UniqueId;
                    pA.Value = link.SiteAId;
                    pAName.Value = link.SiteAName;
                    pLatA.Value = link.LatitudeA;
                    pLonA.Value = link.LongitudeA;
                    pB.Value = link.SiteBId;
                    pBName.Value = link.SiteBName;
                    pLatB.Value = link.LatitudeB;
                    pLonB.Value = link.LongitudeB;
                    pSheet.Value = link.SourceSheet;
                    pRow.Value = link.SourceRow;
                    insert.ExecuteNonQuery();
                }

                using (var meta = connection.CreateCommand())
                {
                    meta.Transaction = tx;
                    meta.CommandText = "INSERT INTO segmentpm_meta (source_path, imported_at, total_links) VALUES ($path, $at, $count);";
                    meta.Parameters.AddWithValue("$path", sourcePath);
                    meta.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
                    meta.Parameters.AddWithValue("$count", links.Count);
                    meta.ExecuteNonQuery();
                }

                tx.Commit();
            }
            finally
            {
                DbGate.Release();
            }
        }

        private static string ResolveSourcePath(string? input)
        {
            if (!string.IsNullOrWhiteSpace(input) && File.Exists(input))
            {
                return input;
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SEGMENTPM.xlsx"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "File Source", "SEGMENTPM.xlsx"),
                Path.Combine(AppContext.BaseDirectory, "SEGMENTPM.xlsx"),
                Path.Combine(AppContext.BaseDirectory, "File Source", "SEGMENTPM.xlsx"),
                Path.Combine(Environment.CurrentDirectory, "SEGMENTPM.xlsx"),
                Path.Combine(Environment.CurrentDirectory, "File Source", "SEGMENTPM.xlsx")
            };

            var exact = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            var searchRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "File Source"),
                AppContext.BaseDirectory,
                Path.Combine(AppContext.BaseDirectory, "File Source"),
                Environment.CurrentDirectory,
                Path.Combine(Environment.CurrentDirectory, "File Source")
            };
            var patterns = new[]
            {
                "SEGMENTPM*.xlsx",
                "*SEGMENTPM*.xlsx",
                "SEGMENT PM*.xlsx",
                "*SEGMENT PM*.xlsx",
                "SEGMENTPM*.xlsm",
                "*SEGMENTPM*.xlsm",
                "SEGMENTPM*.xlsb",
                "*SEGMENTPM*.xlsb"
            };

            var discovered = new List<string>();
            foreach (var root in searchRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var pattern in patterns)
                {
                    discovered.AddRange(Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly));
                }
            }

            return discovered
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? string.Empty;
        }

        private static bool IsTargetSheet(string name)
        {
            var normalized = NormalizeHeader(name);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return SheetNameMustContainAny.Any(normalized.Contains);
        }

        private static List<string[]> ReadAllRows(IExcelDataReader reader)
        {
            var rows = new List<string[]>();
            while (reader.Read())
            {
                var arr = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var v = reader.GetValue(i);
                    arr[i] = v?.ToString()?.Trim() ?? string.Empty;
                }

                rows.Add(arr);
            }

            return rows;
        }

        private static int GuessHeaderRow(List<string[]> rows)
        {
            var maxScan = Math.Min(40, rows.Count);
            var bestRow = 0;
            var bestScore = int.MinValue;
            for (var r = 0; r < maxScan; r++)
            {
                var row = rows[r];
                var nonEmptyCount = row.Count(x => !string.IsNullOrWhiteSpace(x));
                var normalized = row
                    .Select(NormalizeHeader)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
                var hintHit = normalized.Count(x => HeaderScoreHints.Any(h => x.StartsWith(h, StringComparison.OrdinalIgnoreCase)));
                var score = (hintHit * 100) + nonEmptyCount;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRow = r;
                }
            }

            return bestRow;
        }

        private static Dictionary<string, List<int>> BuildHeaderMap(string[] header)
        {
            var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length; i++)
            {
                var key = NormalizeHeader(header[i]);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!map.TryGetValue(key, out var idx))
                {
                    idx = new List<int>();
                    map[key] = idx;
                }

                idx.Add(i);
            }

            return map;
        }

        private static IEnumerable<SegmentLinkRecord> ExtractLinksFromRow(
            string sheetName,
            int rowNumber,
            string[] row,
            Dictionary<string, List<int>> map)
        {
            var route = FirstNonEmpty(row, FindFirstIndexByPrefixes(map, RoutePrefixes));
            if (string.IsNullOrWhiteSpace(route) || route == "-")
            {
                yield break;
            }

            var uniqueId = FirstNonEmpty(row, FindFirstIndexByPrefixes(map, UniqueIdPrefixes));

            var siteAIds = FindIndicesByPrefixes(map, SiteAIdPrefixes);
            var siteANames = FindIndicesByPrefixes(map, SiteANamePrefixes);
            var longA = FindIndicesByPrefixes(map, LonAPrefixes);
            var latA = FindIndicesByPrefixes(map, LatAPrefixes);
            var siteBIds = FindIndicesByPrefixes(map, SiteBIdPrefixes);
            var siteBNames = FindIndicesByPrefixes(map, SiteBNamePrefixes);
            var longB = FindIndicesByPrefixes(map, LonBPrefixes);
            var latB = FindIndicesByPrefixes(map, LatBPrefixes);

            var max = new[] { siteAIds.Count, longA.Count, latA.Count, siteBIds.Count, longB.Count, latB.Count }.DefaultIfEmpty(0).Max();
            if (max <= 0)
            {
                yield break;
            }

            var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < max; i++)
            {
                var siteAId = ValueAt(row, siteAIds, i);
                var siteAName = ValueAt(row, siteANames, i);
                var siteBId = ValueAt(row, siteBIds, i);
                var siteBName = ValueAt(row, siteBNames, i);
                var lonAText = ValueAt(row, longA, i);
                var latAText = ValueAt(row, latA, i);
                var lonBText = ValueAt(row, longB, i);
                var latBText = ValueAt(row, latB, i);

                if (!TryParseLonLat(lonAText, latAText, out var latAValue, out var lonAValue) ||
                    !TryParseLonLat(lonBText, latBText, out var latBValue, out var lonBValue))
                {
                    continue;
                }

                var key = route + "|" + uniqueId + "|" + siteAId + "|" + siteBId + "|" +
                          latAValue.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                          lonAValue.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                          latBValue.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                          lonBValue.ToString("0.######", CultureInfo.InvariantCulture);
                if (!dedup.Add(key))
                {
                    continue;
                }

                yield return new SegmentLinkRecord
                {
                    RouteSegment = route,
                    UniqueId = uniqueId,
                    SiteAId = siteAId,
                    SiteAName = siteAName,
                    LatitudeA = latAValue,
                    LongitudeA = lonAValue,
                    SiteBId = siteBId,
                    SiteBName = siteBName,
                    LatitudeB = latBValue,
                    LongitudeB = lonBValue,
                    SourceSheet = sheetName,
                    SourceRow = rowNumber
                };
            }
        }

        private static bool TryParseLonLat(string lonText, string latText, out double lat, out double lon)
        {
            lat = 0;
            lon = 0;
            if (string.IsNullOrWhiteSpace(lonText) || string.IsNullOrWhiteSpace(latText))
            {
                return false;
            }

            var combined = latText + "," + lonText;
            if (CoordinateFormatService.TryParseAnyToDecimal(combined, out lat, out lon))
            {
                return true;
            }

            if (!TryParseDoubleFlexible(latText, out lat) || !TryParseDoubleFlexible(lonText, out lon))
            {
                return false;
            }

            return lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
        }

        private static bool TryParseDoubleFlexible(string text, out double value)
        {
            var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static int? FindFirstIndexByPrefixes(Dictionary<string, List<int>> map, IReadOnlyList<string> prefixes)
        {
            var indices = FindIndicesByPrefixes(map, prefixes);
            return indices.Count > 0 ? indices[0] : null;
        }

        private static List<int> FindIndicesByPrefixes(Dictionary<string, List<int>> map, IReadOnlyList<string> prefixes)
        {
            var output = new List<int>();
            foreach (var prefix in prefixes)
            {
                CollectByPrefix(map, prefix, output);
            }

            return output
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }

        private static void CollectByPrefix(Dictionary<string, List<int>> map, string prefix, List<int> output)
        {
            foreach (var pair in map)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                output.AddRange(pair.Value);
            }
        }

        private static string FirstNonEmpty(string[] row, params int?[] indices)
        {
            foreach (var idx in indices)
            {
                if (!idx.HasValue || idx.Value < 0 || idx.Value >= row.Length)
                {
                    continue;
                }

                var val = row[idx.Value]?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }

            return string.Empty;
        }

        private static string ValueAt(string[] row, List<int> indices, int order)
        {
            if (indices.Count == 0 || order < 0 || order >= indices.Count)
            {
                return string.Empty;
            }

            var idx = indices[order];
            if (idx < 0 || idx >= row.Length)
            {
                return string.Empty;
            }

            return row[idx]?.Trim() ?? string.Empty;
        }

        private static string NormalizeHeader(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var chars = raw
                .Trim()
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray();
            return new string(chars);
        }

        private static async Task EnsureInitializedAsync()
        {
            if (_initialized)
            {
                return;
            }

            await DbGate.WaitAsync();
            try
            {
                if (_initialized)
                {
                    return;
                }

                Directory.CreateDirectory(DirectoryPath);
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS segmentpm_links (" +
                    "id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                    "route_segment TEXT NOT NULL, " +
                    "unique_id TEXT, " +
                    "site_a_id TEXT, " +
                    "site_a_name TEXT, " +
                    "lat_a REAL NOT NULL, " +
                    "lon_a REAL NOT NULL, " +
                    "site_b_id TEXT, " +
                    "site_b_name TEXT, " +
                    "lat_b REAL NOT NULL, " +
                    "lon_b REAL NOT NULL, " +
                    "source_sheet TEXT, " +
                    "source_row INTEGER NOT NULL" +
                    ");" +
                    "CREATE INDEX IF NOT EXISTS idx_segmentpm_route ON segmentpm_links(route_segment);" +
                    "CREATE INDEX IF NOT EXISTS idx_segmentpm_uid ON segmentpm_links(unique_id);" +
                    "CREATE TABLE IF NOT EXISTS segmentpm_meta (" +
                    "source_path TEXT, imported_at TEXT, total_links INTEGER" +
                    ");";
                cmd.ExecuteNonQuery();
                _initialized = true;
            }
            finally
            {
                DbGate.Release();
            }
        }

        private sealed class ParsedImportLinks
        {
            public ParsedImportLinks(List<SegmentLinkRecord> links, int processedSheets, int processedRows)
            {
                Links = links;
                ProcessedSheets = processedSheets;
                ProcessedRows = processedRows;
            }

            public List<SegmentLinkRecord> Links { get; }
            public int ProcessedSheets { get; }
            public int ProcessedRows { get; }
        }

        public sealed class SegmentPmImportResult
        {
            public string SourcePath { get; set; } = string.Empty;
            public int ProcessedSheets { get; set; }
            public int ProcessedRows { get; set; }
            public int InsertedLinks { get; set; }
        }

        public sealed class SegmentSiteLinkPoint
        {
            public string RouteSegment { get; set; } = string.Empty;
            public string UniqueId { get; set; } = string.Empty;
            public string SiteAId { get; set; } = string.Empty;
            public string SiteAName { get; set; } = string.Empty;
            public double LatitudeA { get; set; }
            public double LongitudeA { get; set; }
            public string SiteBId { get; set; } = string.Empty;
            public string SiteBName { get; set; } = string.Empty;
            public double LatitudeB { get; set; }
            public double LongitudeB { get; set; }
        }

        private sealed class SegmentLinkRecord
        {
            public string RouteSegment { get; set; } = string.Empty;
            public string UniqueId { get; set; } = string.Empty;
            public string SiteAId { get; set; } = string.Empty;
            public string SiteAName { get; set; } = string.Empty;
            public double LatitudeA { get; set; }
            public double LongitudeA { get; set; }
            public string SiteBId { get; set; } = string.Empty;
            public string SiteBName { get; set; } = string.Empty;
            public double LatitudeB { get; set; }
            public double LongitudeB { get; set; }
            public string SourceSheet { get; set; } = string.Empty;
            public int SourceRow { get; set; }

            public string DedupKey =>
                RouteSegment + "|" + UniqueId + "|" + SiteAId + "|" + SiteBId + "|" +
                LatitudeA.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                LongitudeA.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                LatitudeB.ToString("0.######", CultureInfo.InvariantCulture) + "|" +
                LongitudeB.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
