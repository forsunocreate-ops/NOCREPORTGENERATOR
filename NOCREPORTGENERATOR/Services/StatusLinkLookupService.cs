using ExcelDataReader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class StatusLinkLookupService
    {
        private const string TargetSheetName = "All Fo Cut";
        private const int ColStatusLink = 5; // F
        private static readonly string[] DefaultOptions = { "Open", "Closed", "Cancel" };

        public static Task<IReadOnlyList<string>> GetStatusLinkOptionsAsync(
            string? filePath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var option in DefaultOptions)
                {
                    values.Add(option);
                }

                try
                {
                    var path = ResolvePath(filePath);
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        return (IReadOnlyList<string>)values.ToList();
                    }

                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = ExcelReaderFactory.CreateReader(stream);

                    var targetFound = false;
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sheet = reader.Name ?? string.Empty;
                        if (!string.Equals(sheet, TargetSheetName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        targetFound = true;
                        while (reader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var status = NormalizeStatus(GetCellText(reader, ColStatusLink));
                            if (string.IsNullOrWhiteSpace(status))
                            {
                                continue;
                            }

                            values.Add(status);
                        }

                        break;
                    }
                    while (reader.NextResult());

                    if (!targetFound)
                    {
                        // fallback: use first sheet if target name missing
                        stream.Position = 0;
                        using var fallbackReader = ExcelReaderFactory.CreateReader(stream);
                        while (fallbackReader.Read())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var status = NormalizeStatus(GetCellText(fallbackReader, ColStatusLink));
                            if (!string.IsNullOrWhiteSpace(status))
                            {
                                values.Add(status);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogError("StatusLinkLookupService.GetStatusLinkOptionsAsync", ex);
                }

                var ordered = values
                    .OrderBy(x => GetPriority(x))
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return (IReadOnlyList<string>)ordered;
            }, cancellationToken);
        }

        private static int GetPriority(string value)
        {
            if (string.Equals(value, "Open", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(value, "Closed", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(value, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return 3;
        }

        private static string ResolvePath(string? filePath)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                return filePath.Trim();
            }

            var downloadPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "TT_TRACKER.xlsx");
            if (File.Exists(downloadPath))
            {
                return downloadPath;
            }

            var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(fallbackDir))
            {
                return string.Empty;
            }

            var latest = Directory.GetFiles(fallbackDir, "TT_TRACKER*.xlsx")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            return latest ?? string.Empty;
        }

        private static string NormalizeStatus(string? value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            if (string.Equals(text, "open", StringComparison.OrdinalIgnoreCase))
            {
                return "Open";
            }

            if (string.Equals(text, "closed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "close", StringComparison.OrdinalIgnoreCase))
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

        private static string GetCellText(IExcelDataReader reader, int index)
        {
            try
            {
                if (index < 0 || index >= reader.FieldCount)
                {
                    return string.Empty;
                }

                return reader.GetValue(index)?.ToString()?.Trim() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
