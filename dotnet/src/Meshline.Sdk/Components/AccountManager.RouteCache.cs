using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using System.Runtime.ExceptionServices;

namespace Meshline.Components;

sealed partial class AccountManager
{
    static readonly TimeSpan RouteFreshness = TimeSpan.FromHours(1);
    static readonly TimeSpan RefreshFailureDelay = TimeSpan.FromMinutes(1);
    readonly Lock _routeCacheGate = new();
    readonly Dictionary<string, RouteEntry> _routeCache = new(StringComparer.Ordinal);
    CancellationTokenSource _routeRefreshLifetime = new();
    Task? _stoppingRefreshes;
    bool _routeCacheDisposed;

    sealed class RouteEntry
    {
        public AccountRoute? Route;
        public DateTimeOffset RefreshAfter;
        public DateTimeOffset RetryAfter;
        public long Publication;
        public Task<RouteRefreshResult>? Refresh;
    }

    // A background failure is observed here even if every caller cancels its wait.
    sealed record RouteRefreshResult(AccountRoute? Route, ExceptionDispatchInfo? Failure = null);

    RouteEntry GetRouteEntry(string accountId)
    {
        if (!_routeCache.TryGetValue(accountId, out var entry))
            _routeCache.Add(accountId, entry = new());
        return entry;
    }

    async Task<AccountRoute?> ReadRouteAsync(string accountId, bool forceRefresh, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (AccountAdapter.ValidateAccountId(accountId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(accountId));
        Task<RouteRefreshResult> refresh;
        lock (_routeCacheGate)
        {
            var entry = GetRouteEntry(accountId);
            var now = Clock.UtcNow;
            if (!forceRefresh && entry.Route is { } cached && cached.ExpiresAt > now.ToUnixTimeSeconds())
            {
                if (now >= entry.RefreshAfter && now >= entry.RetryAfter && _stoppingRefreshes is null)
                    StartRouteRefresh(accountId, entry);
                return cached;
            }

            if (!forceRefresh && now < entry.RetryAfter && entry.Refresh is { } failed)
                refresh = failed;
            else
                refresh = StartRouteRefresh(accountId, entry);
        }
        var result = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        result.Failure?.Throw();
        // A route can expire while a caller is waiting to resume, even after verification.
        return result.Route is { } route && route.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds() ? route : null;
    }

    // Called under _routeCacheGate. Only the task registration is synchronous.
    Task<RouteRefreshResult> StartRouteRefresh(string accountId, RouteEntry entry)
    {
        if (entry.Refresh is { IsCompleted: false } pending)
            return pending;
        if (_stoppingRefreshes is not null)
            throw new OperationCanceledException("Account route refreshes are stopping.");
        var token = _routeRefreshLifetime.Token;
        var operation = BeginOperation(ref token);
        return entry.Refresh = RefreshRouteCoreAsync(accountId, entry, entry.Publication, operation, token);
    }

    async Task<RouteRefreshResult> RefreshRouteCoreAsync(string accountId, RouteEntry entry, long publication, IDisposable operation, CancellationToken cancellationToken)
    {
        using (operation)
        {
            await Task.Yield();
            try
            {
                var route = await ResolveRouteAsync(accountId, cancellationToken).ConfigureAwait(false);
                bool changed = false;
                await _routeSaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Publication may finish while discovery is in flight. Never let that older
                    // lookup replace, invalidate, or change the freshness of a newer publication.
                    lock (_routeCacheGate)
                    {
                        if (entry.Publication != publication && entry.Route is { } published
                            && (route is null || route.CompareWith(published) is RouteComparison.Older or RouteComparison.Equivalent))
                            return new(published);
                    }
                    if (route is not null)
                        await SaveRouteAsync(route, cancellationToken).ConfigureAwait(false);
                    lock (_routeCacheGate)
                    {
                        if (route is not null)
                            changed = CacheRoute(entry, route);
                        else
                        {
                            // Discovery returning not_found is not signed revocation evidence.
                            entry.RetryAfter = Clock.UtcNow + RefreshFailureDelay;
                            if (accountId == AccountId && (entry.Route is null || entry.Route.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()))
                            {
                                changed = State != AccountState.Unknown;
                                State = AccountState.Unknown;
                            }
                        }
                    }
                }
                finally { _routeSaveGate.Release(); }
                if (changed)
                    AccountChanged?.Invoke(this, EventArgs.Empty);
                return new(route);
            }
            catch (Exception exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    lock (_routeCacheGate)
                    {
                        if (entry.Publication == publication)
                            entry.RetryAfter = Clock.UtcNow + RefreshFailureDelay;
                    }
                    ReportBackgroundError(BackgroundOperation.Connect, accountId, exception);
                }
                return new(null, ExceptionDispatchInfo.Capture(exception));
            }
        }
    }

    // Called under the local commit gate and cache lock, after verification and persistence.
    bool CacheRoute(RouteEntry entry, AccountRoute route)
    {
        entry.Route = route;
        entry.RefreshAfter = Clock.UtcNow + RouteFreshness;
        entry.RetryAfter = default;
        if (route.Account != AccountId)
            return false;
        var changed = Route is null || route.CompareWith(Route) != RouteComparison.Equivalent || State != AccountState.Established;
        Route = route;
        State = AccountState.Established;
        return changed;
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // On-demand lookups also work before StartAsync; stopping must drain those too.
        await StopRouteRefreshesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override Task OnStopAsync() => StopRouteRefreshesAsync();

    Task StopRouteRefreshesAsync()
    {
        lock (_routeCacheGate)
            return _routeCacheDisposed ? Task.CompletedTask : _stoppingRefreshes ??= DrainRouteRefreshesAsync();
    }

    async Task DrainRouteRefreshesAsync()
    {
        await Task.Yield();
        Task<RouteRefreshResult>[] pending;
        lock (_routeCacheGate)
            pending = [.. _routeCache.Values.Select(entry => entry.Refresh).OfType<Task<RouteRefreshResult>>()];
        try
        {
            // A throwing cancellation callback must not skip draining the requests it canceled.
            await Task.WhenAll(_routeRefreshLifetime.CancelAsync(), Task.WhenAll(pending)).ConfigureAwait(false);
        }
        finally
        {
            lock (_routeCacheGate)
            {
                _routeRefreshLifetime.Dispose();
                _routeRefreshLifetime = new();
                _stoppingRefreshes = null;
            }
        }
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        Task stopping;
        lock (_routeCacheGate)
        {
            stopping = StopRouteRefreshesAsync();
            _routeCacheDisposed = true;
        }
        await stopping.ConfigureAwait(false);
        lock (_routeCacheGate)
        {
            _routeCache.Clear();
            _routeRefreshLifetime.Dispose();
        }
        _publicationGate.Dispose();
        _routeSaveGate.Dispose();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
