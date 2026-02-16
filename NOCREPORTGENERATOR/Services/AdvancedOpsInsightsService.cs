using NOCREPORTGENERATOR.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NOCREPORTGENERATOR.Services
{
    public enum OpsPageKind
    {
        IncidentDetail360,
        SlaBreachMonitor,
        NocCommandCenter,
        RcaProblemManagement,
        WorkloadShiftPlanner,
        AutomationRules,
        IntegrationHealth,
        DataQualityCenter,
        NotificationCenter,
        ReportBuilder
    }

    public sealed class OpsListItem
    {
        public OpsListItem(string title, string detail, string? recordId = null)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "-" : title;
            Detail = string.IsNullOrWhiteSpace(detail) ? "-" : detail;
            RecordId = recordId ?? string.Empty;
        }

        public string Title { get; }
        public string Detail { get; }
        public string RecordId { get; }
    }

    public sealed class OpsChartPoint
    {
        public OpsChartPoint(string label, int value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public int Value { get; }
    }

    public sealed class OpsPageData
    {
        public OpsPageData(
            string summary,
            string kpi1Label,
            string kpi1Value,
            string kpi2Label,
            string kpi2Value,
            string kpi3Label,
            string kpi3Value,
            IReadOnlyList<OpsChartPoint> chart,
            IReadOnlyList<OpsListItem> leftItems,
            IReadOnlyList<OpsListItem> rightItems)
        {
            Summary = summary;
            Kpi1Label = kpi1Label;
            Kpi1Value = kpi1Value;
            Kpi2Label = kpi2Label;
            Kpi2Value = kpi2Value;
            Kpi3Label = kpi3Label;
            Kpi3Value = kpi3Value;
            Chart = chart;
            LeftItems = leftItems;
            RightItems = rightItems;
        }

        public string Summary { get; }
        public string Kpi1Label { get; }
        public string Kpi1Value { get; }
        public string Kpi2Label { get; }
        public string Kpi2Value { get; }
        public string Kpi3Label { get; }
        public string Kpi3Value { get; }
        public IReadOnlyList<OpsChartPoint> Chart { get; }
        public IReadOnlyList<OpsListItem> LeftItems { get; }
        public IReadOnlyList<OpsListItem> RightItems { get; }
    }

    public static class AdvancedOpsInsightsService
    {
        private static readonly Regex IncRegex = new(@"INC-\d{8}-\d{8}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StatusSeverityRegex = new(@"\[(?<status>[^-\]]+)\s*-\s*(?<severity>[^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static OpsPageData Build(
            OpsPageKind kind,
            IReadOnlyList<LocalFormRecord> records,
            ImportJobState importState,
            DateTimeOffset now)
        {
            var trend7d = BuildDailyTrend(records, now, 7);

            return kind switch
            {
                OpsPageKind.IncidentDetail360 => BuildIncident360(records, trend7d),
                OpsPageKind.SlaBreachMonitor => BuildSlaMonitor(records, now, trend7d),
                OpsPageKind.NocCommandCenter => BuildCommandCenter(records, trend7d),
                OpsPageKind.RcaProblemManagement => BuildRca(records, trend7d),
                OpsPageKind.WorkloadShiftPlanner => BuildWorkload(records, trend7d),
                OpsPageKind.AutomationRules => BuildAutomationRules(records, trend7d),
                OpsPageKind.IntegrationHealth => BuildIntegrationHealth(records, importState, now, trend7d),
                OpsPageKind.DataQualityCenter => BuildDataQuality(records, trend7d),
                OpsPageKind.NotificationCenter => BuildNotifications(records, importState, now, trend7d),
                OpsPageKind.ReportBuilder => BuildReportBuilder(records, now, trend7d),
                _ => new OpsPageData("No data", "-", "0", "-", "0", "-", "0", trend7d, Array.Empty<OpsListItem>(), Array.Empty<OpsListItem>())
            };
        }

        private static OpsPageData BuildIncident360(IReadOnlyList<LocalFormRecord> records, IReadOnlyList<OpsChartPoint> trend)
        {
            var latest = records
                .OrderByDescending(x => x.DispatchDateTime)
                .Take(12)
                .Select(x => new OpsListItem(ResolveInc(x), NormalizeStatus(GetStatus(x)) + " | " + x.DispatchDateTime.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture), x.Id))
                .ToList();

            var topSegment = records
                .Where(x => !string.IsNullOrWhiteSpace(x.SegmentRoute))
                .GroupBy(x => x.SegmentRoute.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .Take(6)
                .Select(x => new OpsListItem(x.Key, x.Count().ToString(CultureInfo.InvariantCulture) + " tickets"))
                .ToList();

            return new OpsPageData(
                "Total " + records.Count.ToString(CultureInfo.InvariantCulture) + " tickets",
                "Open", records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture),
                "Closed", records.Count(x => GetStatus(x) == "closed").ToString(CultureInfo.InvariantCulture),
                "Cancel", records.Count(x => GetStatus(x) == "cancel").ToString(CultureInfo.InvariantCulture),
                trend,
                latest,
                topSegment);
        }

        private static OpsPageData BuildSlaMonitor(IReadOnlyList<LocalFormRecord> records, DateTimeOffset now, IReadOnlyList<OpsChartPoint> trend)
        {
            var open = records.Where(x => GetStatus(x) == "open").ToList();
            var near = new List<(LocalFormRecord Record, double RemainingHours, string Severity)>();
            var breached = new List<(LocalFormRecord Record, double DelayHours, string Severity)>();

            foreach (var record in open)
            {
                var severity = GetSeverity(record);
                var threshold = GetSlaThresholdHours(severity);
                var age = Math.Max(0, (now - record.OccurDateTime).TotalHours);
                var remaining = threshold - age;
                if (remaining < 0)
                {
                    breached.Add((record, Math.Abs(remaining), severity));
                }
                else if (remaining <= Math.Max(0.5, threshold * 0.3))
                {
                    near.Add((record, remaining, severity));
                }
            }

            var nearItems = near.OrderBy(x => x.RemainingHours).Take(12)
                .Select(x => new OpsListItem(ResolveInc(x.Record), x.Severity + " | remaining " + FormatDurationHours(x.RemainingHours), x.Record.Id)).ToList();
            var breachItems = breached.OrderByDescending(x => x.DelayHours).Take(12)
                .Select(x => new OpsListItem(ResolveInc(x.Record), x.Severity + " | breached " + FormatDurationHours(x.DelayHours), x.Record.Id)).ToList();

            return new OpsPageData(
                "SLA monitor live",
                "Near Breach", near.Count.ToString(CultureInfo.InvariantCulture),
                "Breached", breached.Count.ToString(CultureInfo.InvariantCulture),
                "Open", open.Count.ToString(CultureInfo.InvariantCulture),
                trend,
                nearItems,
                breachItems);
        }

        private static OpsPageData BuildCommandCenter(IReadOnlyList<LocalFormRecord> records, IReadOnlyList<OpsChartPoint> trend)
        {
            var alarms = records.Where(x => GetStatus(x) == "open")
                .Select(x => new { Record = x, Severity = GetSeverity(x), AgeHours = Math.Max(0, (DateTimeOffset.Now - x.OccurDateTime).TotalHours) })
                .OrderByDescending(x => SeverityWeight(x.Severity))
                .ThenByDescending(x => x.AgeHours)
                .Take(12)
                .Select(x => new OpsListItem(ResolveInc(x.Record), x.Severity + " | age " + FormatDurationHours(x.AgeHours), x.Record.Id))
                .ToList();

            var regional = records.Where(x => !string.IsNullOrWhiteSpace(x.SegmentRoute))
                .GroupBy(x => FirstToken(x.SegmentRoute), StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(g =>
                {
                    var total = g.Count();
                    var open = g.Count(x => GetStatus(x) == "open");
                    var healthy = total == 0 ? 100 : ((total - open) * 100d / total);
                    return new OpsListItem(g.Key, healthy.ToString("0.0", CultureInfo.InvariantCulture) + "% healthy");
                }).ToList();

            return new OpsPageData(
                "Command center live",
                "Open Alarm", records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture),
                "Critical", records.Count(x => GetStatus(x) == "open" && GetSeverity(x) == "critical").ToString(CultureInfo.InvariantCulture),
                "Major", records.Count(x => GetStatus(x) == "open" && GetSeverity(x) == "major").ToString(CultureInfo.InvariantCulture),
                trend,
                alarms,
                regional);
        }

        private static OpsPageData BuildRca(IReadOnlyList<LocalFormRecord> records, IReadOnlyList<OpsChartPoint> trend)
        {
            var causes = records.Where(x => !string.IsNullOrWhiteSpace(x.RootCause))
                .GroupBy(x => NormalizeToken(x.RootCause), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .Take(10)
                .Select(g => new OpsListItem(Shorten(g.Key, 52), g.Count().ToString(CultureInfo.InvariantCulture) + " incidents"))
                .ToList();

            var actions = causes.Take(8).Select(c => new OpsListItem("Action", "Prepare mitigation for: " + c.Title)).ToList();

            return new OpsPageData(
                "RCA backlog",
                "Unique Cause", causes.Count.ToString(CultureInfo.InvariantCulture),
                "With Cause", records.Count(x => !string.IsNullOrWhiteSpace(x.RootCause)).ToString(CultureInfo.InvariantCulture),
                "Without Cause", records.Count(x => string.IsNullOrWhiteSpace(x.RootCause)).ToString(CultureInfo.InvariantCulture),
                trend,
                causes,
                actions);
        }

        private static OpsPageData BuildWorkload(IReadOnlyList<LocalFormRecord> records, IReadOnlyList<OpsChartPoint> trend)
        {
            var openByPic = records.Where(x => GetStatus(x) == "open" && !string.IsNullOrWhiteSpace(x.Pic))
                .GroupBy(x => x.Pic.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Pic = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(12)
                .ToList();

            var left = openByPic.Select(x => new OpsListItem(x.Pic, x.Count.ToString(CultureInfo.InvariantCulture) + " open TT")).ToList();
            var shift = new[]
            {
                new OpsListItem("Shift Pagi", openByPic.Take(4).Sum(x => x.Count).ToString(CultureInfo.InvariantCulture) + " workload"),
                new OpsListItem("Shift Sore", openByPic.Skip(4).Take(4).Sum(x => x.Count).ToString(CultureInfo.InvariantCulture) + " workload"),
                new OpsListItem("Shift Malam", openByPic.Skip(8).Take(4).Sum(x => x.Count).ToString(CultureInfo.InvariantCulture) + " workload")
            }.ToList();

            return new OpsPageData(
                "Shift load distribution",
                "PIC Active", openByPic.Count.ToString(CultureInfo.InvariantCulture),
                "Max Load", (openByPic.FirstOrDefault()?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                "Open TT", records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture),
                trend,
                left,
                shift);
        }

        private static OpsPageData BuildAutomationRules(IReadOnlyList<LocalFormRecord> records, IReadOnlyList<OpsChartPoint> trend)
        {
            var open = records.Where(x => GetStatus(x) == "open").ToList();
            var backbone = open.Count(x => Contains(x.SegmentRoute, "BACKBONE"));
            var critical = open.Count(x => GetSeverity(x) == "critical");
            var noPic = open.Count(x => string.IsNullOrWhiteSpace(x.Pic));
            var duplicateTitle = open.Where(x => !string.IsNullOrWhiteSpace(x.Title))
                .GroupBy(x => x.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .Count(x => x.Count() > 1);

            var activeRules = new List<OpsListItem>
            {
                new("RULE-001", "BACKBONE => priority major | hit " + backbone),
                new("RULE-002", "Critical open => escalate L3 | hit " + critical),
                new("RULE-003", "No PIC => auto-assign | hit " + noPic),
                new("RULE-004", "Duplicate title => link parent | hit " + duplicateTitle)
            };
            var simulation = activeRules.Select(x => new OpsListItem("Simulation", x.Detail)).ToList();

            return new OpsPageData(
                "Rule engine snapshot",
                "Rules Active", activeRules.Count.ToString(CultureInfo.InvariantCulture),
                "Open Evaluated", open.Count.ToString(CultureInfo.InvariantCulture),
                "Auto Candidate", (noPic + duplicateTitle).ToString(CultureInfo.InvariantCulture),
                trend,
                activeRules,
                simulation);
        }

        private static OpsPageData BuildIntegrationHealth(IReadOnlyList<LocalFormRecord> records, ImportJobState state, DateTimeOffset now, IReadOnlyList<OpsChartPoint> trend)
        {
            var latest = records.OrderByDescending(x => x.SavedAt).FirstOrDefault();
            var freshness = latest is null ? "no data" : (now - latest.SavedAt).TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min ago";

            var connectors = new List<OpsListItem>
            {
                new("DATABASE_LINK", "ready"),
                new("SQLite Cache", "ready"),
                new("Import Pipeline", state.IsActive ? "running" : "idle"),
                new("Local Draft Storage", "ready")
            };

            var logs = new List<OpsListItem>
            {
                new("Last import state", string.IsNullOrWhiteSpace(state.Message) ? "-" : state.Message),
                new("Last data refresh", freshness),
                new("Record count", records.Count.ToString(CultureInfo.InvariantCulture)),
                new("Open records", records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture))
            };

            return new OpsPageData(
                "Integration status",
                "Import", state.IsActive ? "Running" : "Idle",
                "Freshness", freshness,
                "Records", records.Count.ToString(CultureInfo.InvariantCulture),
                trend,
                connectors,
                logs);
        }

        private static OpsPageData BuildDataQuality(IReadOnlyList<LocalFormRecord> records, IReadOnlyList<OpsChartPoint> trend)
        {
            var missingSystem = records.Where(x => string.IsNullOrWhiteSpace(x.SystemKey)).Take(12).ToList();
            var missingPic = records.Where(x => string.IsNullOrWhiteSpace(x.Pic)).Take(12).ToList();
            var invalidCoord = records.Where(x => !string.IsNullOrWhiteSpace(x.Coordinate) && !x.Coordinate.Contains(",", StringComparison.Ordinal)).Take(12).ToList();
            var unknownSegment = records.Where(x => string.IsNullOrWhiteSpace(x.SegmentRoute)).Take(12).ToList();

            var violations = new List<OpsListItem>
            {
                new("Missing System Key", missingSystem.Count.ToString(CultureInfo.InvariantCulture)),
                new("Missing PIC", missingPic.Count.ToString(CultureInfo.InvariantCulture)),
                new("Invalid Coordinate", invalidCoord.Count.ToString(CultureInfo.InvariantCulture)),
                new("Missing Segment", unknownSegment.Count.ToString(CultureInfo.InvariantCulture))
            };

            var samples = missingSystem.Concat(missingPic).Concat(unknownSegment)
                .Take(12)
                .Select(x => new OpsListItem(ResolveInc(x), "Needs enrichment", x.Id))
                .ToList();

            return new OpsPageData(
                "Data quality watchdog",
                "Missing Key", missingSystem.Count.ToString(CultureInfo.InvariantCulture),
                "Missing PIC", missingPic.Count.ToString(CultureInfo.InvariantCulture),
                "Missing Segment", unknownSegment.Count.ToString(CultureInfo.InvariantCulture),
                trend,
                violations,
                samples);
        }

        private static OpsPageData BuildNotifications(IReadOnlyList<LocalFormRecord> records, ImportJobState state, DateTimeOffset now, IReadOnlyList<OpsChartPoint> trend)
        {
            var recent = records.OrderByDescending(x => x.SavedAt).Take(12)
                .Select(x => new OpsListItem(ResolveInc(x), NormalizeStatus(GetStatus(x)) + " | " + x.SavedAt.ToString("dd-MM HH:mm", CultureInfo.InvariantCulture), x.Id))
                .ToList();

            var activity = new List<OpsListItem>
            {
                new("Import state", state.IsActive ? "Running" : "Idle"),
                new("Last message", string.IsNullOrWhiteSpace(state.Message) ? "-" : state.Message),
                new("Open tickets", records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture)),
                new("Clock", now.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture))
            };

            return new OpsPageData(
                "Notification stream",
                "Unread", recent.Count.ToString(CultureInfo.InvariantCulture),
                "Open", records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture),
                "Import", state.IsActive ? "Running" : "Idle",
                trend,
                recent,
                activity);
        }

        private static OpsPageData BuildReportBuilder(IReadOnlyList<LocalFormRecord> records, DateTimeOffset now, IReadOnlyList<OpsChartPoint> trend)
        {
            var templates = new List<OpsListItem>
            {
                new("Daily NOC Summary", records.Count(x => x.OccurDateTime.LocalDateTime.Date == now.LocalDateTime.Date).ToString(CultureInfo.InvariantCulture) + " today"),
                new("Weekly Incident Trend", records.Count(x => x.OccurDateTime.LocalDateTime.Date >= now.LocalDateTime.Date.AddDays(-7)).ToString(CultureInfo.InvariantCulture) + " last 7d"),
                new("Monthly SLA Compliance", records.Count(x => x.OccurDateTime.LocalDateTime.Date >= now.LocalDateTime.Date.AddDays(-30)).ToString(CultureInfo.InvariantCulture) + " last 30d"),
                new("Executive Snapshot", "Open " + records.Count(x => GetStatus(x) == "open").ToString(CultureInfo.InvariantCulture))
            };

            var queue = records.OrderByDescending(x => x.SavedAt).Take(8)
                .Select((x, i) => new OpsListItem("JOB-" + (2900 + i).ToString(CultureInfo.InvariantCulture), ResolveInc(x) + " | source ticket", x.Id))
                .ToList();

            return new OpsPageData(
                "Report pipeline",
                "Templates", templates.Count.ToString(CultureInfo.InvariantCulture),
                "Queue", queue.Count.ToString(CultureInfo.InvariantCulture),
                "Total Records", records.Count.ToString(CultureInfo.InvariantCulture),
                trend,
                templates,
                queue);
        }

        private static IReadOnlyList<OpsChartPoint> BuildDailyTrend(IReadOnlyList<LocalFormRecord> records, DateTimeOffset now, int days)
        {
            var output = new List<OpsChartPoint>();
            for (var i = days - 1; i >= 0; i--)
            {
                var day = now.LocalDateTime.Date.AddDays(-i);
                var count = records.Count(x => x.OccurDateTime.LocalDateTime.Date == day);
                output.Add(new OpsChartPoint(day.ToString("dd MMM", CultureInfo.InvariantCulture), count));
            }

            return output;
        }

        private static string ResolveInc(LocalFormRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.TtIoh))
            {
                return record.TtIoh.Trim().ToUpperInvariant();
            }

            var match = IncRegex.Match(record.Title ?? string.Empty);
            return match.Success ? match.Value.ToUpperInvariant() : (string.IsNullOrWhiteSpace(record.Name) ? "INC-UNKNOWN" : record.Name);
        }

        private static string GetStatus(LocalFormRecord record)
        {
            var normalized = NormalizeToken(record.StatusLink);
            if (normalized is "close" or "closed") return "closed";
            if (normalized is "cancel" or "cancelled" or "canceled") return "cancel";
            if (!string.IsNullOrWhiteSpace(normalized)) return normalized;

            var match = StatusSeverityRegex.Match(record.Title ?? string.Empty);
            if (!match.Success) return "open";

            var fromTitle = NormalizeToken(match.Groups["status"].Value);
            if (fromTitle is "close" or "closed") return "closed";
            if (fromTitle is "cancel" or "cancelled" or "canceled") return "cancel";
            return string.IsNullOrWhiteSpace(fromTitle) ? "open" : fromTitle;
        }

        private static string GetSeverity(LocalFormRecord record)
        {
            var match = StatusSeverityRegex.Match(record.Title ?? string.Empty);
            if (!match.Success) return "minor";
            var severity = NormalizeToken(match.Groups["severity"].Value);
            return string.IsNullOrWhiteSpace(severity) ? "minor" : severity;
        }

        private static int SeverityWeight(string severity)
        {
            return severity switch { "critical" => 3, "major" => 2, "minor" => 1, _ => 0 };
        }

        private static double GetSlaThresholdHours(string severity)
        {
            return severity switch { "critical" => 2, "major" => 4, "minor" => 8, _ => 12 };
        }

        private static string NormalizeStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Open";
            return char.ToUpperInvariant(status[0]) + status.Substring(1).ToLowerInvariant();
        }

        private static string FirstToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            var token = value.Trim().Split(new[] { '-', '_', '/', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(token) ? "Unknown" : token.ToUpperInvariant();
        }

        private static bool Contains(string? source, string keyword)
        {
            return !string.IsNullOrWhiteSpace(source) && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeToken(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= max) return value;
            return value.Substring(0, Math.Max(1, max - 1)) + "...";
        }

        private static string FormatDurationHours(double hours)
        {
            var totalMinutes = Math.Max(0, (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero));
            var hh = totalMinutes / 60;
            var mm = totalMinutes % 60;
            return hh.ToString(CultureInfo.InvariantCulture) + "h " + mm.ToString("00", CultureInfo.InvariantCulture) + "m";
        }
    }
}
