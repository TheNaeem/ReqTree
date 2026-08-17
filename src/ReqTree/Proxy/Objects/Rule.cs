namespace ReqTree.Proxy.Objects;

/// <summary>
/// A condition and what to do when it holds. Rules run on every request, before scripts.
/// </summary>
/// <remarks>
/// Both halves are delegates rather than data, so a rule can express anything C# can. What arrives
/// over MCP is a description — "if the url contains x, block it" — and the tool that receives it
/// builds the pair.
///
/// The action is handed the <see cref="Exchange"/> and changes it. The proxy writes those changes
/// back onto the live request afterwards, so editing the exchange is how a rule affects real
/// traffic: set the response half and the request is answered without ever going upstream, change
/// the url and it goes somewhere else.
/// </remarks>
public sealed class Rule
{
    /// <summary>What this rule is called. Used in every log line it produces.</summary>
    public required string Name { get; init; }

    /// <summary>True when this rule applies to the exchange.</summary>
    public required Func<Exchange, bool> Condition { get; init; }

    /// <summary>What to do to an exchange the condition matched.</summary>
    public required Action<Exchange> Action { get; init; }

    /// <summary>
    /// Which session added it. Several LLMs share one ReqTree, so a rule nobody can attribute is a
    /// rule nobody can safely remove.
    /// </summary>
    public string? AddedBy { get; init; }

    /// <summary>A short account of what the rule does, for sessions that did not create it.</summary>
    public string? Description { get; init; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset AddedAt { get; } = DateTimeOffset.Now;

    /// <summary>
    /// Whether it is currently evaluated at all. Volatile for the same reason
    /// <c>CaptureProxy.CaptureEnabled</c> is: read on every request from proxy threads, written
    /// from an MCP tool on another one.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private volatile bool _enabled = true;

    /// <summary>
    /// How many exchanges this rule has matched. Incremented from many proxy threads at once, so
    /// <see cref="Interlocked"/> rather than a plain increment, which would silently undercount.
    /// </summary>
    public long HitCount => Interlocked.Read(ref _hitCount);

    private long _hitCount;

    internal void RecordHit() => Interlocked.Increment(ref _hitCount);
}
