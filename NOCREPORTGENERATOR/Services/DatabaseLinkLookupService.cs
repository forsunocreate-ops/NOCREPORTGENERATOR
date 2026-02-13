using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ExcelDataReader;

namespace NOCREPORTGENERATOR.Services
{
    public static class DatabaseLinkLookupService
    {
        private const string DefaultDatabaseLinkPath = @"E:\PROJECT VS\NOCREPORTGENERATOR\NOCREPORTGENERATOR\DATABASE_LINK.xlsb";
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

        private static Task<LookupCache> GetCacheAsync()
        {
            lock (Sync)
            {
                _cacheTask ??= Task.Run(LoadCache);
                return _cacheTask;
            }
        }

        private static LookupCache LoadCache()
        {
            var path = ResolveDatabasePath();
            if (path is null)
            {
                throw new FileNotFoundException("DATABASE_LINK.xlsb tidak ditemukan.");
            }

            var segmentMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var picMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var segmentPicBySystemKey = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            var allSegmentRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var segmentPicGlobal = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var allPicDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

            foreach (DataRow row in table.Rows)
            {
                var systemKeyCell = (row[systemKeyColumnName]?.ToString() ?? string.Empty).Trim();
                var segmentRoute = (row[segmentRouteColumnName]?.ToString() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(systemKeyCell) || string.IsNullOrWhiteSpace(segmentRoute))
                {
                    continue;
                }

                foreach (var key in SplitKeys(systemKeyCell))
                {
                    if (!segmentMap.TryGetValue(key, out var segments))
                    {
                        segments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        segmentMap[key] = segments;
                    }

                    segments.Add(segmentRoute);
                    allSegmentRoutes.Add(segmentRoute);

                    var teamSerpo = teamSerpoColumnName is null ? string.Empty : (row[teamSerpoColumnName]?.ToString() ?? string.Empty).Trim();
                    var pic = picColumnName is null ? string.Empty : (row[picColumnName]?.ToString() ?? string.Empty).Trim();
                    var contact = contactColumnName is null ? string.Empty : (row[contactColumnName]?.ToString() ?? string.Empty).Trim();
                    var display = $"{teamSerpo} | {pic} | {contact}";
                    allPicDisplays.Add(display);

                    if (!picMap.TryGetValue(key, out var picDisplays))
                    {
                        picDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        picMap[key] = picDisplays;
                    }

                    picDisplays.Add(display);

                    if (!segmentPicBySystemKey.TryGetValue(key, out var segmentPicMap))
                    {
                        segmentPicMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                        segmentPicBySystemKey[key] = segmentPicMap;
                    }

                    if (!segmentPicMap.TryGetValue(segmentRoute, out var segmentPicDisplays))
                    {
                        segmentPicDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        segmentPicMap[segmentRoute] = segmentPicDisplays;
                    }

                    segmentPicDisplays.Add(display);

                    if (!segmentPicGlobal.TryGetValue(segmentRoute, out var globalPicDisplays))
                    {
                        globalPicDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        segmentPicGlobal[segmentRoute] = globalPicDisplays;
                    }

                    globalPicDisplays.Add(display);
                }
            }

            return new LookupCache(segmentMap, picMap, segmentPicBySystemKey, allSegmentRoutes, segmentPicGlobal, allPicDisplays);
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
            foreach (DataColumn column in table.Columns)
            {
                if (string.Equals(column.ColumnName?.Trim(), expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return column.ColumnName;
                }
            }

            return null;
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
