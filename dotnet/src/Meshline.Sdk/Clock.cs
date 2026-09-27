namespace Meshline;

// An async execution scope, never a process-wide mutable clock. Background tasks
// inherit their parent's scope through ExecutionContext, including ConfigureAwait(false).
static class Clock
{
    static readonly AsyncLocal<TimeProvider?> Current = new();

    public static TimeProvider Provider => Current.Value ?? TimeProvider.System;
    public static DateTimeOffset UtcNow => Provider.GetUtcNow();

    public static IDisposable Use(TimeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new Scope(provider);
    }

    sealed class Scope : IDisposable
    {
        readonly TimeProvider? previous = Current.Value;
        bool disposed;
        public Scope(TimeProvider provider) => Current.Value = provider;
        public void Dispose()
        {
            if (disposed) return;
            Current.Value = previous;
            disposed = true;
        }
    }
}
