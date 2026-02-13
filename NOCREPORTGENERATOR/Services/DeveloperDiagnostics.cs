using System;
using System.IO;
using System.Text;

namespace NOCREPORTGENERATOR.Services
{
    public static class DeveloperDiagnostics
    {
        private static readonly object Sync = new();
        private static readonly StringBuilder Buffer = new();
        private static readonly string LogDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOCREPORTGENERATOR");
        private static readonly string LogFilePath = Path.Combine(LogDirectoryPath, "developer.log");
        public static bool IsDeveloperModeEnabled { get; set; }

        public static void LogError(string context, Exception? ex)
        {
            var header = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] {context}";
            string details;
            if (ex is null)
            {
                details = "No exception payload.";
            }
            else
            {
                try
                {
                    details = ex.ToString();
                }
                catch (Exception toStringEx)
                {
                    details = "Failed to render exception via ToString(). " +
                        "Type=" + ex.GetType().FullName + ", Message=" + ex.Message +
                        Environment.NewLine +
                        "ToString() failure: " + toStringEx.Message;
                }
            }
            Append(header + Environment.NewLine + details);
        }

        public static void LogInfo(string message)
        {
            Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [INFO] {message}");
        }

        public static string GetLogs()
        {
            lock (Sync)
            {
                var memoryText = Buffer.ToString();
                var fileText = string.Empty;

                if (File.Exists(LogFilePath))
                {
                    try
                    {
                        fileText = File.ReadAllText(LogFilePath);
                    }
                    catch
                    {
                        fileText = string.Empty;
                    }
                }

                if (string.IsNullOrWhiteSpace(memoryText) && string.IsNullOrWhiteSpace(fileText))
                {
                    return "No logs captured.";
                }

                if (string.IsNullOrWhiteSpace(fileText))
                {
                    return memoryText;
                }

                if (string.IsNullOrWhiteSpace(memoryText))
                {
                    return fileText;
                }

                return memoryText + Environment.NewLine + "----- FILE LOG -----" + Environment.NewLine + fileText;
            }
        }

        public static void Clear()
        {
            lock (Sync)
            {
                Buffer.Clear();
                if (File.Exists(LogFilePath))
                {
                    File.Delete(LogFilePath);
                }
            }
        }

        private static void Append(string message)
        {
            lock (Sync)
            {
                Buffer.AppendLine(message);
                Buffer.AppendLine();

                try
                {
                    Directory.CreateDirectory(LogDirectoryPath);
                    File.AppendAllText(LogFilePath, message + Environment.NewLine + Environment.NewLine);
                }
                catch
                {
                    // Keep memory logging alive even when file logging fails.
                }
            }
        }
    }
}
