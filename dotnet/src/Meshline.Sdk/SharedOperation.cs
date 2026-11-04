using System.Runtime.ExceptionServices;

namespace Meshline;

// Caller cancellation only ends that caller's wait. The owner stops admission
// before draining, and retains completed operations until their failure is handled.
sealed class SharedOperation<T>(Task<T> work)
{
    readonly Lock gate = new();
    readonly Task<Result> completion = CaptureAsync(work);
    TaskCompletionSource? waitersDrained;
    int waiters;
    bool observed;
    bool reported;

    sealed record Result(T? Value, ExceptionDispatchInfo? Failure = null);

    public bool IsCompleted => completion.IsCompleted;

    public bool CanForget
    {
        get
        {
            lock (gate) return completion.IsCompletedSuccessfully
                && (observed || reported || completion.Result.Failure is not { } failure || !BackgroundFailure.IsRequired(failure.SourceException));
        }
    }

    public async Task<T> WaitAsync(CancellationToken cancellationToken)
    {
        lock (gate)
            if (waiters++ == 0) waitersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Failure is { } failure)
            {
                lock (gate) observed = true;
                failure.Throw();
            }
            return result.Value!;
        }
        finally
        {
            lock (gate)
                if (--waiters == 0) waitersDrained!.TrySetResult();
        }
    }

    public async Task DrainAsync()
    {
        var result = await completion.ConfigureAwait(false);
        Task pending;
        lock (gate) pending = waiters == 0 ? Task.CompletedTask : waitersDrained!.Task;
        await pending.ConfigureAwait(false);
        lock (gate)
        {
            if (observed || reported || result.Failure is not { } failure || !BackgroundFailure.IsRequired(failure.SourceException)) return;
            reported = true;
            failure.Throw();
        }
    }

    static async Task<Result> CaptureAsync(Task<T> work)
    {
        try { return new(await work.ConfigureAwait(false)); }
        catch (Exception error)
        {
            return new(default, ExceptionDispatchInfo.Capture(work.Exception is { InnerExceptions.Count: > 1 } aggregate ? aggregate : error));
        }
    }
}
