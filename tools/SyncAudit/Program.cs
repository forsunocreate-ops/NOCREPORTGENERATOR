using NOCREPORTGENERATOR.Services;
using System;
using System.IO;
using System.Linq;
using System.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var basePath = @"E:\PROJECT VS\NOCREPORTGENERATOR\NOCREPORTGENERATOR";
var databaseLinkPath = Path.Combine(basePath, "DATABASE_LINK.xlsb");
var databaseTtPath = Path.Combine(basePath, "File Source", "DATABASE_TT.xlsx");
var segmentPmPath = Path.Combine(basePath, "File Source", "SEGMENTPM.xlsx");

Console.WriteLine("FILES");
Console.WriteLine($"DATABASE_LINK={databaseLinkPath} | EXISTS={File.Exists(databaseLinkPath)}");
Console.WriteLine($"DATABASE_TT={databaseTtPath} | EXISTS={File.Exists(databaseTtPath)}");
Console.WriteLine($"SEGMENTPM={segmentPmPath} | EXISTS={File.Exists(segmentPmPath)}");

Console.WriteLine("SYNC_START");
await DatabaseLinkLookupService.RefreshCacheAsync();
var segmentImport = await SegmentPmMapService.ImportFromWorkbookAsync(segmentPmPath, "no");
var ttImport = await TtTrackerImportService.ImportAsync(databaseTtPath, default);
DatabaseTtSegmentPmLookupService.InvalidateCache();
var segmentByTt = await DatabaseTtSegmentPmLookupService.GetSegmentPmByTtIohAsync(databaseTtPath);
var updatedHistory = await LocalFormStorageService.SynchronizeSegmentPmByTtIohAsync(segmentByTt);

Console.WriteLine("SYNC_DONE");
Console.WriteLine($"SEGMENT_LINKS={segmentImport.InsertedLinks}");
Console.WriteLine($"SEGMENT_MAP_TT={segmentByTt.Count}");
Console.WriteLine($"UPDATED_HISTORY={updatedHistory + ttImport.Inserted}");
Console.WriteLine($"IMPORTED_DBTT={ttImport.Inserted}");

var records = await LocalFormStorageService.GetAllAsync();
var total = records.Count;
var emptySegmentPm = records.Count(x => string.IsNullOrWhiteSpace(x.SegmentPm));
var nonEmpty = records.Where(x => !string.IsNullOrWhiteSpace(x.SegmentPm)).ToList();

Console.WriteLine($"TOTAL_RECORDS={total}");
Console.WriteLine($"EMPTY_SEGMENT_PM={emptySegmentPm}");
Console.WriteLine($"FILLED_SEGMENT_PM={nonEmpty.Count}");

foreach (var sample in nonEmpty.Take(8))
{
    Console.WriteLine($"SAMPLE|{sample.TtIoh}|{sample.SegmentRoute}|{sample.SegmentPm}");
}

foreach (var sample in records.Where(x => string.IsNullOrWhiteSpace(x.SegmentPm)).Take(8))
{
    Console.WriteLine($"EMPTY|{sample.TtIoh}|{sample.SegmentRoute}");
}
