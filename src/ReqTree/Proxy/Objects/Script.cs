namespace ReqTree.Proxy.Objects;

/// <summary>
/// Arbitrary code an LLM supplied, run against every exchange at one hook.
/// </summary>
/// <remarks>
/// A script is the escape hatch for what a rule cannot express: it has no condition, it just runs,
/// and it decides for itself whether to do anything. Scripts run in the order they were added, and
/// always after every rule — rules are the declarative path and should get first say.
///
/// Scripts are not sandboxed. What is guaranteed is only that a failing script cannot break
/// traffic: the proxy catches whatever it throws, logs it, and carries on with the next one — and
/// that one which never returns is abandoned rather than allowed to hold the request open.
/// </remarks>
public sealed class Script
{
    /// <summary>
    /// How long a script may run before the proxy stops waiting for it.
    /// </summary>
    /// <remarks>
    /// Five seconds is far longer than any reasonable script needs and short enough that a stuck one
    /// is noticed on the first request rather than the hundredth. It exists because a script is
    /// arbitrary code from an LLM, and <c>while (true)</c> is a plausible mistake: without a limit
    /// the request never completes, and the next one starts another copy.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What this script is called. Used in every log line it produces.</summary>
    public required string Name { get; init; }

    /// <summary>Which hook it runs at.</summary>
    public required ProxyHook Hook { get; init; }

    /// <summary>The compiled body.</summary>
    public required Action<Exchange> Run { get; init; }

    /// <summary>
    /// How long this script may run for. <see cref="TimeSpan.Zero"/> means no limit.
    /// </summary>
    /// <remarks>
    /// Overridable because a script that walks a large body legitimately takes longer than one that
    /// reads a header, and because someone who knows their script is safe should be able to skip the
    /// thread hop the timeout costs. Zero is the deliberate opt-out and runs inline, exactly as
    /// before this existed.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Which session added it. Several LLMs share one ReqTree, so a script nobody can attribute is
    /// a script nobody can safely remove.
    /// </summary>
    public string? AddedBy { get; init; }

    /// <summary>The source it was compiled from, so another session can read what it does.</summary>
    public string? Source { get; init; }

    /// <summary>When it was added.</summary>
    public DateTimeOffset AddedAt { get; } = DateTimeOffset.Now;

    /// <summary>
    /// Whether it is currently run at all. Volatile: read on every request from proxy threads,
    /// written from an MCP tool on another one.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private volatile bool _enabled = true;

    /// <summary>How many times it has run to completion.</summary>
    public long RunCount => Interlocked.Read(ref _runCount);

    /// <summary>How many times it has thrown. A script failing every time is worth noticing.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>
    /// How many times it has been abandoned for running too long.
    /// </summary>
    /// <remarks>
    /// Never more than one in practice: the first timeout disables the script, because .NET cannot
    /// stop code that will not stop itself. Leaving it enabled would start a fresh runaway thread on
    /// every subsequent request, and that — not the single stuck one — is what takes a machine down.
    /// </remarks>
    public long TimeoutCount => Interlocked.Read(ref _timeoutCount);

    private long _runCount;
    private long _errorCount;
    private long _timeoutCount;

    internal void RecordRun() => Interlocked.Increment(ref _runCount);
    internal void RecordError() => Interlocked.Increment(ref _errorCount);
    internal void RecordTimeout() => Interlocked.Increment(ref _timeoutCount);
}
