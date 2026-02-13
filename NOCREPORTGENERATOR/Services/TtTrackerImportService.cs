using ExcelDataReader;
using NOCREPORTGENERATOR.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class TtTrackerImportService
    {
        private const string TargetSheetName = "All Fo Cut";
        private const int EmptyStreakStopThreshold = 2500;
        private static readonly Regex TtIohRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProgressDateRegex = new(
            @"(?<dt>\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\s+\d{1,2}:\d{2}(?::\d{2})?\b)",
            RegexOptions.Compiled);

        // Fixed column indexes based on TT_TRACKER.xlsx mapping.
        private const int ColSegmentRoute = 9;   // J
        private const int ColStatusLink = 5;     // F
        private const int ColCutPoint = 10;      // K
        private const int ColProgress = 11;      // L
        private const int ColOccur = 12;         // M
        private const int ColDispatch = 15;      // P
        private const int ColDetail = 17;        // R
        private const int ColSpecific = 19;      // T
        private const int ColTtIoh = 25;         // Z
        private const int ColAddress = 40;       // AO
        private const int ColLatitude = 42;      // AQ
        private const int ColLongitude = 43;     // AR
        private const int ColPic = 55;           // BD
        private const int ColTitle = 61;         // BJ

        public static Task<ImportPreviewResult> BuildPreviewAsync(
            string filePath,
            int takeCount,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => BuildPreviewInternal(filePath, takeCount, cancellationToken), cancellationToken);
        }

        public static Task<ImportExecutionResult> ImportAsync(
            string filePath,
            CancellationToken cancellationToken,
            IProgress<ImportProgressInfo>? progress = null)
        {
            return Task.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    throw new FileNotFoundException("File import tidak ditemukan.", filePath);
                }

                var result = new ImportExecutionResult();
                var upsertContext = await LocalFormStorageService.CreateBulkUpsertByTtIohContextAsync();
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = ExcelReaderFactory.CreateReader(stream);

                var rowCountHint = 0;
                var targetFound = false;
                var emptyStreak = 0;
                var lastProgressRows = 0;
                var progressWatch = Stopwatch.StartNew();

                do
                {
                    var sheetName = reader.Name ?? string.Empty;
                    if (!string.Equals(sheetName, TargetSheetName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    targetFound = true;
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.TotalRowsRead++;

                        var record = TryBuildRecord(reader);
                        if (record is null)
                        {
                            result.SkippedRows++;
                            emptyStreak++;
                        }
                        else
                        {
                            upsertContext.Upsert(record);
                            result.ValidRows++;
                            emptyStreak = 0;
                        }

                        if (emptyStreak >= EmptyStreakStopThreshold && result.ValidRows > 0)
                        {
                            break;
                        }

                        if (result.TotalRowsRead - lastProgressRows >= 1200 ||
                            progressWatch.ElapsedMilliseconds >= 350)
                        {
                            lastProgressRows = result.TotalRowsRead;
                            progressWatch.Restart();
                            progress?.Report(BuildProgress(result, rowCountHint, "Membaca dan memetakan data..."));
                        }
                    }

                    break;
                }
                while (reader.NextResult());

                if (!targetFound)
                {
                    throw new InvalidDataException("Sheet 'All Fo Cut' tidak ditemukan pada file.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ImportProgressInfo(70, "Menyiapkan import ke database..."));
                var writeProgress = new Progress<LocalFormStorageService.BulkWriteProgress>(x =>
                {
                    var ratio = x.Total <= 0 ? 0 : (x.Processed * 1d / x.Total);
                    var percent = 70 + (29 * ratio);
                    var message =
                        "Mengimpor ke database... " +
                        x.Processed.ToString(CultureInfo.InvariantCulture) + "/" +
                        x.Total.ToString(CultureInfo.InvariantCulture);
                    progress?.Report(new ImportProgressInfo(percent, message));
                });

                await LocalFormStorageService.SaveBulkUpsertByTtIohContextAsync(upsertContext, cancellationToken, writeProgress);
                result.Inserted = upsertContext.Result.Inserted;
                result.Updated = upsertContext.Result.Updated;
                result.StorageSkipped = upsertContext.Result.Skipped;
                result.SkippedExisting = upsertContext.Result.SkippedExisting;
                progress?.Report(new ImportProgressInfo(100, "Import selesai."));
                return result;
            }, cancellationToken);
        }

        private static ImportPreviewResult BuildPreviewInternal(string filePath, int takeCount, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("File import tidak ditemukan.", filePath);
            }

            var target = Math.Max(1, takeCount);
            var result = new ImportPreviewResult();
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var targetFound = false;
            var emptyStreak = 0;
            do
            {
                var sheetName = reader.Name ?? string.Empty;
                if (!string.Equals(sheetName, TargetSheetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                targetFound = true;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.TotalRowsRead++;
                    var record = TryBuildRecord(reader);
                    if (record is null)
                    {
                        emptyStreak++;
                    }
                    else
                    {
                        result.Rows.Add(record);
                        emptyStreak = 0;
                        if (result.Rows.Count >= target)
                        {
                            break;
                        }
                    }

                    if (emptyStreak >= EmptyStreakStopThreshold && result.Rows.Count > 0)
                    {
                        break;
                    }
                }

                break;
            }
            while (reader.NextResult());

            if (!targetFound)
            {
                throw new InvalidDataException("Sheet 'All Fo Cut' tidak ditemukan pada file.");
            }

            return result;
        }

        private static LocalFormRecord? TryBuildRecord(IExcelDataReader reader)
        {
            var ttIoh = NormalizeTtIoh(GetText(reader, ColTtIoh));
            if (string.IsNullOrWhiteSpace(ttIoh))
            {
                ttIoh = ExtractTtIoh(GetText(reader, ColTitle));
            }

            if (string.IsNullOrWhiteSpace(ttIoh))
            {
                return null;
            }

            var occur = ParseDateTimeOffset(reader.GetValue(ColOccur)) ?? DateTimeOffset.Now;
            var dispatch = ParseDateTimeOffset(reader.GetValue(ColDispatch)) ?? occur;
            var detail = GetText(reader, ColDetail);
            var specific = GetText(reader, ColSpecific);
            var rootCause = CombineRootCause(detail, specific);
            var cutPoint = ChooseCutPoint(GetText(reader, ColCutPoint), GetText(reader, ColAddress));
            var coordinate = BuildCoordinate(GetText(reader, ColLatitude), GetText(reader, ColLongitude));
            var now = DateTimeOffset.Now;

            return new LocalFormRecord
            {
                Name = ttIoh,
                SavedAt = now,
                TtIoh = ttIoh,
                Title = GetText(reader, ColTitle),
                OccurDateTime = occur,
                DispatchDateTime = dispatch,
                StatusLink = NormalizeStatusLink(GetText(reader, ColStatusLink)),
                Pic = GetText(reader, ColPic),
                RootCause = rootCause,
                CutPoint = cutPoint,
                ShowSegmentRoute = true,
                ShowSystemKey = false,
                SegmentRoute = GetText(reader, ColSegmentRoute),
                SystemKey = string.Empty,
                Coordinate = coordinate,
                UpdateProgress = FormatUpdateProgress(GetText(reader, ColProgress))
            };
        }

        private static ImportProgressInfo BuildProgress(
            ImportExecutionResult result,
            int rowCountHint,
            string stage)
        {
            double percent;
            if (rowCountHint <= 0)
            {
                percent = Math.Min(68, 5 + (result.TotalRowsRead / 1200d));
            }
            else
            {
                percent = Math.Min(68, (result.TotalRowsRead * 68d) / rowCountHint);
            }

            var message =
                stage +
                " | Scanned " + result.TotalRowsRead.ToString(CultureInfo.InvariantCulture) +
                ", valid " + result.ValidRows.ToString(CultureInfo.InvariantCulture);
            return new ImportProgressInfo(percent, message);
        }

        private static string GetText(IExcelDataReader reader, int columnIndex)
        {
            try
            {
                if (columnIndex < 0 || columnIndex >= reader.FieldCount)
                {
                    return string.Empty;
                }

                var value = reader.GetValue(columnIndex);
                if (value is null)
                {
                    return string.Empty;
                }

                return value switch
                {
                    DateTime dateTime => dateTime.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture),
                    double number when number > 20000 && number < 70000 => DateTime.FromOADate(number).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture),
                    _ => value.ToString()?.Trim() ?? string.Empty
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DateTimeOffset? ParseDateTimeOffset(object? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is DateTime dt)
            {
                return new DateTimeOffset(dt);
            }

            if (value is double d && d > 20000 && d < 70000)
            {
                return new DateTimeOffset(DateTime.FromOADate(d));
            }

            var text = value.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dtParsed))
            {
                return new DateTimeOffset(dtParsed);
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                return parsed;
            }

            return null;
        }

        private static string CombineRootCause(string detail, string specific)
        {
            var cleanDetail = NormalizeText(detail);
            var cleanSpecific = NormalizeText(specific);
            if (string.IsNullOrWhiteSpace(cleanDetail))
            {
                return cleanSpecific;
            }

            if (string.IsNullOrWhiteSpace(cleanSpecific) ||
                string.Equals(cleanDetail, cleanSpecific, StringComparison.OrdinalIgnoreCase))
            {
                return cleanDetail;
            }

            return cleanDetail + " | " + cleanSpecific;
        }

        private static string ChooseCutPoint(string cutPoint, string address)
        {
            var cleanCutPoint = NormalizeText(cutPoint);
            return string.IsNullOrWhiteSpace(cleanCutPoint) ? NormalizeText(address) : cleanCutPoint;
        }

        private static string BuildCoordinate(string lat, string lon)
        {
            var cleanLat = NormalizeText(lat);
            var cleanLon = NormalizeText(lon);
            if (string.IsNullOrWhiteSpace(cleanLat) || string.IsNullOrWhiteSpace(cleanLon))
            {
                return string.Empty;
            }

            return cleanLat + "," + cleanLon;
        }

        private static string NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string NormalizeStatusLink(string? value)
        {
            var text = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (string.Equals(text, "open", StringComparison.OrdinalIgnoreCase))
            {
                return "Open";
            }

            if (string.Equals(text, "close", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "closed", StringComparison.OrdinalIgnoreCase))
            {
                return "Closed";
            }

            if (string.Equals(text, "cancel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "Cancel";
            }

            return text;
        }

        private static string FormatUpdateProgress(string? rawValue)
        {
            var raw = NormalizeText(rawValue);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var normalizedNewLine = raw
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            var lines = normalizedNewLine
                .Split('\n')
                .Select(CleanProgressLine)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (lines.Count == 0)
            {
                return string.Empty;
            }

            var withParsedDate = lines
                .Select(x => new ProgressLineItem(x, TryParseProgressDate(x)))
                .ToList();

            if (withParsedDate.Any(x => x.ParsedAt.HasValue))
            {
                withParsedDate = withParsedDate
                    .OrderBy(x => x.ParsedAt ?? DateTimeOffset.MaxValue)
                    .ThenBy(x => x.Text, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return string.Join(Environment.NewLine, withParsedDate.Select(x => x.Text));
        }

        private static string CleanProgressLine(string line)
        {
            return Regex.Replace(line.Trim(), @"\s+", " ").Trim();
        }

        private static DateTimeOffset? TryParseProgressDate(string line)
        {
            var match = ProgressDateRegex.Match(line);
            if (!match.Success)
            {
                return null;
            }

            var dateText = match.Groups["dt"].Value.Trim();
            if (DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            if (DateTimeOffset.TryParse(dateText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                return parsed;
            }

            return null;
        }

        private static string NormalizeTtIoh(string? value)
        {
            var text = NormalizeText(value).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return TtIohRegex.IsMatch(text) ? text : string.Empty;
        }

        private static string ExtractTtIoh(string? source)
        {
            var text = NormalizeText(source).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = TtIohRegex.Match(text);
            return match.Success ? match.Value : string.Empty;
        }

        public sealed class ImportPreviewResult
        {
            public List<LocalFormRecord> Rows { get; } = new();
            public int TotalRowsRead { get; set; }
        }

        public sealed class ImportExecutionResult
        {
            public int TotalRowsRead { get; set; }
            public int ValidRows { get; set; }
            public int SkippedRows { get; set; }
            public int Inserted { get; set; }
            public int Updated { get; set; }
            public int StorageSkipped { get; set; }
            public int SkippedExisting { get; set; }
        }

        public sealed class ImportProgressInfo
        {
            public ImportProgressInfo(double percent, string message)
            {
                Percent = percent;
                Message = message;
            }

            public double Percent { get; }
            public string Message { get; }
        }

        private sealed class ProgressLineItem
        {
            public ProgressLineItem(string text, DateTimeOffset? parsedAt)
            {
                Text = text;
                ParsedAt = parsedAt;
            }

            public string Text { get; }
            public DateTimeOffset? ParsedAt { get; }
        }
    }
}
