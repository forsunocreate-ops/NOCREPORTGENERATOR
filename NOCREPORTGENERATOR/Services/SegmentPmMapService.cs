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

                sheetProcessed++;
            }
            while (reader.NextResult());

            var deduped = allLinks
                .Where(x => !string.IsNullOrWhiteSpace(x.RouteSegment))
                .GroupBy(x => x.DedupKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            await SaveLinksAsync(deduped, resolvedPath, cancellationToken);

            return new SegmentPmImportResult
            {
                SourcePath = resolvedPath,
                ProcessedSheets = sheetProcessed,
                ProcessedRows = parsedRows,
                InsertedLinks = deduped.Count
            };
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
                Path.Combine(AppContext.BaseDirectory, "SEGMENTPM.xlsx"),
                Path.Combine(Environment.CurrentDirectory, "SEGMENTPM.xlsx")
            };

            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static bool IsTargetSheet(string name)
        {
            return name.Equals("Data_IOH Fiber Asset Master", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("New Link", StringComparison.OrdinalIgnoreCase);
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
            var maxScan = Math.Min(20, rows.Count);
            var bestRow = 0;
            var bestCount = -1;
            for (var r = 0; r < maxScan; r++)
            {
                var count = rows[r].Count(x => !string.IsNullOrWhiteSpace(x));
                if (count > bestCount)
                {
                    bestCount = count;
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
            var route = FirstNonEmpty(row,
                FirstIndex(map, "routesegment2025"),
                FirstIndex(map, "routesegment2024"),
                FirstIndex(map, "routesegment"));
            if (string.IsNullOrWhiteSpace(route) || route == "-")
            {
                yield break;
            }

            var uniqueId = FirstNonEmpty(row, FirstIndex(map, "uniqueid"));

            var siteAIds = Indices(map, "siteida");
            var siteANames = Indices(map, "sitenamea");
            var longA = Indices(map, "longa");
            var latA = Indices(map, "lata");
            var siteBIds = Indices(map, "siteidb");
            var siteBNames = Indices(map, "sitenameb");
            var longB = Indices(map, "longb");
            var latB = Indices(map, "latb");

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

        private static int? FirstIndex(Dictionary<string, List<int>> map, string key)
        {
            return map.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : null;
        }

        private static List<int> Indices(Dictionary<string, List<int>> map, string key)
        {
            return map.TryGetValue(key, out var list) ? list : new List<int>();
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
            if (indices.Count == 0)
            {
                return string.Empty;
            }

            var idx = indices[Math.Min(order, indices.Count - 1)];
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
