namespace Meshline;

// Only the owner of a deadline can identify its expiration. External cancellation
// (including a component lifetime) must retain its original meaning.
sealed class RequestDeadline : IDisposable
{
    readonly string operation;
    readonly TimeSpan duration;
    readonly CancellationToken external;
    readonly CancellationTokenSource deadline;
    readonly CancellationTokenSource linked;

    public RequestDeadline(string operation, TimeSpan duration, CancellationToken external, TimeProvider clock)
    {
        this.operation = operation;
        this.duration = duration;
        this.external = external;
        deadline = new(duration, clock);
        linked = CancellationTokenSource.CreateLinkedTokenSource(external, deadline.Token);
    }

    public CancellationToken Token => linked.Token;

    public Exception Classify(Exception error)
    {
        if (error is not OperationCanceledException || external.IsCancellationRequested || !deadline.IsCancellationRequested)
            return error;
        var timeout = new TimeoutException($"Request '{operation}' timed out after {duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} seconds.", error);
        timeout.Data["operation"] = operation;
        timeout.Data["timeoutSeconds"] = duration.TotalSeconds;
        return timeout;
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        try { return await action(Token).ConfigureAwait(false); }
        catch (OperationCanceledException error)
        {
            var classified = Classify(error);
            if (ReferenceEquals(classified, error)) throw;
            throw classified;
        }
    }

    public void Dispose()
    {
        linked.Dispose();
        deadline.Dispose();
    }
}
