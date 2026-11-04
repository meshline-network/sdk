using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Meshline;

// Ordinary background work can fail after its runtime is canceled. Observer and
// uncancellable completion failures must still reach shutdown with their original type.
static class BackgroundFailure
{
    static readonly ConditionalWeakTable<Exception, object> required = new();

    public static void Preserve(Exception error) => required.GetValue(error, static _ => new());

    public static bool IsRequired(Exception error) => required.TryGetValue(error, out _)
        || (error is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(IsRequired)
            : error.InnerException is { } inner && IsRequired(inner));

    public static void ThrowIfCanceled(Exception error, CancellationToken token)
    {
        if (!token.IsCancellationRequested) return;
        if (IsRequired(error)) ExceptionDispatchInfo.Capture(error).Throw();
        token.ThrowIfCancellationRequested();
    }

    public static void Notify(Action notification)
    {
        try { notification(); }
        catch (Exception error) { Preserve(error); throw; }
    }

    public static async Task CompleteAsync(Task completion, Exception? priorFailure = null, CancellationToken operationToken = default)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (priorFailure is OperationCanceledException && operationToken.IsCancellationRequested && !IsRequired(priorFailure)) priorFailure = null;
            if (priorFailure is not null) ThrowIfAny([priorFailure, error]);
            Preserve(error);
            throw;
        }
    }

    public static async Task DrainAsync(params Task[] tasks)
    {
        List<Exception> failures = [];
        foreach (var task in tasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception error)
            {
                // Faulted tasks can hold multiple failures; canceled tasks retain
                // their original cancellation exception only when awaited.
                if (task.Exception is { } aggregate) failures.AddRange(aggregate.InnerExceptions);
                else failures.Add(error);
            }
        }
        ThrowIfAny(failures);
    }

    // Invoke cleanup steps in order, including after synchronous or asynchronous failures.
    public static async ValueTask CleanupAsync(params Func<ValueTask>[] steps)
    {
        List<Exception> failures = [];
        foreach (var step in steps)
        {
            try { await step().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        ThrowIfAny(failures);
    }

    public static void ThrowIfAny(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0) return;
        // The same failure can reach cleanup through both a worker and its
        // notification stream. Retain distinct failures without reporting it twice.
        HashSet<Exception> unique = new(ReferenceEqualityComparer.Instance);
        var repeated = false;
        void Collect(Exception error)
        {
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) Collect(inner);
            else if (!unique.Add(error)) repeated = true;
        }
        foreach (var failure in failures) Collect(failure);
        if (repeated) failures = unique.ToArray();
        var error = failures.Count == 1 ? failures[0] : new AggregateException(failures);
        Preserve(error);
        ExceptionDispatchInfo.Capture(error).Throw();
    }
}
