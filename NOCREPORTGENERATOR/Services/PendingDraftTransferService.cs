using NOCREPORTGENERATOR.Models;

namespace NOCREPORTGENERATOR.Services
{
    public static class PendingDraftTransferService
    {
        private static readonly object Sync = new();
        private static LocalFormRecord? _pending;

        public static void SetPending(LocalFormRecord record)
        {
            lock (Sync)
            {
                _pending = record;
            }
        }

        public static LocalFormRecord? Consume()
        {
            lock (Sync)
            {
                var value = _pending;
                _pending = null;
                return value;
            }
        }
    }
}
