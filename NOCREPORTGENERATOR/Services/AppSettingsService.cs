using Windows.Storage;

namespace NOCREPORTGENERATOR.Services
{
    public static class AppSettingsService
    {
        private const string GeminiApiKeyName = "gemini_api_key";
        private const string SegmentPmSourcePathName = "segmentpm_source_path";

        public static string GetGeminiApiKey()
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.TryGetValue(GeminiApiKeyName, out var value) && value is string text)
                {
                    return text.Trim();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static void SetGeminiApiKey(string? apiKey)
        {
            var key = apiKey?.Trim() ?? string.Empty;
            var localSettings = ApplicationData.Current.LocalSettings;
            if (string.IsNullOrWhiteSpace(key))
            {
                localSettings.Values.Remove(GeminiApiKeyName);
                return;
            }

            localSettings.Values[GeminiApiKeyName] = key;
        }

        public static string GetSegmentPmSourcePath()
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.TryGetValue(SegmentPmSourcePathName, out var value) && value is string text)
                {
                    return text.Trim();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static void SetSegmentPmSourcePath(string? filePath)
        {
            var path = filePath?.Trim() ?? string.Empty;
            var localSettings = ApplicationData.Current.LocalSettings;
            if (string.IsNullOrWhiteSpace(path))
            {
                localSettings.Values.Remove(SegmentPmSourcePathName);
                return;
            }

            localSettings.Values[SegmentPmSourcePathName] = path;
        }
    }
}
