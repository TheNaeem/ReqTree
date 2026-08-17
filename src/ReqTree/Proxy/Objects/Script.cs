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
/// traffic: the proxy catches whatever it throws, logs it, and carries on with the next one.
/// </remarks>
public sealed class Script
{
    /// <summary>What this script is called. Used in every log line it produces.</summary>
    public required string Name { get; init; }

    /// <summary>Which hook it runs at.</summary>
    public required ProxyHook Hook { get; init; }

    /// <summary>The compiled body.</summary>
    public required Action<Exchange> Run { get; init; }

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

    private long _runCount;
    private long _errorCount;

    internal void RecordRun() => Interlocked.Increment(ref _runCount);
    internal void RecordError() => Interlocked.Increment(ref _errorCount);
}
