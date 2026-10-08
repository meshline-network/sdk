using Meshline.Models.Protocol;
using System.Runtime.ExceptionServices;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    readonly Lock _ownStateQueryGate = new();
    readonly Dictionary<OwnStateQueryKey, OwnStateQuery> _ownStateQueries = [];

    // Component ownership supplies the network, Registry and account boundary. Do not
    // join a request made before a new certificate, known state or relay notification.
    readonly record struct OwnStateQueryKey(string RelayId, bool UseAccount, string? Certificate, long KnownRevision, long RequiredRevision);

    sealed class OwnStateQuery
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<OwnStateQueryResult> Task = null!;
        public int Waiters;
    }

    // Keep failures observed even when the last waiter cancels. Active waiters still
    // receive the original exception, including its stack; completed results are not cached.
    sealed record OwnStateQueryResult(AccountDeviceState? State, ExceptionDispatchInfo? Failure = null);

    async Task<AccountDeviceState?> ResolveSharedOwnStateAsync(string relayId, bool useAccount, CancellationToken cancellationToken)
    {
        OwnStateQueryKey key;
        OwnStateQuery query;
        lock (_ownStateQueryGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            key = new(relayId, useAccount, Local?.ToJson(), DeviceState?.Revision ?? -1,
                _requiredDeviceRevisions.TryGetValue(relayId, out var required) ? required : -1);
            if (!_ownStateQueries.TryGetValue(key, out query!))
            {
                query = new();
                var token = query.Cancellation.Token;
                IDisposable operation;
                try { operation = BeginOperation(ref token); }
                catch
                {
                    query.Cancellation.Dispose();
                    throw;
                }
                _ownStateQueries.Add(key, query);
                query.Task = RunOwnStateQueryAsync(key, query, operation, token);
            }
            query.Waiters++;
        }

        try
        {
            var result = await query.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            result.Failure?.Throw();
            return result.State;
        }
        finally
        {
            Task? canceled = null;
            lock (_ownStateQueryGate)
            {
                if (--query.Waiters == 0)
                {
                    RemoveOwnStateQuery(key, query);
                    canceled = query.Cancellation.CancelAsync();
                }
            }
            if (canceled is not null)
            {
                try
                {
                    await Task.WhenAll(canceled, query.Task).ConfigureAwait(false);
                }
                finally { query.Cancellation.Dispose(); }
            }
        }
    }

    async Task<OwnStateQueryResult> RunOwnStateQueryAsync(OwnStateQueryKey key, OwnStateQuery query, IDisposable operation, CancellationToken cancellationToken)
    {
        using (operation)
        {
            await Task.Yield();
            try
            {
                return new(await ResolveDeviceStateCoreAsync(Options.AccountId, null, key.RelayId, key.UseAccount, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                return new(null, ExceptionDispatchInfo.Capture(exception));
            }
            finally
            {
                lock (_ownStateQueryGate)
                    RemoveOwnStateQuery(key, query);
            }
        }
    }

    // Called under _ownStateQueryGate. An abandoned query may finish after its replacement.
    void RemoveOwnStateQuery(OwnStateQueryKey key, OwnStateQuery query)
    {
        if (_ownStateQueries.TryGetValue(key, out var current) && ReferenceEquals(current, query))
            _ownStateQueries.Remove(key);
    }
}
