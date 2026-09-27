namespace Meshline.Tests.Support;

internal static class AsyncTest
{
    internal static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    internal static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await predicate())
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
