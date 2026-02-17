using ExcelDataReader;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class DatabaseLinkLookupService
    {
        private const string DefaultDatabaseLinkPath = @"E:\PROJECT VS\NOCREPORTGENERATOR\NOCREPORTGENERATOR\DATABASE_LINK.xlsb";
        private const string LookupCacheDbFileName = "database_link_cache.db";
        private static readonly object Sync = new();
        private static Task<LookupCache>? _cacheTask;

        public static async Task<IReadOnlyList<string>> FindSegmentRoutesBySystemKeyAsync(string systemKeyInput)
        {
            var keys = SplitKeys(systemKeyInput);
            if (keys.Count == 0)
            {
                return Array.Empty<string>();
            }

            var cache = await GetCacheAsync();
            var segments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                if (cache.SegmentRoutesBySystemKey.TryGetValue(key, out var values))
                {
                    foreach (var value in values)
                    {
                        segments.Add(value);
                    }
                }
            }

            return segments.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<SegmentLookupResult> FindSegmentLookupBySystemKeyAsync(string systemKeyInput)
        {
            var keys = SplitKeys(systemKeyInput);
            var cache = await GetCacheAsync();
            if (keys.Count == 0)
            {
                return BuildSegmentLookupResult(cache.AllSegmentRoutes, cache.SegmentPicGlobal);
            }

            var segments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var segmentPicMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                if (!cache.SegmentRoutesBySystemKey.TryGetValue(key, out var routeValues))
                {
                    continue;
                }

                foreach (var route in routeValues)
                {
                    segments.Add(route);

                    if (!segmentPicMap.TryGetValue(route, out var picSet))
                    {
                        picSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        segmentPicMap[route] = picSet;
                    }

                    if (cache.SegmentPicBySystemKey.TryGetValue(key, out var routePicMap) &&
                        routePicMap.TryGetValue(route, out var picValues))
                    {
                        foreach (var value in picValues)
                        {
                            picSet.Add(value);
                        }
                    }
                }
            }

            return BuildSegmentLookupResult(segments, segmentPicMap);
        }

        public static async Task<IReadOnlyList<string>> FindPicDisplayBySystemKeyAsync(string systemKeyInput)
        {
            var keys = SplitKeys(systemKeyInput);
            if (keys.Count == 0)
            {
                return Array.Empty<string>();
            }

            var cache = await GetCacheAsync();
            var displays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (cache.PicDisplaysBySystemKey.TryGetValue(key, out var values))
                {
                    foreach (var value in values)
                    {
                        displays.Add(value);
                    }
                }
            }

            return displays.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<IReadOnlyList<string>> FindAllPicDisplaysAsync()
        {
            var cache = await GetCacheAsync();
            return cache.AllPicDisplays.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetSegmentPicMapAsync()
        {
            var cache = await GetCacheAsync();
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in cache.SegmentPicGlobal)
            {
                map[pair.Key] = pair.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }

            return map;
        }

        private static Task<LookupCache> GetCacheAsync()
        {
            lock (Sync)
            {
                _cacheTask ??= Task.Run(LoadCache);
                return _cacheTask;
            }
        }

        public static async Task RefreshCacheAsync()
        {
            lock (Sync)
            {
                _cacheTask = null;
            }

            await GetCacheAsync();
        }

        private static LookupCache LoadCache()
        {
            var sourcePath = ResolveDatabasePath();
            if (sourcePath is null)
            {
                throw new FileNotFoundException("DATABASE_LINK.xlsb tidak ditemukan.");
            }

            EnsureSqlCacheUpToDate(sourcePath);
            return LoadCacheFromSqlite();
        }

        private static void EnsureSqlCacheUpToDate(string sourcePath)
        {
            var cacheDbPath = ResolveSqliteCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(cacheDbPath) ?? AppContext.BaseDirectory);

            var sourceTicks = File.GetLastWriteTimeUtc(sourcePath).Ticks;
            var sourceFullPath = Path.GetFullPath(sourcePath);

            if (!File.Exists(cacheDbPath))
            {
                RebuildSqliteCache(sourcePath, cacheDbPath, sourceTicks, sourceFullPath);
                return;
            }

            try
            {
                using var connection = new SqliteConnection("Data Source=" + cacheDbPath);
                connection.Open();
                EnsureSchema(connection);

                var cachedSourcePath = ReadMetadata(connection, "source_path");
                var cachedTicksText = ReadMetadata(connection, "source_last_write_utc_ticks");
                var cacheVersion = ReadMetadata(connection, "cache_version");

                var isTicksValid = long.TryParse(cachedTicksText, out var cachedTicks);
                var isSameSource = string.Equals(cachedSourcePath, sourceFullPath, StringComparison.OrdinalIgnoreCase);
                var isSameVersion = string.Equals(cacheVersion, "1", StringComparison.Ordinal);

                if (!isTicksValid || !isSameSource || !isSameVersion || cachedTicks != sourceTicks)
                {
                    RebuildSqliteCache(sourcePath, cacheDbPath, sourceTicks, sourceFullPath);
                }
            }
            catch
            {
                RebuildSqliteCache(sourcePath, cacheDbPath, sourceTicks, sourceFullPath);
            }
        }

        private static void RebuildSqliteCache(string sourcePath, string cacheDbPath, long sourceTicks, string sourceFullPath)
        {
            using var stream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration
                {
                    UseHeaderRow = true
                }
            });

            var table = dataSet.Tables.Cast<DataTable>()
                .FirstOrDefault(x => string.Equals(x.TableName, "FOA Active", StringComparison.OrdinalIgnoreCase));

            if (table is null)
            {
                throw new InvalidDataException("Sheet 'FOA Active' tidak ditemukan pada DATABASE_LINK.xlsb.");
            }

            var systemKeyColumnName = FindColumnName(table, "System Key");
            var segmentRouteColumnName = FindColumnName(table, "Segment Route");
            var teamSerpoColumnName = FindColumnName(table, "Team Serpo");
            var picColumnName = FindColumnName(table, "PIC");
            var contactColumnName = FindColumnName(table, "Contact Number");

            if (systemKeyColumnName is null || segmentRouteColumnName is null)
            {
                throw new InvalidDataException("Kolom 'System Key' atau 'Segment Route' tidak ditemukan pada sheet 'FOA Active'.");
            }

            if (File.Exists(cacheDbPath))
            {
                File.Delete(cacheDbPath);
            }

            using var connection = new SqliteConnection("Data Source=" + cacheDbPath);
            connection.Open();
            EnsureSchema(connection);

            using var transaction = connection.BeginTransaction();
            using var insertSystemSegment = connection.CreateCommand();
            insertSystemSegment.Transaction = transaction;
            insertSystemSegment.CommandText =
                "INSERT INTO system_segment(system_key, segment_route) VALUES($system_key, $segment_route);";
            var pSystemKey = insertSystemSegment.Parameters.Add("$system_key", SqliteType.Text);
            var pSegmentRoute = insertSystemSegment.Parameters.Add("$segment_route", SqliteType.Text);

            using var insertSystemPic = connection.CreateCommand();
            insertSystemPic.Transaction = transaction;
            insertSystemPic.CommandText =
                "INSERT INTO system_pic(system_key, segment_route, pic_display) VALUES($system_key, $segment_route, $pic_display);";
            var pSystemKeyPic = insertSystemPic.Parameters.Add("$system_key", SqliteType.Text);
            var pSegmentRoutePic = insertSystemPic.Parameters.Add("$segment_route", SqliteType.Text);
            var pPicDisplay = insertSystemPic.Parameters.Add("$pic_display", SqliteType.Text);

            using var insertSegmentPic = connection.CreateCommand();
            insertSegmentPic.Transaction = transaction;
            insertSegmentPic.CommandText =
                "INSERT INTO segment_pic(segment_route, pic_display) VALUES($segment_route, $pic_display);";
            var pSegmentRouteGlobal = insertSegmentPic.Parameters.Add("$segment_route", SqliteType.Text);
            var pPicDisplayGlobal = insertSegmentPic.Parameters.Add("$pic_display", SqliteType.Text);

            using var insertAllPic = connection.CreateCommand();
            insertAllPic.Transaction = transaction;
            insertAllPic.CommandText =
                "INSERT INTO all_pic(pic_display) VALUES($pic_display);";
            var pAllPicDisplay = insertAllPic.Parameters.Add("$pic_display", SqliteType.Text);

            var uniqueSystemSegment = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueSystemPic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueSegmentPic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueAllPic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow row in table.Rows)
            {
                var systemKeyCell = (row[systemKeyColumnName]?.ToString() ?? string.Empty).Trim();
                var segmentRoute = (row[segmentRouteColumnName]?.ToString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(systemKeyCell) || string.IsNullOrWhiteSpace(segmentRoute))
                {
                    continue;
                }

                var teamSerpo = teamSerpoColumnName is null ? string.Empty : (row[teamSerpoColumnName]?.ToString() ?? string.Empty).Trim();
                var pic = picColumnName is null ? string.Empty : (row[picColumnName]?.ToString() ?? string.Empty).Trim();
                var contact = contactColumnName is null ? string.Empty : (row[contactColumnName]?.ToString() ?? string.Empty).Trim();
                var display = teamSerpo + " | " + pic + " | " + contact;

                foreach (var key in SplitKeys(systemKeyCell))
                {
                    var systemSegmentKey = key + "||" + segmentRoute;
                    if (uniqueSystemSegment.Add(systemSegmentKey))
                    {
                        pSystemKey.Value = key;
                        pSegmentRoute.Value = segmentRoute;
                        insertSystemSegment.ExecuteNonQuery();
                    }

                    var systemPicKey = key + "||" + segmentRoute + "||" + display;
                    if (uniqueSystemPic.Add(systemPicKey))
                    {
                        pSystemKeyPic.Value = key;
                        pSegmentRoutePic.Value = segmentRoute;
                        pPicDisplay.Value = display;
                        insertSystemPic.ExecuteNonQuery();
                    }
                }

                var segmentPicKey = segmentRoute + "||" + display;
                if (uniqueSegmentPic.Add(segmentPicKey))
                {
                    pSegmentRouteGlobal.Value = segmentRoute;
                    pPicDisplayGlobal.Value = display;
                    insertSegmentPic.ExecuteNonQuery();
                }

                if (uniqueAllPic.Add(display))
                {
                    pAllPicDisplay.Value = display;
                    insertAllPic.ExecuteNonQuery();
                }
            }

            UpsertMetadata(connection, transaction, "source_path", sourceFullPath);
            UpsertMetadata(connection, transaction, "source_last_write_utc_ticks", sourceTicks.ToString());
            UpsertMetadata(connection, transaction, "cache_version", "1");
            transaction.Commit();
        }

        private static LookupCache LoadCacheFromSqlite()
        {
            var cacheDbPath = ResolveSqliteCachePath();
            var segmentMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var picMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var segmentPicBySystemKey = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            var allSegmentRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var segmentPicGlobal = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var allPicDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqliteConnection("Data Source=" + cacheDbPath);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT system_key, segment_route FROM system_segment;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var segmentRoute = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(segmentRoute))
                    {
                        continue;
                    }

                    if (!segmentMap.TryGetValue(key, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        segmentMap[key] = set;
                    }

                    set.Add(segmentRoute);
                    allSegmentRoutes.Add(segmentRoute);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT system_key, segment_route, pic_display FROM system_pic;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var segmentRoute = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var display = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(segmentRoute) || string.IsNullOrWhiteSpace(display))
                    {
                        continue;
                    }

                    if (!picMap.TryGetValue(key, out var picSet))
                    {
                        picSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        picMap[key] = picSet;
                    }

                    picSet.Add(display);

                    if (!segmentPicBySystemKey.TryGetValue(key, out var perSegment))
                    {
                        perSegment = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                        segmentPicBySystemKey[key] = perSegment;
                    }

                    if (!perSegment.TryGetValue(segmentRoute, out var segmentPicSet))
                    {
                        segmentPicSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        perSegment[segmentRoute] = segmentPicSet;
                    }

                    segmentPicSet.Add(display);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT segment_route, pic_display FROM segment_pic;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var segmentRoute = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var display = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (string.IsNullOrWhiteSpace(segmentRoute) || string.IsNullOrWhiteSpace(display))
                    {
                        continue;
                    }

                    if (!segmentPicGlobal.TryGetValue(segmentRoute, out var picSet))
                    {
                        picSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        segmentPicGlobal[segmentRoute] = picSet;
                    }

                    picSet.Add(display);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT pic_display FROM all_pic;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var display = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(display))
                    {
                        allPicDisplays.Add(display);
                    }
                }
            }

            return new LookupCache(segmentMap, picMap, segmentPicBySystemKey, allSegmentRoutes, segmentPicGlobal, allPicDisplays);
        }

        private static string ResolveSqliteCachePath()
        {
            var localDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NOCREPORTGENERATOR");
            return Path.Combine(localDir, LookupCacheDbFileName);
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS metadata (k TEXT PRIMARY KEY, v TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS system_segment (system_key TEXT NOT NULL, segment_route TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS system_pic (system_key TEXT NOT NULL, segment_route TEXT NOT NULL, pic_display TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS segment_pic (segment_route TEXT NOT NULL, pic_display TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS all_pic (pic_display TEXT NOT NULL);" +
                "CREATE INDEX IF NOT EXISTS idx_system_segment_key ON system_segment(system_key);" +
                "CREATE INDEX IF NOT EXISTS idx_system_pic_key ON system_pic(system_key);" +
                "CREATE INDEX IF NOT EXISTS idx_segment_pic_route ON segment_pic(segment_route);" +
                "CREATE INDEX IF NOT EXISTS idx_all_pic_display ON all_pic(pic_display);";
            cmd.ExecuteNonQuery();
        }

        private static string ReadMetadata(SqliteConnection connection, string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT v FROM metadata WHERE k = $k LIMIT 1;";
            cmd.Parameters.AddWithValue("$k", key);
            var value = cmd.ExecuteScalar();
            return value?.ToString() ?? string.Empty;
        }

        private static void UpsertMetadata(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                "INSERT INTO metadata(k, v) VALUES($k, $v) " +
                "ON CONFLICT(k) DO UPDATE SET v = excluded.v;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }

        private static string? ResolveDatabasePath()
        {
            if (File.Exists(DefaultDatabaseLinkPath))
            {
                return DefaultDatabaseLinkPath;
            }

            var localPath = Path.Combine(AppContext.BaseDirectory, "DATABASE_LINK.xlsb");
            if (File.Exists(localPath))
            {
                return localPath;
            }

            return null;
        }

        private static List<string> SplitKeys(string value)
        {
            return value
                .Split(new[] { ",", ";", "\r", "\n", "\t" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? FindColumnName(DataTable table, string expectedName)
        {
            var expected = NormalizeHeader(expectedName);
            foreach (DataColumn column in table.Columns)
            {
                var normalized = NormalizeHeader(column.ColumnName ?? string.Empty);
                if (string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return column.ColumnName;
                }
            }

            foreach (DataColumn column in table.Columns)
            {
                var normalized = NormalizeHeader(column.ColumnName ?? string.Empty);
                if (normalized.Contains(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return column.ColumnName;
                }
            }

            return null;
        }

        private static string NormalizeHeader(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();
        }

        private static SegmentLookupResult BuildSegmentLookupResult(
            IEnumerable<string> segments,
            IReadOnlyDictionary<string, HashSet<string>> picMap)
        {
            var orderedSegments = segments.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var orderedMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in picMap)
            {
                orderedMap[pair.Key] = pair.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }

            return new SegmentLookupResult(orderedSegments, orderedMap);
        }

        private static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToUpperInvariant();
            normalized = normalized.Replace(" ", string.Empty);
            normalized = normalized.Replace("\u00A0", string.Empty);
            return normalized;
        }

        private sealed class LookupCache
        {
            public LookupCache(
                Dictionary<string, HashSet<string>> segmentRoutesBySystemKey,
                Dictionary<string, HashSet<string>> picDisplaysBySystemKey,
                Dictionary<string, Dictionary<string, HashSet<string>>> segmentPicBySystemKey,
                HashSet<string> allSegmentRoutes,
                Dictionary<string, HashSet<string>> segmentPicGlobal,
                HashSet<string> allPicDisplays)
            {
                SegmentRoutesBySystemKey = segmentRoutesBySystemKey;
                PicDisplaysBySystemKey = picDisplaysBySystemKey;
                SegmentPicBySystemKey = segmentPicBySystemKey;
                AllSegmentRoutes = allSegmentRoutes;
                SegmentPicGlobal = segmentPicGlobal;
                AllPicDisplays = allPicDisplays;
            }

            public Dictionary<string, HashSet<string>> SegmentRoutesBySystemKey { get; }
            public Dictionary<string, HashSet<string>> PicDisplaysBySystemKey { get; }
            public Dictionary<string, Dictionary<string, HashSet<string>>> SegmentPicBySystemKey { get; }
            public HashSet<string> AllSegmentRoutes { get; }
            public Dictionary<string, HashSet<string>> SegmentPicGlobal { get; }
            public HashSet<string> AllPicDisplays { get; }
        }

        public sealed class SegmentLookupResult
        {
            public SegmentLookupResult(IReadOnlyList<string> segments, IReadOnlyDictionary<string, IReadOnlyList<string>> picBySegment)
            {
                Segments = segments;
                PicBySegment = picBySegment;
            }

            public IReadOnlyList<string> Segments { get; }
            public IReadOnlyDictionary<string, IReadOnlyList<string>> PicBySegment { get; }
        }
    }
}
