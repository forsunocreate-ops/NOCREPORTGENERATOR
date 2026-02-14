using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NOCREPORTGENERATOR.Services
{
    public static class CoordinateFormatService
    {
        public static bool TryParseAnyToDecimal(string? text, out double latitude, out double longitude)
        {
            latitude = default;
            longitude = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (TryParseDecimalPair(text, out latitude, out longitude))
            {
                return true;
            }

            if (TryParseHemispherePair(text, out latitude, out longitude))
            {
                return true;
            }

            return false;
        }

        public static string ToDms(double latitude, double longitude)
        {
            return ToDmsSingle(latitude, true) + " " + ToDmsSingle(longitude, false);
        }

        private static bool TryParseDecimalPair(string text, out double lat, out double lon)
        {
            lat = default;
            lon = default;

            var matches = Regex.Matches(
                text,
                @"(?<!\d)([-+]?\d{1,2}(?:[.,]\d+)?)\s*[,;/\s]\s*([-+]?\d{1,3}(?:[.,]\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            foreach (Match match in matches)
            {
                if (!TryParseFlexibleDouble(match.Groups[1].Value, out var candidateLat) ||
                    !TryParseFlexibleDouble(match.Groups[2].Value, out var candidateLon))
                {
                    continue;
                }

                if (!IsValidLatLon(candidateLat, candidateLon))
                {
                    continue;
                }

                lat = candidateLat;
                lon = candidateLon;
                return true;
            }

            return false;
        }

        private static bool TryParseHemispherePair(string text, out double lat, out double lon)
        {
            lat = default;
            lon = default;
            var latFound = TryExtractHemisphereCoordinate(text, true, out lat);
            var lonFound = TryExtractHemisphereCoordinate(text, false, out lon);
            return latFound && lonFound && IsValidLatLon(lat, lon);
        }

        private static bool TryExtractHemisphereCoordinate(string text, bool isLatitude, out double value)
        {
            value = default;
            var hemiPattern = isLatitude ? "[NS]" : "[EW]";
            var degreePattern = isLatitude ? @"\d{1,2}" : @"\d{1,3}";

            var trailingHemisphere = Regex.Match(
                text,
                @"(?i)(?<deg>" + degreePattern + @")\s*(?:\u00B0|d|\u00BA)?\s*(?<min>\d{1,2})?\s*(?:'|m|min)?\s*(?<sec>\d{1,2}(?:[.,]\d+)?)?\s*(?:\""|s|sec)?\s*(?<hem>" + hemiPattern + @")");
            if (trailingHemisphere.Success &&
                TryConvertToDecimal(
                    trailingHemisphere.Groups["deg"].Value,
                    trailingHemisphere.Groups["min"].Value,
                    trailingHemisphere.Groups["sec"].Value,
                    trailingHemisphere.Groups["hem"].Value,
                    out value))
            {
                return true;
            }

            var leadingHemisphere = Regex.Match(
                text,
                @"(?i)(?<hem>" + hemiPattern + @")\s*(?<deg>" + degreePattern + @")\s*(?:\u00B0|d|\u00BA)?\s*(?<min>\d{1,2})?\s*(?:'|m|min)?\s*(?<sec>\d{1,2}(?:[.,]\d+)?)?\s*(?:\""|s|sec)?");
            if (leadingHemisphere.Success &&
                TryConvertToDecimal(
                    leadingHemisphere.Groups["deg"].Value,
                    leadingHemisphere.Groups["min"].Value,
                    leadingHemisphere.Groups["sec"].Value,
                    leadingHemisphere.Groups["hem"].Value,
                    out value))
            {
                return true;
            }

            return false;
        }

        private static bool TryConvertToDecimal(string degText, string minText, string secText, string hemText, out double value)
        {
            value = default;
            if (!TryParseFlexibleDouble(degText, out var degree))
            {
                return false;
            }

            var minute = 0d;
            var second = 0d;
            if (!string.IsNullOrWhiteSpace(minText) && !TryParseFlexibleDouble(minText, out minute))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(secText) && !TryParseFlexibleDouble(secText, out second))
            {
                return false;
            }

            var decimalValue = Math.Abs(degree) + (Math.Abs(minute) / 60d) + (Math.Abs(second) / 3600d);
            var hemisphere = hemText?.Trim().ToUpperInvariant() ?? string.Empty;
            if (hemisphere == "S" || hemisphere == "W")
            {
                decimalValue *= -1d;
            }

            value = decimalValue;
            return true;
        }

        private static bool TryParseFlexibleDouble(string text, out double value)
        {
            var normalized = (text ?? string.Empty).Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsValidLatLon(double lat, double lon)
        {
            return lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
        }

        private static string ToDmsSingle(double value, bool isLatitude)
        {
            var hemisphere = isLatitude
                ? (value >= 0 ? "N" : "S")
                : (value >= 0 ? "E" : "W");

            var abs = Math.Abs(value);
            var degrees = (int)Math.Floor(abs);
            var minutesTotal = (abs - degrees) * 60d;
            var minutes = (int)Math.Floor(minutesTotal);
            var seconds = (minutesTotal - minutes) * 60d;

            return degrees.ToString(CultureInfo.InvariantCulture) +
                   "\u00B0" +
                   minutes.ToString("00", CultureInfo.InvariantCulture) +
                   "'" +
                   seconds.ToString("00.0", CultureInfo.InvariantCulture) +
                   "\"" +
                   hemisphere;
        }
    }
}