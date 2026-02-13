using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR.Services
{
    public static class GeminiCoordinateParserService
    {
        private static readonly HttpClient HttpClient = new();

        public static async Task<CoordinateExtractionResult> ExtractDmsCoordinateFromImageAsync(
            byte[] imageBytes,
            string mimeType,
            CancellationToken cancellationToken = default)
        {
            if (imageBytes is null || imageBytes.Length == 0)
            {
                return CoordinateExtractionResult.Fail("File gambar kosong.");
            }

            var apiKey = AppSettingsService.GetGeminiApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return CoordinateExtractionResult.Fail("API key Gemini belum diisi di Settings.");
            }

            var requestBody = BuildRequestBody(imageBytes, mimeType);
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
                return CoordinateExtractionResult.Fail("Gagal akses Gemini API: " + ex.Message);
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CoordinateExtractionResult.Fail("Gemini API error (" + (int)response.StatusCode + "): " + TrimError(responseText));
            }

            var candidateText = ExtractCandidateText(responseText);
            if (string.IsNullOrWhiteSpace(candidateText))
            {
                return CoordinateExtractionResult.Fail("Gemini tidak mengembalikan kandidat koordinat.");
            }

            if (TryExtractLatLonFromJson(candidateText, out var lat, out var lon) ||
                TryExtractLatLonFromJson(responseText, out lat, out lon) ||
                CoordinateFormatService.TryParseAnyToDecimal(candidateText, out lat, out lon))
            {
                return CoordinateExtractionResult.Success(CoordinateFormatService.ToDms(lat, lon), lat, lon);
            }

            return CoordinateExtractionResult.Fail("Koordinat tidak ditemukan dari hasil AI.");
        }

        private static string BuildRequestBody(byte[] imageBytes, string mimeType)
        {
            var base64 = Convert.ToBase64String(imageBytes);
            var safeMime = string.IsNullOrWhiteSpace(mimeType) ? "image/jpeg" : mimeType.Trim().ToLowerInvariant();
            var prompt =
                "Extract only geographic coordinates from this image watermark/text. " +
                "Accept any format (decimal, DMS, hemisphere). " +
                "Return strict JSON only with this schema: " +
                "{\"latitude\": number|null, \"longitude\": number|null, \"raw_text\": string, \"confidence\": number}. " +
                "Latitude and longitude must be decimal degrees.";

            return JsonSerializer.Serialize(new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = safeMime,
                                    data = base64
                                }
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.1,
                    responseMimeType = "application/json"
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

        private static bool TryExtractLatLonFromJson(string source, out double lat, out double lon)
        {
            lat = default;
            lon = default;
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            var jsonCandidate = TryExtractJsonObject(source);
            if (string.IsNullOrWhiteSpace(jsonCandidate))
            {
                jsonCandidate = source;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonCandidate);
                var root = doc.RootElement;
                if (!TryGetDouble(root, "latitude", out lat) || !TryGetDouble(root, "longitude", out lon))
                {
                    return false;
                }

                return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
            }
            catch
            {
                return false;
            }
        }

        private static string TryExtractJsonObject(string source)
        {
            var match = Regex.Match(source, @"\{[\s\S]*\}");
            return match.Success ? match.Value : string.Empty;
        }

        private static bool TryGetDouble(JsonElement root, string property, out double value)
        {
            value = default;
            if (!root.TryGetProperty(property, out var element))
            {
                return false;
            }

            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetDouble(out value);
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(
                    (element.GetString() ?? string.Empty).Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value);
            }

            return false;
        }

        private static string TrimError(string text)
        {
            var clean = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
            return clean.Length <= 260 ? clean : clean.Substring(0, 260) + "...";
        }
    }

    public sealed class CoordinateExtractionResult
    {
        private CoordinateExtractionResult(bool isSuccess, string message, string coordinateDms, double latitude, double longitude)
        {
            IsSuccess = isSuccess;
            Message = message;
            CoordinateDms = coordinateDms;
            Latitude = latitude;
            Longitude = longitude;
        }

        public bool IsSuccess { get; }
        public string Message { get; }
        public string CoordinateDms { get; }
        public double Latitude { get; }
        public double Longitude { get; }

        public static CoordinateExtractionResult Success(string coordinateDms, double latitude, double longitude)
        {
            return new CoordinateExtractionResult(true, "OK", coordinateDms, latitude, longitude);
        }

        public static CoordinateExtractionResult Fail(string message)
        {
            return new CoordinateExtractionResult(false, message, string.Empty, 0d, 0d);
        }
    }
}
