using System;
using System.Threading;

namespace NOCREPORTGENERATOR.Services
{
    public static class ImportJobService
    {
        private static readonly object Sync = new();
        private static CancellationTokenSource? _cts;
        private static ImportJobState _state = ImportJobState.Inactive;

        public static ImportJobState CurrentState
        {
            get
            {
                lock (Sync)
                {
                    return _state;
                }
            }
        }

        public static event Action<ImportJobState>? StateChanged;

        public static CancellationToken Start(string message)
        {
            ImportJobState state;
            CancellationToken token;
            lock (Sync)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _state = new ImportJobState(true, true, 0, message, true);
                state = _state;
                token = _cts.Token;
            }

            RaiseChanged(state);
            return token;
        }

        public static void Report(double percent, string message)
        {
            ImportJobState state;
            lock (Sync)
            {
                var normalized = Math.Max(0, Math.Min(100, percent));
                _state = new ImportJobState(true, false, normalized, message, true);
                state = _state;
            }

            RaiseChanged(state);
        }

        public static void Complete(string message)
        {
            ImportJobState state;
            lock (Sync)
            {
                _cts?.Dispose();
                _cts = null;
                _state = new ImportJobState(false, false, 100, message, false);
                state = _state;
            }

            RaiseChanged(state);
        }

        public static void Cancel()
        {
            lock (Sync)
            {
                _cts?.Cancel();
            }
        }

        private static void RaiseChanged(ImportJobState state)
        {
            StateChanged?.Invoke(state);
        }
    }

    public readonly record struct ImportJobState(
        bool IsActive,
        bool IsIndeterminate,
        double Percent,
        string Message,
        bool CanCancel)
    {
        public static ImportJobState Inactive => new(false, false, 0, string.Empty, false);
    }
}
