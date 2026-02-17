using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NOCREPORTGENERATOR.Models
{
    public sealed class LocalFormRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

        public string TtIoh { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTimeOffset OccurDateTime { get; set; }
        public DateTimeOffset DispatchDateTime { get; set; }
        public DateTimeOffset FinishDateTime { get; set; }
        public string StatusLink { get; set; } = string.Empty;
        public string Pic { get; set; } = string.Empty;
        public string RootCause { get; set; } = string.Empty;
        public string CutPoint { get; set; } = string.Empty;
        public bool ShowSegmentRoute { get; set; } = true;
        public bool ShowSystemKey { get; set; } = true;
        public string SegmentRoute { get; set; } = string.Empty;
        public string SegmentPm { get; set; } = string.Empty;
        public string SystemKey { get; set; } = string.Empty;
        public string Coordinate { get; set; } = string.Empty;
        public string UpdateProgress { get; set; } = string.Empty;
        public List<ImpactListItem> ImpactList { get; set; } = new();

        [JsonIgnore]
        public string DisplayName => $"{SavedAt:dd-MM-yyyy HH:mm} - {Name}";
    }

    public sealed class ImpactListItem
    {
        public string Impact { get; set; } = string.Empty;
        public string StatusLink { get; set; } = string.Empty;
    }
}
