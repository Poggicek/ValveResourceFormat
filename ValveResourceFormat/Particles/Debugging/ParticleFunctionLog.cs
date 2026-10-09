using Microsoft.Extensions.Logging;

namespace ValveResourceFormat.Particles.Debugging
{
    /// <summary>
    /// Forwards one particle function's log to the system's logger and keeps its warnings, so the
    /// debugger can show which function a warning belongs to. Unlike the forwarded log it also keeps
    /// the warnings that <see cref="LoggerExtensions"/> already reported for another
    /// system, since every function is entitled to its own.
    /// </summary>
    internal sealed class ParticleFunctionLog(ILogger inner) : ILogger, IWarningRecorder
    {
        // A function warning every step about something new would otherwise grow without bound.
        private const int MaxMessages = 32;

        private readonly List<string> messages = [];

        // Warnings raised from per-step code repeat every step, so they are told apart before being
        // formatted rather than after.
        private readonly HashSet<int> recorded = [];
        private bool recordOnly;

        public IReadOnlyList<string> Messages => messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning && messages.Count < MaxMessages)
            {
                var message = formatter(state, exception);

                if (!messages.Contains(message))
                {
                    messages.Add(message);
                }
            }

            if (!recordOnly)
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }

        void IWarningRecorder.Record(string message, ReadOnlySpan<object?> args)
        {
            var key = new HashCode();
            key.Add(message);

            foreach (var arg in args)
            {
                key.Add(arg);
            }

            if (messages.Count >= MaxMessages || !recorded.Add(key.ToHashCode()))
            {
                return;
            }

            recordOnly = true;

            try
            {
#pragma warning disable CA2254 // The template is a constant at every call site
                this.LogWarning(message, args.ToArray());
#pragma warning restore CA2254
            }
            finally
            {
                recordOnly = false;
            }
        }
    }
}
