using ReqTree.Proxy.Objects;

namespace ReqTree.Proxy;

/// <summary>
/// Every captured exchange. This is the thing that gets passed around and queried, whether the
/// traffic arrived seconds ago through the proxy or was read back out of a saved file.
/// </summary>
/// <remarks>
/// One store for both cases, because nothing about holding, adding or searching an exchange changes
/// once it is no longer arriving. The only real difference is where the ids come from, and
/// <see cref="AddExchange"/> handles that in a line: an exchange with an id keeps it, one without
/// gets the next.
///
/// Writes take a short lock; reads take a snapshot and then work outside it. Exchanges arrive on
/// proxy threads and must not be made to wait behind a slow regex over ten thousand bodies.
/// </remarks>
public sealed class ExchangeStore
{
    /// <summary>Guards everything below. Held only for the length of a write or a copy.</summary>
    private readonly Lock _sync = new();

    /// <summary>
    /// One held exchange, and the size we last charged it.
    /// </summary>
    /// <remarks>
    /// The size has to be remembered rather than recomputed. The response half is filled in on the
    /// *same object* that was stored, so by the time the second AddExchange arrives, asking the
    /// stored exchange how big it is gives the new answer — subtracting that and adding it back
    /// nets to nothing and the response body is never charged at all. The byte cap then only ever
    /// counted request bodies, which is a small fraction of the memory.
    /// </remarks>
    private sealed class Entry
    {
        public required Exchange Exchange { get; set; }
        public required long AccountedBytes { get; set; }
    }

    /// <summary>
    /// Age order, oldest first. A linked list rather than a List because with caps in force the
    /// oldest is dropped on nearly every request, and removing the front of a List of five thousand
    /// shifts five thousand references every time.
    /// </summary>
    private readonly LinkedList<Entry> _order = new();

    /// <summary>
    /// Id lookup. Holds the node rather than the exchange, so filling in a response is a pointer
    /// write instead of a scan for the right position — and that path runs on every response.
    /// </summary>
    private readonly Dictionary<long, LinkedListNode<Entry>> _byId = [];

    private long _nextId;

    /// <summary>
    /// The highest id ever dropped to stay within the caps.
    /// </summary>
    /// <remarks>
    /// This is how a response arriving for an already-dropped exchange is told apart from one
    /// simply arriving out of order. Dropping is always oldest-first, so everything discarded is
    /// below this line and a single watermark is enough.
    ///
    /// The obvious shortcut — "any id at or below the highest we have seen must have been dropped"
    /// — is wrong and was a real bug. The proxy numbers exchanges when it first sees them, but
    /// calls AddExchange later, so under concurrency a lower id routinely arrives after a higher
    /// one. That rule silently refused around one request in fifteen under load.
    /// </remarks>
    private long _droppedWatermark;

    private long _approximateBytes;
    private long _totalSeen;
    private long _dropped;
    private long _cleared;
    private bool _stopped;

    /// <summary>Most exchanges held before the oldest are dropped. Zero means no limit.</summary>
    public int Capacity { get; }

    /// <summary>Approximate ceiling on captured bodies, in bytes. Zero means no limit.</summary>
    public long MaxBytes { get; }

    /// <summary>Stop recording once this many have been recorded. Zero means no limit.</summary>
    public long StopAfter { get; }

    /// <summary>Raised once, when a limit stops recording. The proxy turns capture off.</summary>
    public event Action<string>? LimitReached;

    /// <param name="capacity">Most exchanges to hold. Zero, the default, means unbounded.</param>
    /// <param name="maxBytes">Approximate body-memory ceiling. Zero means unbounded.</param>
    /// <param name="stopAfter">Stop recording after this many. Zero means never.</param>
    /// <remarks>
    /// Unbounded by default on purpose. A capture read back from a file has to hold everything that
    /// was in it, and a default capacity here would silently drop the oldest half of a large saved
    /// capture as it loaded — while the file on disk still said it held them.
    /// </remarks>
    public ExchangeStore(int capacity = 0, long maxBytes = 0, long stopAfter = 0)
    {
        Capacity = Math.Max(capacity, 0);
        MaxBytes = Math.Max(maxBytes, 0);
        StopAfter = Math.Max(stopAfter, 0);
    }

    /// <summary>How many exchanges are held right now.</summary>
    public int Count { get { lock (_sync) return _order.Count; } }

    /// <summary>Approximate bytes of body currently held.</summary>
    public long ApproximateBytes { get { lock (_sync) return _approximateBytes; } }

    /// <summary>How many have ever been recorded here, including any since dropped.</summary>
    public long TotalSeen { get { lock (_sync) return _totalSeen; } }

    /// <summary>How many have been dropped to stay within the caps.</summary>
    public long Dropped { get { lock (_sync) return _dropped; } }

    /// <summary>
    /// How many have been removed deliberately, by one of the clearing methods.
    /// </summary>
    /// <remarks>
    /// Counted apart from <see cref="Dropped"/> so a reader can tell "the caps are biting" from
    /// "somebody threw these away". Without it, a store holding nothing after a clear would report
    /// hundreds recorded and none dropped, which reads like exchanges went missing on their own.
    /// </remarks>
    public long Cleared { get { lock (_sync) return _cleared; } }

    /// <summary>True once a limit has stopped recording.</summary>
    public bool StoppedByLimit { get { lock (_sync) return _stopped; } }

    /// <summary>
    /// Records an exchange, or updates one already held.
    /// </summary>
    /// <remarks>
    /// Called twice for a normal exchange: once when the request is seen, and again once the
    /// response has been filled in on the same object. The second call recognises it by its id and
    /// updates in place — without that check it would file the same request a second time.
    ///
    /// An exchange arriving with an id already set keeps it, which is what lets a capture read from
    /// a file mean the same ids it meant when it was written, and lets the proxy number exchanges
    /// it is not recording.
    ///
    /// Runs on proxy threads, so it does the minimum under the lock.
    /// </remarks>
    /// <returns>
    /// False when it was refused: either a limit has stopped recording, or this exchange had
    /// already been dropped to stay within the caps.
    /// </returns>
    public bool AddExchange(Exchange exchange)
    {
        lock (_sync)
        {
            if (exchange.Id != 0 && _byId.TryGetValue(exchange.Id, out var held))
            {
                // Already here. The response half usually arrives on the same object, but this
                // stays correct if a caller hands back a different one carrying the same id.
                // The charge comes off at what it was recorded as, not at what the object now says.
                _approximateBytes -= held.Value.AccountedBytes;

                held.Value.Exchange = exchange;
                held.Value.AccountedBytes = BodyBytes(exchange);
                _approximateBytes += held.Value.AccountedBytes;

                // A response body is often what pushes the store over its ceiling, so the caps have
                // to be applied on update as well as on insert.
                Trim();
                return true;
            }

            if (_stopped) return false;

            if (exchange.Id == 0)
            {
                exchange.Id = ++_nextId;
            }
            else if (exchange.Id <= _droppedWatermark)
            {
                // Below everything we have discarded, so this exchange was dropped to stay within
                // the caps and its response is arriving afterwards. Refused rather than
                // re-inserted: putting it back would place an old exchange at the young end of the
                // age order and count it as a second arrival.
                return false;
            }
            else
            {
                // Not one of ours: from a file, or numbered by the proxy. The counter moves past it
                // so nothing this store numbers later collides with it.
                _nextId = Math.Max(_nextId, exchange.Id);
            }

            var bytes = BodyBytes(exchange);
            _byId[exchange.Id] = _order.AddLast(new Entry { Exchange = exchange, AccountedBytes = bytes });
            _approximateBytes += bytes;
            _totalSeen++;

            Trim();
            CheckStopAfter();

            return true;
        }
    }

    /// <summary>
    /// Drops the oldest until the store is back inside its caps. Only ever called with the lock held.
    /// </summary>
    /// <remarks>
    /// Never drops the last exchange, however large. A single body bigger than the whole ceiling
    /// would otherwise empty the store to make room for something it then dropped as well, leaving
    /// a capture that is permanently empty for no visible reason.
    /// </remarks>
    private void Trim()
    {
        while (_order.Count > 1 && OverCap())
        {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            _byId.Remove(oldest.Exchange.Id);
            _approximateBytes -= oldest.AccountedBytes;
            _droppedWatermark = Math.Max(_droppedWatermark, oldest.Exchange.Id);
            _dropped++;
        }
    }

    private bool OverCap() =>
        (Capacity > 0 && _order.Count > Capacity) || (MaxBytes > 0 && _approximateBytes > MaxBytes);

    /// <summary>Stops recording once the total cap is reached. Only ever called with the lock held.</summary>
    private void CheckStopAfter()
    {
        if (StopAfter == 0 || _stopped || _totalSeen < StopAfter) return;

        _stopped = true;

        LimitReached?.Invoke(
            $"Capture limit reached: {_totalSeen} exchange(s) recorded, which was the --stop-after "
            + $"limit of {StopAfter}. Recording has stopped; traffic still flows normally.");
    }

    /// <summary>What an exchange costs us, near enough. Bodies dominate; the rest is noise.</summary>
    private static long BodyBytes(Exchange exchange) =>
        (exchange.RequestBody?.Length ?? 0) + (exchange.ResponseBody?.Length ?? 0);

    /// <summary>
    /// A copy of the exchanges as they stand, oldest first. Queries run over this rather than over
    /// the live list, so a long search never blocks traffic arriving.
    /// </summary>
    public IReadOnlyList<Exchange> Snapshot()
    {
        lock (_sync) return [.. _order.Select(entry => entry.Exchange)];
    }

    // -------------------------------------------------------------------------------------
    // Querying. Four ways in, deliberately: everything, a time window, the last few, and a
    // keyword search. Each is a few lines of LINQ over a snapshot, because the store is small
    // enough that anything cleverer would be complexity bought with nothing.
    //
    // These live here rather than on CaptureProxy so a capture opened from a file answers the
    // same questions a live one does, without a proxy having to exist.
    // -------------------------------------------------------------------------------------

    /// <summary>One exchange by id, or null.</summary>
    public Exchange? GetById(long id)
    {
        lock (_sync) return _byId.TryGetValue(id, out var node) ? node.Value.Exchange : null;
    }

    /// <summary>Exchanges that started within the last <paramref name="minutes"/>, oldest first.</summary>
    public IReadOnlyList<Exchange> Since(double minutes)
    {
        // Worked out once, from the caller's "how long ago", so every exchange is compared against
        // the same instant rather than a clock that moves as the list is walked.
        var cutoff = DateTimeOffset.Now.AddMinutes(-Math.Abs(minutes));
        return [.. Snapshot().Where(exchange => exchange.StartedAt >= cutoff)];
    }

    /// <summary>The most recent <paramref name="count"/> exchanges, still oldest first.</summary>
    /// <remarks>
    /// Ordered oldest first like everything else, so a reader can follow a flow forwards. Taking
    /// the last n and reversing them would put the newest at the top, which reads backwards for
    /// anything that is a sequence — and traffic almost always is.
    /// </remarks>
    public IReadOnlyList<Exchange> Recent(int count)
    {
        var all = Snapshot();
        return count >= all.Count ? all : [.. all.Skip(all.Count - Math.Max(count, 0))];
    }

    /// <summary>Which parts of an exchange a search looks at.</summary>
    [Flags]
    public enum SearchIn
    {
        Url = 1,
        RequestHeaders = 2,
        ResponseHeaders = 4,
        RequestBody = 8,
        ResponseBody = 16,
        All = Url | RequestHeaders | ResponseHeaders | RequestBody | ResponseBody,
    }

    /// <summary>Exchanges containing <paramref name="keyword"/> in the chosen places.</summary>
    public IReadOnlyList<Exchange> Search(string keyword, SearchIn where)
    {
        if (string.IsNullOrEmpty(keyword)) return [];

        return [.. Snapshot().Where(exchange => Matches(exchange, keyword, where))];
    }

    private static bool Matches(Exchange exchange, string keyword, SearchIn where)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;

        if (where.HasFlag(SearchIn.Url) && exchange.Url.Contains(keyword, ci))
            return true;

        // Name and value both, because a search for "authorization" should find the header and a
        // search for a token should find the one carrying it.
        if (where.HasFlag(SearchIn.RequestHeaders) && HeadersContain(exchange.RequestHeaders, keyword))
            return true;

        if (where.HasFlag(SearchIn.ResponseHeaders) && HeadersContain(exchange.ResponseHeaders, keyword))
            return true;

        // The text properties decode once and cache, so searching every body in the store does not
        // re-decode them on each query.
        if (where.HasFlag(SearchIn.RequestBody) && exchange.RequestBodyText.Contains(keyword, ci))
            return true;

        if (where.HasFlag(SearchIn.ResponseBody) && exchange.ResponseBodyText.Contains(keyword, ci))
            return true;

        return false;
    }

    private static bool HeadersContain(IReadOnlyList<(string Name, string Value)>? headers, string keyword)
    {
        if (headers is null) return false;

        foreach (var (name, value) in headers)
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    // -------------------------------------------------------------------------------------
    // Clearing. Deliberate removal, as against Trim's automatic dropping to stay inside the
    // caps — the same four ways in as the queries above, so anything that can be found can be
    // thrown away on the same terms.
    //
    // All of them advance the dropped watermark past whatever they removed. A response usually
    // arrives on the second AddExchange for an exchange whose request half is already stored;
    // remove that half and the response would no longer be recognised, and would be filed as a
    // brand new exchange holding a response and no request. The watermark is what refuses it.
    // -------------------------------------------------------------------------------------

    /// <summary>Removes everything held. Returns how many went.</summary>
    /// <remarks>
    /// <see cref="TotalSeen"/> and the <c>--stop-after</c> limit are deliberately left alone: they
    /// describe the session, not the contents, and a store stopped by its limit stays stopped.
    /// </remarks>
    public int Clear()
    {
        lock (_sync)
        {
            var removed = _order.Count;
            if (removed == 0) return 0;

            // Everything numbered so far, not merely everything held — after a full clear there is
            // nothing left that a late response could legitimately belong to.
            _droppedWatermark = Math.Max(_droppedWatermark, _nextId);

            _order.Clear();
            _byId.Clear();
            _approximateBytes = 0;
            _cleared += removed;

            return removed;
        }
    }

    /// <summary>Removes the <paramref name="count"/> oldest exchanges.</summary>
    public int RemoveOldest(int count) => RemoveFromOneEnd(count, oldest: true);

    /// <summary>Removes the <paramref name="count"/> newest exchanges.</summary>
    public int RemoveNewest(int count) => RemoveFromOneEnd(count, oldest: false);

    private int RemoveFromOneEnd(int count, bool oldest)
    {
        if (count <= 0) return 0;

        lock (_sync)
        {
            var removed = 0;

            while (removed < count && _order.Count > 0)
            {
                RemoveNode(oldest ? _order.First! : _order.Last!);
                removed++;
            }

            _cleared += removed;
            return removed;
        }
    }

    /// <summary>Removes every exchange <see cref="Search"/> would return for these arguments.</summary>
    public int RemoveMatching(string keyword, SearchIn where)
    {
        if (string.IsNullOrEmpty(keyword)) return 0;

        // Matched outside the lock, through Search, for the reason the class comment gives: deciding
        // whether a body contains a keyword can mean decoding every body held, and traffic arriving
        // must not queue behind that. Ids are then removed under the lock, and any that has gone in
        // the meantime is simply not found.
        return RemoveByIds([.. Search(keyword, where).Select(exchange => exchange.Id)]);
    }

    /// <summary>Removes exchanges that started more than <paramref name="minutes"/> ago.</summary>
    public int RemoveOlderThan(double minutes)
    {
        var cutoff = DateTimeOffset.Now.AddMinutes(-Math.Abs(minutes));
        return RemoveByIds([.. Snapshot().Where(e => e.StartedAt < cutoff).Select(e => e.Id)]);
    }

    private int RemoveByIds(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return 0;

        lock (_sync)
        {
            var removed = 0;

            foreach (var id in ids)
                if (_byId.TryGetValue(id, out var node))
                {
                    RemoveNode(node);
                    removed++;
                }

            _cleared += removed;
            return removed;
        }
    }

    /// <summary>
    /// Unlinks one entry and gives back what it was charged. Only ever called with the lock held,
    /// and never counts towards <see cref="Dropped"/> — every caller is a deliberate removal.
    /// </summary>
    private void RemoveNode(LinkedListNode<Entry> node)
    {
        var entry = node.Value;

        _order.Remove(node);
        _byId.Remove(entry.Exchange.Id);
        _approximateBytes -= entry.AccountedBytes;
        _droppedWatermark = Math.Max(_droppedWatermark, entry.Exchange.Id);
    }

    /// <summary>A summary of what is held, for the stats tool.</summary>
    public CaptureStats Stats()
    {
        var all = Snapshot();

        if (all.Count == 0)
            return new CaptureStats(0, 0, 0, 0, null, null, [], []);

        return new CaptureStats(
            Total: all.Count,
            WithResponse: all.Count(e => e.HasResponse),
            ApproximateBytes: all.Sum(e => (long)(e.RequestBody?.Length ?? 0) + (e.ResponseBody?.Length ?? 0)),
            MedianDurationMs: Median([.. all.Where(e => e.DurationMs is not null).Select(e => e.DurationMs!.Value)]),
            FirstAt: all[0].StartedAt,
            LastAt: all[^1].StartedAt,
            TopHosts: [.. all.GroupBy(e => e.Host)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .Select(g => (g.Key, g.Count()))],
            StatusCodes: [.. all.Where(e => e.StatusCode is not null)
                .GroupBy(e => e.StatusCode!.Value)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count()))]);
    }

    /// <summary>Median rather than mean: one 30-second timeout should not move the reported figure.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}

/// <summary>What a capture looks like in aggregate.</summary>
public sealed record CaptureStats(
    int Total,
    int WithResponse,
    long ApproximateBytes,
    double MedianDurationMs,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    IReadOnlyList<(string Host, int Count)> TopHosts,
    IReadOnlyList<(int Status, int Count)> StatusCodes);
