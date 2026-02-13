using NOCREPORTGENERATOR.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class LocalFormStorageService
    {
        private static readonly object Sync = new();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOCREPORTGENERATOR");
        private static readonly string FilePath = Path.Combine(DirectoryPath, "tt_forms.json");

        public static async Task<IReadOnlyList<LocalFormRecord>> GetAllAsync()
        {
            var items = await ReadAllInternalAsync();
            return items
                .OrderByDescending(x => x.SavedAt)
                .ToList();
        }

        public static async Task SaveAsync(LocalFormRecord record)
        {
            var items = await ReadAllInternalAsync();
            items.Add(record);
            await WriteAllInternalAsync(items);
        }

        public static async Task UpsertAsync(LocalFormRecord record)
        {
            var items = await ReadAllInternalAsync();
            var index = items.FindIndex(x => string.Equals(x.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                items[index] = record;
            }
            else
            {
                items.Add(record);
            }

            await WriteAllInternalAsync(items);
        }

        public static async Task DeleteAsync(string recordId)
        {
            if (string.IsNullOrWhiteSpace(recordId))
            {
                return;
            }

            var items = await ReadAllInternalAsync();
            var removed = items.RemoveAll(x => string.Equals(x.Id, recordId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                await WriteAllInternalAsync(items);
            }
        }

        private static async Task<List<LocalFormRecord>> ReadAllInternalAsync()
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
            }

            if (!File.Exists(FilePath))
            {
                return new List<LocalFormRecord>();
            }

            await using var stream = File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var data = await JsonSerializer.DeserializeAsync<List<LocalFormRecord>>(stream);
            return data ?? new List<LocalFormRecord>();
        }

        private static async Task WriteAllInternalAsync(List<LocalFormRecord> items)
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
            }

            await using var stream = File.Open(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(stream, items, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
    }
}
