namespace NOCREPORTGENERATOR.Services
{
    public static class PendingMapFocusService
    {
        private static PendingMapFocusRequest? _pending;

        public static void Set(double latitude, double longitude, string? ttIoh = null)
        {
            _pending = new PendingMapFocusRequest
            {
                Latitude = latitude,
                Longitude = longitude,
                TtIoh = ttIoh?.Trim() ?? string.Empty
            };
        }

        public static PendingMapFocusRequest? Consume()
        {
            var value = _pending;
            _pending = null;
            return value;
        }
    }

    public sealed class PendingMapFocusRequest
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string TtIoh { get; set; } = string.Empty;
    }
}
