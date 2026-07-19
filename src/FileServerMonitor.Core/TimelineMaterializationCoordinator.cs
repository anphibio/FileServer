using System.Threading.Channels;

namespace FileServerMonitor.Core;

public sealed record TimelineMaterializationWindow(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc)
{
    public static TimelineMaterializationWindow? FromTimestamps(
        IEnumerable<DateTimeOffset> timestamps,
        TimeSpan padding)
    {
        if (padding < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(padding), "Timeline materialization padding cannot be negative.");
        }

        using var iterator = timestamps.GetEnumerator();
        if (!iterator.MoveNext())
        {
            return null;
        }

        var fromUtc = iterator.Current;
        var toUtc = iterator.Current;
        while (iterator.MoveNext())
        {
            if (iterator.Current < fromUtc)
            {
                fromUtc = iterator.Current;
            }

            if (iterator.Current > toUtc)
            {
                toUtc = iterator.Current;
            }
        }

        return new TimelineMaterializationWindow(fromUtc - padding, toUtc + padding);
    }

    public TimelineMaterializationWindow Merge(TimelineMaterializationWindow other)
    {
        return new TimelineMaterializationWindow(
            FromUtc <= other.FromUtc ? FromUtc : other.FromUtc,
            ToUtc >= other.ToUtc ? ToUtc : other.ToUtc);
    }

    public bool Overlaps(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        var effectiveFrom = fromUtc ?? DateTimeOffset.MinValue;
        var effectiveTo = toUtc ?? DateTimeOffset.MaxValue;
        return FromUtc <= effectiveTo && ToUtc >= effectiveFrom;
    }
}

public sealed record TimelineMaterializationJob(
    Guid Id,
    TimelineMaterializationWindow Window);

public sealed record TimelineMaterializationLease(
    Guid LeaseId,
    IReadOnlyList<Guid> JobIds,
    TimelineMaterializationWindow Window)
{
    public static TimelineMaterializationLease? Select(
        IEnumerable<TimelineMaterializationJob> pendingJobs,
        Guid? leaseId = null)
    {
        var ordered = pendingJobs
            .OrderBy(item => item.Window.FromUtc)
            .ThenBy(item => item.Window.ToUtc)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var selectedIds = new List<Guid> { ordered[0].Id };
        var merged = ordered[0].Window;
        for (var index = 1; index < ordered.Length; index++)
        {
            var candidate = ordered[index];
            if (candidate.Window.FromUtc > merged.ToUtc)
            {
                break;
            }

            if (!candidate.Window.Overlaps(merged.FromUtc, merged.ToUtc))
            {
                continue;
            }

            selectedIds.Add(candidate.Id);
            merged = merged.Merge(candidate.Window);
        }

        return new TimelineMaterializationLease(
            leaseId ?? Guid.NewGuid(),
            selectedIds,
            merged);
    }
}

public sealed class TimelineMaterializationCoordinator
{
    private readonly object _sync = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly List<TimelineMaterializationWindow> _pending = [];
    private TimelineMaterializationWindow? _active;

    public void Enqueue(TimelineMaterializationWindow window)
    {
        if (window.ToUtc < window.FromUtc)
        {
            throw new ArgumentException("Timeline materialization window cannot end before it starts.", nameof(window));
        }

        lock (_sync)
        {
            AddPendingWindow(window);
        }

        _wake.Writer.TryWrite(true);
    }

    public async ValueTask<TimelineMaterializationWindow> ClaimAsync(
        CancellationToken cancellationToken,
        TimeSpan debounce = default,
        TimeSpan maxDebounce = default)
    {
        while (await _wake.Reader.WaitToReadAsync(cancellationToken))
        {
            _wake.Reader.TryRead(out _);
            if (debounce > TimeSpan.Zero)
            {
                var deadline = maxDebounce > TimeSpan.Zero
                    ? DateTimeOffset.UtcNow.Add(maxDebounce)
                    : DateTimeOffset.MaxValue;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    var quietDelay = remaining < debounce ? remaining : debounce;
                    var delayTask = Task.Delay(quietDelay, cancellationToken);
                    var wakeTask = _wake.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(delayTask, wakeTask);
                    if (completed == delayTask)
                    {
                        break;
                    }

                    if (!await wakeTask)
                    {
                        break;
                    }

                    while (_wake.Reader.TryRead(out _))
                    {
                    }
                }
            }

            lock (_sync)
            {
                if (_active is not null || _pending.Count == 0)
                {
                    continue;
                }

                _active = _pending[0];
                _pending.RemoveAt(0);
                if (_pending.Count > 0)
                {
                    _wake.Writer.TryWrite(true);
                }

                return _active;
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public void Complete(TimelineMaterializationWindow window)
    {
        lock (_sync)
        {
            EnsureActive(window);
            _active = null;
        }
    }

    public void Retry(TimelineMaterializationWindow window)
    {
        lock (_sync)
        {
            EnsureActive(window);
            _active = null;
            AddPendingWindow(window);
        }

        _wake.Writer.TryWrite(true);
    }

    public bool IsDirty(DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null)
    {
        lock (_sync)
        {
            return _pending.Any(window => window.Overlaps(fromUtc, toUtc))
                || (_active?.Overlaps(fromUtc, toUtc) ?? false);
        }
    }

    private void AddPendingWindow(TimelineMaterializationWindow window)
    {
        var merged = window;
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            var current = _pending[index];
            if (!current.Overlaps(merged.FromUtc, merged.ToUtc))
            {
                continue;
            }

            merged = merged.Merge(current);
            _pending.RemoveAt(index);
        }

        _pending.Add(merged);
        _pending.Sort(static (left, right) => left.FromUtc.CompareTo(right.FromUtc));
    }

    private void EnsureActive(TimelineMaterializationWindow window)
    {
        if (_active != window)
        {
            throw new InvalidOperationException("Only the active timeline materialization window can be completed or retried.");
        }
    }
}
