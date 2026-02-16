using NOCREPORTGENERATOR.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class ReportExportService
    {
        public static Task ExportSummaryPdfAsync(string outputPath, IReadOnlyList<LocalFormRecord> records, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Output path tidak valid.", nameof(outputPath));
            }

            var all = records ?? Array.Empty<LocalFormRecord>();
            var open = all.Count(x => GetStatus(x).Equals("open", StringComparison.OrdinalIgnoreCase));
            var closed = all.Count(x => GetStatus(x).Equals("closed", StringComparison.OrdinalIgnoreCase));
            var cancel = all.Count(x => GetStatus(x).Equals("cancel", StringComparison.OrdinalIgnoreCase));
            var last7 = all.Where(x => x.OccurDateTime.LocalDateTime.Date >= now.LocalDateTime.Date.AddDays(-7)).ToList();

            return Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);

                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(24);
                        page.DefaultTextStyle(x => x.FontSize(10));

                        page.Header().Column(col =>
                        {
                            col.Item().Text("NOC Report Generator - Executive Summary").SemiBold().FontSize(16);
                            col.Item().Text("Generated: " + now.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture));
                        });

                        page.Content().Column(col =>
                        {
                            col.Spacing(12);

                            col.Item().Row(row =>
                            {
                                row.RelativeItem().Background(Colors.Grey.Lighten3).Padding(8).Column(c =>
                                {
                                    c.Item().Text("Total").SemiBold();
                                    c.Item().Text(all.Count.ToString(CultureInfo.InvariantCulture)).FontSize(14);
                                });
                                row.RelativeItem().Background(Colors.Blue.Lighten4).Padding(8).Column(c =>
                                {
                                    c.Item().Text("Open").SemiBold();
                                    c.Item().Text(open.ToString(CultureInfo.InvariantCulture)).FontSize(14);
                                });
                                row.RelativeItem().Background(Colors.Green.Lighten4).Padding(8).Column(c =>
                                {
                                    c.Item().Text("Closed").SemiBold();
                                    c.Item().Text(closed.ToString(CultureInfo.InvariantCulture)).FontSize(14);
                                });
                                row.RelativeItem().Background(Colors.Orange.Lighten4).Padding(8).Column(c =>
                                {
                                    c.Item().Text("Cancel").SemiBold();
                                    c.Item().Text(cancel.ToString(CultureInfo.InvariantCulture)).FontSize(14);
                                });
                            });

                            col.Item().Text("Latest Tickets (Top 20)").SemiBold();
                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn(2);
                                    columns.RelativeColumn(1);
                                    columns.RelativeColumn(2);
                                    columns.RelativeColumn(1);
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Element(CellStyle).Text("TT IOH").SemiBold();
                                    header.Cell().Element(CellStyle).Text("Status").SemiBold();
                                    header.Cell().Element(CellStyle).Text("Segment Route").SemiBold();
                                    header.Cell().Element(CellStyle).Text("Dispatch").SemiBold();
                                });

                                foreach (var record in all.OrderByDescending(x => x.DispatchDateTime).Take(20))
                                {
                                    table.Cell().Element(CellStyle).Text(string.IsNullOrWhiteSpace(record.TtIoh) ? "-" : record.TtIoh);
                                    table.Cell().Element(CellStyle).Text(GetStatus(record));
                                    table.Cell().Element(CellStyle).Text(string.IsNullOrWhiteSpace(record.SegmentRoute) ? "-" : record.SegmentRoute);
                                    table.Cell().Element(CellStyle).Text(record.DispatchDateTime.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture));
                                }
                            });

                            col.Item().Text("7-Day Trend").SemiBold();
                            col.Item().Column(trendCol =>
                            {
                                for (var i = 6; i >= 0; i--)
                                {
                                    var day = now.LocalDateTime.Date.AddDays(-i);
                                    var count = last7.Count(x => x.OccurDateTime.LocalDateTime.Date == day);
                                    trendCol.Item().Text(day.ToString("dd MMM", CultureInfo.InvariantCulture) + ": " + count.ToString(CultureInfo.InvariantCulture));
                                }
                            });
                        });

                        page.Footer().AlignCenter().Text(x =>
                        {
                            x.Span("NOC Report Generator");
                            x.Span(" | Page ");
                            x.CurrentPageNumber();
                            x.Span(" of ");
                            x.TotalPages();
                        });
                    });
                }).GeneratePdf(outputPath);
            });

            static IContainer CellStyle(IContainer container)
            {
                return container.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4).PaddingHorizontal(2);
            }
        }

        private static string GetStatus(LocalFormRecord record)
        {
            var status = (record.StatusLink ?? string.Empty).Trim();
            if (status.Equals("close", StringComparison.OrdinalIgnoreCase) || status.Equals("closed", StringComparison.OrdinalIgnoreCase))
            {
                return "Closed";
            }

            if (status.Equals("cancel", StringComparison.OrdinalIgnoreCase) || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return "Cancel";
            }

            return string.IsNullOrWhiteSpace(status) ? "Open" : status;
        }
    }
}
