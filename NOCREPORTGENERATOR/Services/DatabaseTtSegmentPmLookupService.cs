using ExcelDataReader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class DatabaseTtSegmentPmLookupService
    {
        private const int ColProgress = 11; // L
        private const int ColSegmentRoute = 9; // J
        private const int ColTtIoh = 25;    // Z
        private const int ColTitle = 61;    // BJ
        private static readonly object Sync = new();
        private static Task<IReadOnlyDictionary<string, string>>? _cacheTask;
        private static string _cachePath = string.Empty;
        private static IReadOnlyList<string>? _segmentPmOptionsCache;
        private static readonly Regex TtIohRegex = new(
            @"INC\W*(?<a>\d{8})\W*(?<b>\d{8})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SegmentPmRegex = new(
            @"(?ix)
            (?:tt\s*pm\s*segment|segment\s*tt\s*pm|segment\s*pm)
            \s*(?:[:=\-\)\]\>]\s*|\s+)
            (?<value>[^\r\n,;|]+)",
            RegexOptions.Compiled);
        private static readonly Regex DateLikeRegex = new(
            @"\b(?:\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[-/]\d{1,2}[-/]\d{2,4})\b",
            RegexOptions.Compiled);
        private static readonly Regex NumericRoutePairRegex = new(@"^\s*(?<a>\d{5,8})\s*[-/]\s*(?<b>\d{5,8})\s*$", RegexOptions.Compiled);

        public static void InvalidateCache()
        {
            lock (Sync)
            {
                _cacheTask = null;
                _cachePath = string.Empty;
                _segmentPmOptionsCache = null;
            }
        }

        public static async Task<IReadOnlyDictionary<string, string>> GetSegmentPmByTtIohAsync(
            string? filePath = null,
            CancellationToken cancellationToken = default)
        {
            var resolvedPath = ResolvePath(filePath);
            if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Task<IReadOnlyDictionary<string, string>> task;
            lock (Sync)
            {
                var needsRefresh =
                    _cacheTask is null ||
                    !_cachePath.Equals(resolvedPath, StringComparison.OrdinalIgnoreCase) ||
                    _cacheTask.IsCanceled ||
                    _cacheTask.IsFaulted;

                if (needsRefresh)
                {
                    _cachePath = resolvedPath;
                    _cacheTask = Task.Run(() => BuildMapInternal(resolvedPath, CancellationToken.None));
                }

                task = _cacheTask!;
            }

            return await task.WaitAsync(cancellationToken);
        }

        public static string ExtractSegmentPmFromProgress(string? progressText)
        {
            var source = progressText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            // Prioritas 1: lookup ketat terhadap daftar segment PM kolom H SEGMENTPM.xlsx.
            var options = GetSegmentPmOptionsSafe();
            var matchedByLookup = TryMatchFromKnownOptions(source, options);
            if (!string.IsNullOrWhiteSpace(matchedByLookup))
            {
                return matchedByLookup;
            }

            // Prioritas 2: fallback regex untuk kasus teks progress tidak baku.
            var match = SegmentPmRegex.Match(source);
            if (!match.Success)
            {
                return string.Empty;
            }

            return NormalizeSegmentPmValue(match.Groups["value"].Value);
        }

        private static IReadOnlyDictionary<string, string> BuildMapInternal(string path, CancellationToken cancellationToken)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var segmentPmByRoute = LoadSegmentPmByRouteSafe();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(reader.Name ?? string.Empty, "All Fo Cut", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var progress = reader.FieldCount > ColProgress ? reader.GetValue(ColProgress)?.ToString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(progress))
                    {
                        continue;
                    }

                    var tt = ResolveTtIohFromRow(reader, progress);
                    if (string.IsNullOrWhiteSpace(tt))
                    {
                        continue;
                    }

                    var segmentPm = ExtractSegmentPmFromProgress(progress);
                    if (string.IsNullOrWhiteSpace(segmentPm))
                    {
                        var route = reader.FieldCount > ColSegmentRoute ? reader.GetValue(ColSegmentRoute)?.ToString() ?? string.Empty : string.Empty;
                        segmentPm = ResolveSegmentPmFromRoute(route, segmentPmByRoute);
                    }

                    if (string.IsNullOrWhiteSpace(segmentPm))
                    {
                        continue;
                    }

                    map[tt] = segmentPm;
                }

                break;
            }
            while (reader.NextResult());

            DeveloperDiagnostics.LogInfo(
                "DatabaseTT Segment PM map built: " +
                map.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " TT entries");

            return map;
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadSegmentPmByRouteSafe()
        {
            try
            {
                return SegmentPmMapService
                    .GetSegmentPmByRouteFromWorkbookAsync(password: "no")
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string ResolveSegmentPmFromRoute(
            string? route,
            IReadOnlyDictionary<string, IReadOnlyList<string>> segmentPmByRoute)
        {
            if (string.IsNullOrWhiteSpace(route) || segmentPmByRoute.Count == 0)
            {
                return string.Empty;
            }

            if (segmentPmByRoute.TryGetValue(route, out var values) && values.Count > 0)
            {
                return values[0];
            }

            var reversed = TryReverseNumericRoutePair(route);
            if (!string.IsNullOrWhiteSpace(reversed) &&
                segmentPmByRoute.TryGetValue(reversed, out values) &&
                values.Count > 0)
            {
                return values[0];
            }

            return string.Empty;
        }

        private static string TryReverseNumericRoutePair(string route)
        {
            var match = NumericRoutePairRegex.Match(route ?? string.Empty);
            if (!match.Success)
            {
                return string.Empty;
            }

            return match.Groups["b"].Value + "-" + match.Groups["a"].Value;
        }

        private static string ResolveTtIohFromRow(IExcelDataReader reader, string progress)
        {
            var fromCol = reader.FieldCount > ColTtIoh ? reader.GetValue(ColTtIoh)?.ToString() ?? string.Empty : string.Empty;
            var normalizedFromCol = NormalizeTtIoh(fromCol);
            if (!string.IsNullOrWhiteSpace(normalizedFromCol))
            {
                return normalizedFromCol;
            }

            var fromProgress = ExtractTtIoh(progress);
            if (!string.IsNullOrWhiteSpace(fromProgress))
            {
                return fromProgress;
            }

            var title = reader.FieldCount > ColTitle ? reader.GetValue(ColTitle)?.ToString() ?? string.Empty : string.Empty;
            return ExtractTtIoh(title);
        }

        private static string NormalizeTtIoh(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = TtIohRegex.Match(text);
            if (!match.Success)
            {
                return string.Empty;
            }

            return "INC-" + match.Groups["a"].Value + "-" + match.Groups["b"].Value;
        }

        private static string ExtractTtIoh(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = TtIohRegex.Match(text);
            if (!match.Success)
            {
                return string.Empty;
            }

            return "INC-" + match.Groups["a"].Value + "-" + match.Groups["b"].Value;
        }

        private static string NormalizeSegmentPmValue(string value)
        {
            var text = value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Potong jika setelah nilai segment muncul timestamp progress berikutnya.
            var dateMatch = DateLikeRegex.Match(text);
            if (dateMatch.Success && dateMatch.Index > 0)
            {
                text = text[..dateMatch.Index].TrimEnd();
            }

            while (text.StartsWith("(", StringComparison.Ordinal) ||
                   text.StartsWith("[", StringComparison.Ordinal))
            {
                text = text[1..].TrimStart();
            }

            while (text.EndsWith(")", StringComparison.Ordinal) ||
                   text.EndsWith("]", StringComparison.Ordinal) ||
                   text.EndsWith(".", StringComparison.Ordinal))
            {
                text = text[..^1].TrimEnd();
            }

            return text;
        }

        private static string TryMatchFromKnownOptions(string source, IReadOnlyList<string> options)
        {
            if (options.Count == 0)
            {
                return string.Empty;
            }

            var normalizedSource = NormalizeLookupToken(source);
            if (string.IsNullOrWhiteSpace(normalizedSource))
            {
                return string.Empty;
            }

            string best = string.Empty;
            var bestLen = -1;
            foreach (var option in options)
            {
                var clean = option?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(clean))
                {
                    continue;
                }

                var normalizedOption = NormalizeLookupToken(clean);
                if (normalizedOption.Length < 4)
                {
                    continue;
                }

                if (!normalizedSource.Contains(normalizedOption, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalizedOption.Length > bestLen)
                {
                    best = clean;
                    bestLen = normalizedOption.Length;
                }
            }

            return best;
        }

        private static string NormalizeLookupToken(string value)
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

        private static IReadOnlyList<string> GetSegmentPmOptionsSafe()
        {
            lock (Sync)
            {
                if (_segmentPmOptionsCache is not null)
                {
                    return _segmentPmOptionsCache;
                }
            }

            try
            {
                var loaded = SegmentPmMapService
                    .GetSegmentPmOptionsFromWorkbookAsync(password: "no")
                    .GetAwaiter()
                    .GetResult();

                var clean = loaded
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                lock (Sync)
                {
                    _segmentPmOptionsCache = clean;
                }

                return clean;
            }
            catch
            {
                var empty = Array.Empty<string>();
                lock (Sync)
                {
                    _segmentPmOptionsCache = empty;
                }

                return empty;
            }
        }

        private static string ResolvePath(string? inputPath)
        {
            if (!string.IsNullOrWhiteSpace(inputPath) && File.Exists(inputPath))
            {
                return inputPath;
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DATABASE_TT.xlsx"),
                Path.Combine(AppContext.BaseDirectory, "DATABASE_TT.xlsx"),
                Path.Combine(Environment.CurrentDirectory, "DATABASE_TT.xlsx")
            };

            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }
    }
}
