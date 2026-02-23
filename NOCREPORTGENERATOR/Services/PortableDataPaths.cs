using System;
using System.IO;

namespace NOCREPORTGENERATOR.Services
{
    public static class PortableDataPaths
    {
        private const string DataDirectoryName = "Data";

        public static string DataDirectoryPath =>
            Path.Combine(AppContext.BaseDirectory, DataDirectoryName);

        public static string LocalFormsDbPath =>
            Path.Combine(DataDirectoryPath, "tt_forms.db");

        public static string SegmentPmDbPath =>
            Path.Combine(DataDirectoryPath, "segmentpm_map.db");

        public static string DatabaseLinkCacheDbPath =>
            Path.Combine(DataDirectoryPath, "database_link_cache.db");

        public static string LiveMapSnapshotPath =>
            Path.Combine(DataDirectoryPath, "live_map_snapshot.json");

        public static void EnsureDataDirectory()
        {
            Directory.CreateDirectory(DataDirectoryPath);
        }
    }
}
