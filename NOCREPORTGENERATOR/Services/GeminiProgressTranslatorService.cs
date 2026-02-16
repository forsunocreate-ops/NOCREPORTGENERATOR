using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class GeminiProgressTranslatorService
    {
        private static readonly HttpClient HttpClient = new();

        public static async Task<TextTranslateResult> TranslateToEnglishAsync(
            string sourceText,
            CancellationToken cancellationToken = default)
        {
            var source = (sourceText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                return TextTranslateResult.Fail("Update Progress kosong.");
            }

            var apiKey = AppSettingsService.GetGeminiApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return TextTranslateResult.Fail("API key Gemini belum diisi di Settings.");
            }

            var requestBody = BuildRequestBody(source);
            var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + Uri.EscapeDataString(apiKey);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await HttpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                return TextTranslateResult.Fail("Gagal akses Gemini API: " + ex.Message);
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return TextTranslateResult.Fail("Gemini API error (" + (int)response.StatusCode + "): " + TrimError(responseText));
            }

            var translated = ExtractCandidateText(responseText);
            if (string.IsNullOrWhiteSpace(translated))
            {
                return TextTranslateResult.Fail("Gemini tidak mengembalikan hasil terjemahan.");
            }

            return TextTranslateResult.Success(translated.Trim());
        }

        private static string BuildRequestBody(string sourceText)
        {
            var prompt =
                "You are a deterministic translator for NOC outage updates. " +
                "Translate the input text to natural professional English. " +
                "If input is already English, return it unchanged. " +
                "Keep exact line count and line breaks. " +
                "Preserve all numbers, date/time, IDs, route codes, site codes, names, and emojis exactly. " +
                "Do not add explanations or notes. Output only translated plain text.";

            return JsonSerializer.Serialize(new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { text = sourceText }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0,
                    topP = 1,
                    responseMimeType = "text/plain"
                }
            });
        }

        private static string ExtractCandidateText(string responseText)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
                {
                    return string.Empty;
                }

                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("content", out var content) ||
                        !content.TryGetProperty("parts", out var parts) ||
                        parts.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            return text.GetString() ?? string.Empty;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string TrimError(string text)
        {
            var clean = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
            return clean.Length <= 260 ? clean : clean.Substring(0, 260) + "...";
        }
    }

    public sealed class TextTranslateResult
    {
        private TextTranslateResult(bool isSuccess, string text, string message)
        {
            IsSuccess = isSuccess;
            Text = text;
            Message = message;
        }

        public bool IsSuccess { get; }
        public string Text { get; }
        public string Message { get; }

        public static TextTranslateResult Success(string text)
        {
            return new TextTranslateResult(true, text, "OK");
        }

        public static TextTranslateResult Fail(string message)
        {
            return new TextTranslateResult(false, string.Empty, message);
        }
    }
}
