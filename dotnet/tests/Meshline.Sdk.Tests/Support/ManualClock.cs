namespace Meshline.Tests.Support;

// Timers use monotonic ticks; callbacks run outside the lock, as with system timers.
internal sealed class ManualClock(DateTimeOffset? initialTime = null) : TimeProvider
{
    readonly Lock gate = new();
    readonly List<Timer> timers = [];
    DateTimeOffset utc = initialTime ?? new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return utc;
    }

    public override long GetTimestamp()
    {
        lock (gate)
            return timestamp;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public bool HasTimerDueWithin(TimeSpan interval)
    {
        lock (gate)
            return timers.Any(t => t.Due <= timestamp + interval.Ticks);
    }

    public int ActiveTimers
    {
        get
        {
            lock (gate)
                return timers.Count(t => t.Due != long.MaxValue);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed.Ticks);
        long target;
        lock (gate)
            target = checked(timestamp + elapsed.Ticks);
        while (true)
        {
            Timer? timer;
            lock (gate)
            {
                timer = timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                var next = timer?.Due ?? target;
                utc += TimeSpan.FromTicks(next - timestamp);
                timestamp = next;
                if (timer is null)
                    return;
                timer.Due = timer.Period > 0 ? checked(timestamp + timer.Period) : long.MaxValue;
            }

            timer.Callback(timer.State);
        }
    }

    sealed class Timer(ManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; set; } = long.MaxValue;
        public long Period { get; private set; }

        bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed)
                    return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner.timestamp + dueTime.Ticks);
                Period = period.Ticks;
                if (!owner.timers.Contains(this))
                    owner.timers.Add(this);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
