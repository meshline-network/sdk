using System.Collections.Concurrent;
using System.Diagnostics;

namespace Meshline.Tests.Support;

// Observe per-fixture database boundaries without exposing private SDK helpers.
internal sealed class DatabaseObserver : IDisposable
{
    readonly ConcurrentBag<IDisposable> subscriptions = [];
    readonly IDisposable all;

    public DatabaseObserver(Action<string, object?> callback)
    {
        all = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(listener =>
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
                subscriptions.Add(listener.Subscribe(new Observer<KeyValuePair<string, object?>>(pair =>
                {
                    callback(pair.Key, pair.Value);
                })));
        }));
    }

    public void Dispose()
    {
        all.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    sealed class Observer<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
