using NOCREPORTGENERATOR.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class LocalFormStorageService
    {
        private static readonly SemaphoreSlim DbGate = new(1, 1);
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOCREPORTGENERATOR");
        private static readonly string DbPath = Path.Combine(DirectoryPath, "tt_forms.db");
        private static readonly string LegacyJsonPath = Path.Combine(DirectoryPath, "tt_forms.json");
        private static readonly string ConnectionString = "Data Source=" + DbPath + ";Cache=Shared;Pooling=True";
        private static bool _isInitialized;

        public static async Task<IReadOnlyList<LocalFormRecord>> GetAllAsync()
        {
            await EnsureInitializedAsync();
            return await Task.Run(async () =>
            {
                await DbGate.WaitAsync();
                try
                {
                    var list = new List<LocalFormRecord>();
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT id, name, saved_at, tt_ioh, title, occur_date_time, dispatch_date_time, status_link, pic, root_cause, cut_point, " +
                        "show_segment_route, show_system_key, segment_route, system_key, coordinate, update_progress " +
                        "FROM tt_forms ORDER BY saved_at DESC;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        list.Add(MapRecord(reader));
                    }

                    return (IReadOnlyList<LocalFormRecord>)list;
                }
                finally
                {
                    DbGate.Release();
                }
            });
        }

        public static async Task<IReadOnlyList<MapCoordinateRecord>> GetCoordinateRecordsAsync()
        {
            await EnsureInitializedAsync();
            return await Task.Run(async () =>
            {
                await DbGate.WaitAsync();
                try
                {
                    var list = new List<MapCoordinateRecord>();
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT tt_ioh, title, cut_point, segment_route, dispatch_date_time, coordinate " +
                        "FROM tt_forms " +
                        "WHERE coordinate IS NOT NULL AND TRIM(coordinate) <> '' " +
                        "ORDER BY dispatch_date_time DESC;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        list.Add(new MapCoordinateRecord
                        {
                            TtIoh = SafeGetString(reader, 0, string.Empty),
                            Title = SafeGetString(reader, 1, string.Empty),
                            CutPoint = SafeGetString(reader, 2, string.Empty),
                            SegmentRoute = SafeGetString(reader, 3, string.Empty),
                            DispatchDateTime = ParseStorageDate(SafeGetString(reader, 4, string.Empty), DateTimeOffset.Now),
                            Coordinate = SafeGetString(reader, 5, string.Empty)
                        });
                    }

                    return (IReadOnlyList<MapCoordinateRecord>)list;
                }
                finally
                {
                    DbGate.Release();
                }
            });
        }

        public static async Task SaveAsync(LocalFormRecord record)
        {
            await UpsertAsync(record);
        }

        public static async Task UpsertAsync(LocalFormRecord record)
        {
            if (record is null)
            {
                return;
            }

            await EnsureInitializedAsync();
            await DbGate.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                UpsertRecord(connection, record);
            }
            finally
            {
                DbGate.Release();
            }
        }

        public static async Task DeleteAsync(string recordId)
        {
            if (string.IsNullOrWhiteSpace(recordId))
            {
                return;
            }

            await EnsureInitializedAsync();
            await DbGate.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM tt_forms WHERE id = $id;";
                command.Parameters.AddWithValue("$id", recordId.Trim());
                command.ExecuteNonQuery();
            }
            finally
            {
                DbGate.Release();
            }
        }

        public static async Task<BulkUpsertResult> BulkUpsertByTtIohAsync(IEnumerable<LocalFormRecord> records)
        {
            var incoming = records?.ToList() ?? new List<LocalFormRecord>();
            if (incoming.Count == 0)
            {
                return new BulkUpsertResult();
            }

            var context = await CreateBulkUpsertByTtIohContextAsync();
            foreach (var record in incoming)
            {
                context.Upsert(record);
            }

            await SaveBulkUpsertByTtIohContextAsync(context);
            return context.Result;
        }

        public static async Task<BulkUpsertContext> CreateBulkUpsertByTtIohContextAsync()
        {
            await EnsureInitializedAsync();
            return await Task.Run(async () =>
            {
                await DbGate.WaitAsync();
                try
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    using var connection = new SqliteConnection(ConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT tt_ioh, id FROM tt_forms " +
                        "WHERE tt_ioh IS NOT NULL AND TRIM(tt_ioh) <> '' " +
                        "ORDER BY saved_at DESC;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var ttIoh = NormalizeKey(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                        var id = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        if (!string.IsNullOrWhiteSpace(ttIoh) && !string.IsNullOrWhiteSpace(id) && !map.ContainsKey(ttIoh))
                        {
                            map[ttIoh] = id;
                        }
                    }

                    return new BulkUpsertContext(map);
                }
                finally
                {
                    DbGate.Release();
                }
            });
        }

        public static async Task SaveBulkUpsertByTtIohContextAsync(
            BulkUpsertContext context,
            CancellationToken cancellationToken = default,
            IProgress<BulkWriteProgress>? progress = null)
        {
            if (context is null)
            {
                return;
            }

            await EnsureInitializedAsync();
            await DbGate.WaitAsync();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();
                var total = context.PendingUpserts.Count;
                var processed = 0;
                foreach (var record in context.PendingUpserts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UpsertRecord(connection, record, transaction);
                    processed++;

                    if (processed % 500 == 0 || processed == total)
                    {
                        progress?.Report(new BulkWriteProgress(processed, total));
                    }
                }

                transaction.Commit();
            }
            finally
            {
                DbGate.Release();
            }
        }

        private static string NormalizeKey(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        private static async Task EnsureInitializedAsync()
        {
            if (_isInitialized)
            {
                return;
            }

            await DbGate.WaitAsync();
            try
            {
                if (_isInitialized)
                {
                    return;
                }

                Directory.CreateDirectory(DirectoryPath);
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "CREATE TABLE IF NOT EXISTS tt_forms (" +
                    "id TEXT PRIMARY KEY, " +
                    "name TEXT NOT NULL, " +
                    "saved_at TEXT NOT NULL, " +
                    "tt_ioh TEXT, " +
                    "title TEXT, " +
                    "occur_date_time TEXT, " +
                    "dispatch_date_time TEXT, " +
                    "status_link TEXT, " +
                    "pic TEXT, " +
                    "root_cause TEXT, " +
                    "cut_point TEXT, " +
                    "show_segment_route INTEGER NOT NULL, " +
                    "show_system_key INTEGER NOT NULL, " +
                    "segment_route TEXT, " +
                    "system_key TEXT, " +
                    "coordinate TEXT, " +
                    "update_progress TEXT" +
                    ");";
                command.ExecuteNonQuery();
                EnsureColumnExists(connection, "tt_forms", "status_link", "TEXT");

                using var indexCommand = connection.CreateCommand();
                indexCommand.CommandText =
                    "CREATE INDEX IF NOT EXISTS idx_tt_forms_saved_at ON tt_forms(saved_at DESC);" +
                    "CREATE INDEX IF NOT EXISTS idx_tt_forms_tt_ioh ON tt_forms(tt_ioh);";
                indexCommand.ExecuteNonQuery();

                await TryMigrateFromLegacyJsonAsync(connection);
                _isInitialized = true;
            }
            finally
            {
                DbGate.Release();
            }
        }

        private static async Task TryMigrateFromLegacyJsonAsync(SqliteConnection connection)
        {
            if (!File.Exists(LegacyJsonPath))
            {
                return;
            }

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(1) FROM tt_forms;";
            var existingCount = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (existingCount > 0)
            {
                return;
            }

            await using var stream = File.Open(LegacyJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var data = await JsonSerializer.DeserializeAsync<List<LocalFormRecord>>(stream) ?? new List<LocalFormRecord>();
            if (data.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            foreach (var item in data)
            {
                UpsertRecord(connection, item, transaction);
            }

            transaction.Commit();

            var backupPath = Path.Combine(DirectoryPath, "tt_forms.json.migrated_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".bak");
            try
            {
                File.Copy(LegacyJsonPath, backupPath, overwrite: true);
                File.Delete(LegacyJsonPath);
            }
            catch
            {
                // Migration succeeded; keeping legacy file is acceptable if backup/cleanup fails.
            }
        }

        private static void UpsertRecord(SqliteConnection connection, LocalFormRecord record, SqliteTransaction? transaction = null)
        {
            var safe = EnsureRecord(record);
            using var command = connection.CreateCommand();
            if (transaction is not null)
            {
                command.Transaction = transaction;
            }

            command.CommandText =
                "INSERT INTO tt_forms " +
                "(id, name, saved_at, tt_ioh, title, occur_date_time, dispatch_date_time, status_link, pic, root_cause, cut_point, show_segment_route, show_system_key, segment_route, system_key, coordinate, update_progress) " +
                "VALUES " +
                "($id, $name, $saved_at, $tt_ioh, $title, $occur_date_time, $dispatch_date_time, $status_link, $pic, $root_cause, $cut_point, $show_segment_route, $show_system_key, $segment_route, $system_key, $coordinate, $update_progress) " +
                "ON CONFLICT(id) DO UPDATE SET " +
                "name = excluded.name, saved_at = excluded.saved_at, tt_ioh = excluded.tt_ioh, title = excluded.title, occur_date_time = excluded.occur_date_time, " +
                "dispatch_date_time = excluded.dispatch_date_time, status_link = excluded.status_link, pic = excluded.pic, root_cause = excluded.root_cause, cut_point = excluded.cut_point, " +
                "show_segment_route = excluded.show_segment_route, show_system_key = excluded.show_system_key, segment_route = excluded.segment_route, " +
                "system_key = excluded.system_key, coordinate = excluded.coordinate, update_progress = excluded.update_progress;";

            command.Parameters.AddWithValue("$id", safe.Id);
            command.Parameters.AddWithValue("$name", safe.Name);
            command.Parameters.AddWithValue("$saved_at", ToStorageDate(safe.SavedAt));
            command.Parameters.AddWithValue("$tt_ioh", safe.TtIoh);
            command.Parameters.AddWithValue("$title", safe.Title);
            command.Parameters.AddWithValue("$occur_date_time", ToStorageDate(safe.OccurDateTime));
            command.Parameters.AddWithValue("$dispatch_date_time", ToStorageDate(safe.DispatchDateTime));
            command.Parameters.AddWithValue("$status_link", safe.StatusLink);
            command.Parameters.AddWithValue("$pic", safe.Pic);
            command.Parameters.AddWithValue("$root_cause", safe.RootCause);
            command.Parameters.AddWithValue("$cut_point", safe.CutPoint);
            command.Parameters.AddWithValue("$show_segment_route", safe.ShowSegmentRoute ? 1 : 0);
            command.Parameters.AddWithValue("$show_system_key", safe.ShowSystemKey ? 1 : 0);
            command.Parameters.AddWithValue("$segment_route", safe.SegmentRoute);
            command.Parameters.AddWithValue("$system_key", safe.SystemKey);
            command.Parameters.AddWithValue("$coordinate", safe.Coordinate);
            command.Parameters.AddWithValue("$update_progress", safe.UpdateProgress);
            command.ExecuteNonQuery();
        }

        private static LocalFormRecord MapRecord(SqliteDataReader reader)
        {
            return new LocalFormRecord
            {
                Id = SafeGetString(reader, 0, Guid.NewGuid().ToString("N")),
                Name = SafeGetString(reader, 1, string.Empty),
                SavedAt = ParseStorageDate(SafeGetString(reader, 2, string.Empty), DateTimeOffset.Now),
                TtIoh = SafeGetString(reader, 3, string.Empty),
                Title = SafeGetString(reader, 4, string.Empty),
                OccurDateTime = ParseStorageDate(SafeGetString(reader, 5, string.Empty), DateTimeOffset.Now),
                DispatchDateTime = ParseStorageDate(SafeGetString(reader, 6, string.Empty), DateTimeOffset.Now),
                StatusLink = SafeGetString(reader, 7, string.Empty),
                Pic = SafeGetString(reader, 8, string.Empty),
                RootCause = SafeGetString(reader, 9, string.Empty),
                CutPoint = SafeGetString(reader, 10, string.Empty),
                ShowSegmentRoute = SafeGetInt(reader, 11, 1) != 0,
                ShowSystemKey = SafeGetInt(reader, 12, 1) != 0,
                SegmentRoute = SafeGetString(reader, 13, string.Empty),
                SystemKey = SafeGetString(reader, 14, string.Empty),
                Coordinate = SafeGetString(reader, 15, string.Empty),
                UpdateProgress = SafeGetString(reader, 16, string.Empty)
            };
        }

        private static LocalFormRecord EnsureRecord(LocalFormRecord record)
        {
            return new LocalFormRecord
            {
                Id = string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id.Trim(),
                Name = record.Name ?? string.Empty,
                SavedAt = record.SavedAt == default ? DateTimeOffset.Now : record.SavedAt,
                TtIoh = record.TtIoh ?? string.Empty,
                Title = record.Title ?? string.Empty,
                OccurDateTime = record.OccurDateTime == default ? DateTimeOffset.Now : record.OccurDateTime,
                DispatchDateTime = record.DispatchDateTime == default ? (record.OccurDateTime == default ? DateTimeOffset.Now : record.OccurDateTime) : record.DispatchDateTime,
                StatusLink = record.StatusLink ?? string.Empty,
                Pic = record.Pic ?? string.Empty,
                RootCause = record.RootCause ?? string.Empty,
                CutPoint = record.CutPoint ?? string.Empty,
                ShowSegmentRoute = record.ShowSegmentRoute,
                ShowSystemKey = record.ShowSystemKey,
                SegmentRoute = record.SegmentRoute ?? string.Empty,
                SystemKey = record.SystemKey ?? string.Empty,
                Coordinate = record.Coordinate ?? string.Empty,
                UpdateProgress = record.UpdateProgress ?? string.Empty
            };
        }

        private static string ToStorageDate(DateTimeOffset value)
        {
            var safe = value == default ? DateTimeOffset.Now : value;
            return safe.ToString("O", CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset ParseStorageDate(string value, DateTimeOffset fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static string SafeGetString(SqliteDataReader reader, int ordinal, string fallback)
        {
            return reader.IsDBNull(ordinal) ? fallback : reader.GetString(ordinal);
        }

        private static int SafeGetInt(SqliteDataReader reader, int ordinal, int fallback)
        {
            return reader.IsDBNull(ordinal) ? fallback : reader.GetInt32(ordinal);
        }

        private static void EnsureColumnExists(SqliteConnection connection, string tableName, string columnName, string columnSqlType)
        {
            using var info = connection.CreateCommand();
            info.CommandText = "PRAGMA table_info(" + tableName + ");";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(SafeGetString(reader, 1, string.Empty), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE " + tableName + " ADD COLUMN " + columnName + " " + columnSqlType + ";";
            alter.ExecuteNonQuery();
        }

        public sealed class BulkUpsertResult
        {
            public int TotalIncoming { get; set; }
            public int Inserted { get; set; }
            public int Updated { get; set; }
            public int Skipped { get; set; }
            public int SkippedExisting { get; set; }
        }

        public sealed class BulkUpsertContext
        {
            private readonly Dictionary<string, string> _idByTtIoh;
            internal List<LocalFormRecord> PendingUpserts { get; } = new();
            public BulkUpsertResult Result { get; } = new();

            public BulkUpsertContext(Dictionary<string, string> existingIdByTtIoh)
            {
                _idByTtIoh = existingIdByTtIoh ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            public void Upsert(LocalFormRecord record)
            {
                if (record is null)
                {
                    Result.Skipped++;
                    return;
                }

                Result.TotalIncoming++;
                var key = NormalizeKey(record.TtIoh);
                if (string.IsNullOrWhiteSpace(key))
                {
                    Result.Skipped++;
                    return;
                }

                if (_idByTtIoh.TryGetValue(key, out _))
                {
                    Result.SkippedExisting++;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(record.Id))
                    {
                        record.Id = Guid.NewGuid().ToString("N");
                    }

                    _idByTtIoh[key] = record.Id;
                    PendingUpserts.Add(record);
                    Result.Inserted++;
                }
            }
        }

        public sealed class BulkWriteProgress
        {
            public BulkWriteProgress(int processed, int total)
            {
                Processed = processed;
                Total = total;
            }

            public int Processed { get; }
            public int Total { get; }
        }

        public sealed class MapCoordinateRecord
        {
            public string TtIoh { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string CutPoint { get; set; } = string.Empty;
            public string SegmentRoute { get; set; } = string.Empty;
            public DateTimeOffset DispatchDateTime { get; set; }
            public string Coordinate { get; set; } = string.Empty;
        }
    }
}
