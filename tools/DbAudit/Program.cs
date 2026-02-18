using NOCREPORTGENERATOR.Services;
using System;
using System.Linq;

var records = await LocalFormStorageService.GetAllAsync();

var a = records.Count(x => string.Equals((x.SegmentPm ?? string.Empty).Trim(), "099002-100034", StringComparison.OrdinalIgnoreCase));
var b = records.Count(x => string.Equals((x.SegmentPm ?? string.Empty).Trim(), "100034-099002", StringComparison.OrdinalIgnoreCase));

Console.WriteLine($"TOTAL={records.Count}");
Console.WriteLine($"PM_099002_100034={a}");
Console.WriteLine($"PM_100034_099002={b}");

foreach (var row in records.Where(x => string.Equals((x.SegmentPm ?? string.Empty).Trim(), "100034-099002", StringComparison.OrdinalIgnoreCase)).Take(5))
{
    Console.WriteLine($"OK_SAMPLE|{row.TtIoh}|{row.SegmentRoute}|{row.SegmentPm}");
}
