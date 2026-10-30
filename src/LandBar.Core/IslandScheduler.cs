namespace LandBar.Core;

/// <summary>
/// Bounded, deterministic notification carousel. Call from one UI thread.
/// Display time advances only through Tick; hovering therefore pauses both expiry and rotation.
/// </summary>
public sealed class IslandScheduler
{
    private sealed class Entry(IslandEvent value)
    {
        public IslandEvent Value { get; set; } = value;
        public TimeSpan Exposure { get; set; }
    }

    private readonly List<Entry> _visible = [];
    private readonly LinkedList<Entry> _waiting = new();
    private readonly List<IslandEvent> _history = [];
    private readonly HashSet<string> _seenNotificationIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private TimeSpan _sinceRotation;
    private int _currentPage = 1;

    public IslandScheduler(
        int maxVisible = 2,
        int capacity = 30,
        TimeSpan? displayDuration = null,
        TimeSpan? rotationInterval = null)
    {
        if (maxVisible is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(maxVisible), "最多并列显示两条事件。");
        if (capacity < maxVisible)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        MaxVisible = maxVisible;
        Capacity = capacity;
        DisplayDuration = displayDuration ?? TimeSpan.FromSeconds(8);
        RotationInterval = rotationInterval ?? TimeSpan.FromSeconds(4);
        if (DisplayDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(displayDuration));
        if (RotationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rotationInterval));
    }

    public int MaxVisible { get; }
    public int Capacity { get; }
    public TimeSpan DisplayDuration { get; }
    public TimeSpan RotationInterval { get; }
    /// <summary>When disabled, cards retain their display budget while rotation continues.</summary>
    public bool ExpireVisibleItems { get; set; } = true;
    public bool IsPaused { get; private set; }
    public int TotalCount => _visible.Count + _waiting.Count;
    public int PendingCount => _waiting.Count;
    public int PageCount => (TotalCount + MaxVisible - 1) / MaxVisible;
    public int CurrentPage => TotalCount == 0 ? 0 : Math.Min(_currentPage, PageCount);
    public int DroppedCount { get; private set; }
    public long Revision { get; private set; }
    public IReadOnlyList<IslandEvent> VisibleItems => _visible.Select(x => x.Value).ToArray();
    public IReadOnlyList<IslandEvent> History => _history.ToArray();

    /// <summary>
    /// Accepts a new event. Duplicate notification IDs are ignored using a bounded 256-ID cache.
    /// A new song replaces the current event for the same media app, wherever it is queued.
    /// </summary>
    public bool Push(IslandEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Id);

        if (value.Kind == IslandEventKind.Notification && !_seenNotificationIds.Add(value.Id))
            return false;

        if (value.Kind == IslandEventKind.Media)
        {
            var media = _visible.Concat(_waiting).FirstOrDefault(entry =>
                entry.Value.Kind == IslandEventKind.Media &&
                string.Equals(MediaKey(entry.Value), MediaKey(value), StringComparison.OrdinalIgnoreCase));
            if (media is not null)
            {
                if (media.Value.Id == value.Id && media.Value.Title == value.Title &&
                    media.Value.Body == value.Body && media.Value.ArtworkPath == value.ArtworkPath)
                    return false;

                media.Value = value;
                media.Exposure = TimeSpan.Zero;
                Remember(value);
                Revision++;
                return true;
            }
        }

        if (value.Kind == IslandEventKind.Notification)
        {
            _seenOrder.Enqueue(value.Id);
            while (_seenOrder.Count > 256)
                _seenNotificationIds.Remove(_seenOrder.Dequeue());
        }

        // Preserve current cards when overloaded; discard the oldest waiting event first.
        if (TotalCount == Capacity)
        {
            if (_waiting.First is not null)
                _waiting.RemoveFirst();
            else
                _visible.RemoveAt(0);
            DroppedCount++;
        }

        _waiting.AddLast(new Entry(value));
        FillVisible();
        Remember(value);
        Revision++;
        return true;
    }

    public void SetPaused(bool paused) => IsPaused = paused;

    /// <summary>
    /// Advances a monotonic elapsed duration. Queued cards never consume their display budget.
    /// Large ticks are split at rotation and expiry boundaries, preserving fairness after a UI stall.
    /// </summary>
    public void Tick(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (IsPaused)
            return;

        while (elapsed > TimeSpan.Zero && _visible.Count > 0)
        {
            var untilExpiry = ExpireVisibleItems
                ? _visible.Min(entry => DisplayDuration - entry.Exposure)
                : TimeSpan.MaxValue;
            var untilRotation = _waiting.Count > 0
                ? RotationInterval - _sinceRotation
                : TimeSpan.MaxValue;
            var step = Min(elapsed, untilExpiry, untilRotation);
            if (ExpireVisibleItems)
                foreach (var entry in _visible)
                    entry.Exposure += step;
            _sinceRotation += step;
            elapsed -= step;

            var expired = ExpireVisibleItems && _visible.RemoveAll(entry => entry.Exposure >= DisplayDuration) > 0;
            var rotate = _waiting.Count > 0 && _sinceRotation >= RotationInterval;
            if (rotate)
            {
                Rotate();
                Revision++;
            }
            else if (expired)
            {
                FillVisible();
                Revision++;
            }

            if (_waiting.Count == 0)
                _sinceRotation = TimeSpan.Zero;
            if (TotalCount == 0)
                _currentPage = 1;
        }
    }

    /// <summary>Explicit navigation also works while automatic rotation is paused.</summary>
    public void NextPage()
    {
        if (_waiting.Count == 0)
            return;
        Rotate();
        Revision++;
    }

    /// <summary>
    /// Moves backward by a visible group. This is the inverse of NextPage even for odd queue sizes.
    /// Exposure is retained and explicit navigation remains available while paused.
    /// </summary>
    public void PreviousPage()
    {
        if (_waiting.Count == 0)
            return;

        var previousPage = CurrentPage <= 1 ? PageCount : CurrentPage - 1;
        var ring = _visible.Concat(_waiting).ToArray();
        var split = ring.Length - MaxVisible;
        _visible.Clear();
        _waiting.Clear();
        for (var i = split; i < ring.Length; i++)
            _visible.Add(ring[i]);
        for (var i = 0; i < split; i++)
            _waiting.AddLast(ring[i]);

        _sinceRotation = TimeSpan.Zero;
        _currentPage = previousPage;
        Revision++;
    }

    public bool Dismiss(string id)
    {
        var visible = _visible.FindIndex(entry => entry.Value.Id == id);
        if (visible >= 0)
            _visible.RemoveAt(visible);
        else
        {
            var node = _waiting.First;
            while (node is not null && node.Value.Value.Id != id)
                node = node.Next;
            if (node is null)
                return false;
            _waiting.Remove(node);
        }
        FillVisible();
        _sinceRotation = TimeSpan.Zero;
        Revision++;
        return true;
    }

    /// <summary>Clears active cards. The duplicate cache remains so polling cannot resurrect them.</summary>
    public void Clear()
    {
        _visible.Clear();
        _waiting.Clear();
        _sinceRotation = TimeSpan.Zero;
        _currentPage = 1;
        Revision++;
    }

    public void ClearHistory() => _history.Clear();

    /// <summary>
    /// Clears notification cards and history while preserving media entries and their display budgets.
    /// Notification IDs remain deduplicated so a source poll cannot restore cleared messages.
    /// </summary>
    public void ClearNotifications()
    {
        bool activeChanged = _visible.RemoveAll(entry => entry.Value.Kind == IslandEventKind.Notification) > 0;
        var node = _waiting.First;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.Value.Kind == IslandEventKind.Notification)
            {
                _waiting.Remove(node);
                activeChanged = true;
            }
            node = next;
        }
        bool historyChanged = _history.RemoveAll(item => item.Kind == IslandEventKind.Notification) > 0;
        if (!activeChanged && !historyChanged) return;
        if (activeChanged)
        {
            _sinceRotation = TimeSpan.Zero;
            _currentPage = 1;
            FillVisible();
        }
        Revision++;
    }

    private void FillVisible()
    {
        while (_visible.Count < MaxVisible && _waiting.First is not null)
        {
            _visible.Add(_waiting.First.Value);
            _waiting.RemoveFirst();
        }
        _currentPage = Math.Max(1, Math.Min(_currentPage, PageCount));
    }

    private void Rotate()
    {
        foreach (var entry in _visible)
            _waiting.AddLast(entry);
        _visible.Clear();
        FillVisible();
        _sinceRotation = TimeSpan.Zero;
        _currentPage = _currentPage % Math.Max(1, PageCount) + 1;
    }

    private void Remember(IslandEvent value)
    {
        _history.Add(value);
        if (_history.Count > Capacity)
            _history.RemoveRange(0, _history.Count - Capacity);
    }

    private static string MediaKey(IslandEvent value) => value.AppId ?? value.AppName;
    private static TimeSpan Min(TimeSpan a, TimeSpan b, TimeSpan c) => a <= b && a <= c ? a : b <= c ? b : c;
}
