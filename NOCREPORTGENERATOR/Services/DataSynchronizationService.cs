using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class DataSynchronizationService
    {
        public static async Task<SyncAllResult> SynchronizeAllAsync(
            IProgress<SyncProgressInfo>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgressInfo(5, "Sinkronisasi dimulai..."));
            progress?.Report(new SyncProgressInfo(10, "1/5 Refresh cache DATABASE_LINK..."));
            await DatabaseLinkLookupService.RefreshCacheAsync();

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgressInfo(22, "2/5 Import SEGMENTPM.xlsx..."));
            var segmentImport = await SegmentPmMapService.ImportFromWorkbookAsync(password: "no", cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgressInfo(40, "3/5 Import TT dari DATABASE_TT.xlsx..."));
            var databaseTtPath = ResolveDatabaseTtPath();
            TtTrackerImportService.ImportExecutionResult? ttImportResult = null;
            if (!string.IsNullOrWhiteSpace(databaseTtPath) && File.Exists(databaseTtPath))
            {
                var ttProgress = new Progress<TtTrackerImportService.ImportProgressInfo>(p =>
                {
                    var stagePercent = 40 + (p.Percent * 0.4); // 40..80
                    progress?.Report(new SyncProgressInfo(stagePercent, "Import DATABASE_TT: " + p.Message));
                });

                ttImportResult = await TtTrackerImportService.ImportAsync(databaseTtPath, cancellationToken, ttProgress);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgressInfo(86, "4/5 Load mapping Segment PM dari DATABASE_TT.xlsx..."));
            DatabaseTtSegmentPmLookupService.InvalidateCache();
            var segmentPmByTtIoh = await DatabaseTtSegmentPmLookupService.GetSegmentPmByTtIohAsync(cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new SyncProgressInfo(94, "5/5 Sinkron Segment PM ke History TT lokal..."));
            var updatedHistoryRows = await LocalFormStorageService.SynchronizeSegmentPmByTtIohAsync(segmentPmByTtIoh);

            var result = new SyncAllResult
            {
                SegmentPmLinkCount = segmentImport.InsertedLinks,
                SegmentPmLinkRowsRead = segmentImport.ProcessedRows,
                SegmentPmMappedTtCount = segmentPmByTtIoh.Count,
                UpdatedHistoryRows = updatedHistoryRows + (ttImportResult?.Inserted ?? 0),
                ImportedFromDatabaseTt = ttImportResult?.Inserted ?? 0,
                CompletedAt = DateTimeOffset.Now
            };

            progress?.Report(new SyncProgressInfo(
                100,
                "Selesai. link=" + result.SegmentPmLinkCount.ToString(CultureInfo.InvariantCulture) +
                ", mapTT=" + result.SegmentPmMappedTtCount.ToString(CultureInfo.InvariantCulture) +
                ", importDBTT=" + result.ImportedFromDatabaseTt.ToString(CultureInfo.InvariantCulture) +
                ", updateHistory=" + result.UpdatedHistoryRows.ToString(CultureInfo.InvariantCulture)));

            return result;
        }

        private static string ResolveDatabaseTtPath()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DATABASE_TT.xlsx"),
                Path.Combine(AppContext.BaseDirectory, "DATABASE_TT.xlsx"),
                Path.Combine(Environment.CurrentDirectory, "DATABASE_TT.xlsx")
            };

            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        public sealed class SyncAllResult
        {
            public int SegmentPmLinkCount { get; set; }
            public int SegmentPmLinkRowsRead { get; set; }
            public int SegmentPmMappedTtCount { get; set; }
            public int UpdatedHistoryRows { get; set; }
            public int ImportedFromDatabaseTt { get; set; }
            public DateTimeOffset CompletedAt { get; set; }
        }

        public sealed class SyncProgressInfo
        {
            public SyncProgressInfo(double percent, string message)
            {
                Percent = percent;
                Message = message;
            }

            public double Percent { get; }
            public string Message { get; }
        }
    }
}
