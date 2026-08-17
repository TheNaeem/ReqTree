namespace ReqTree.Proxy.Objects;

/// <summary>
/// A named set of scripts, switched on or off as one thing.
/// </summary>
/// <remarks>
/// Scripts only, deliberately. A script can do everything a rule can and more, so letting an
/// environment hold both would mean two collections, two orderings and two places to look for
/// anything — for no capability that is not already there. An environment is a name, a flag, and
/// a list of scripts.
///
/// Environment scripts run before standalone ones, so a set assembled for the work in hand gets
/// first say. Nothing is forbidden after it: a later script may still change what an environment
/// script decided, and gets a warning in the log saying so.
/// </remarks>
public sealed class Environment
{
    /// <summary>What this environment is called. Used to enable, disable and unload it.</summary>
    public required string Name { get; init; }

    /// <summary>Which session created it.</summary>
    public string? AddedBy { get; init; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset AddedAt { get; } = DateTimeOffset.Now;

    /// <summary>
    /// Whether its scripts run at all. Volatile: read on every request from proxy threads, written
    /// from an MCP tool on another one.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    private volatile bool _enabled = true;

    /// <summary>
    /// The scripts in it, in the order they run.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale on every change rather than mutated, so a request already walking the
    /// list finishes over what it started with instead of seeing a half-applied edit. Scripts are
    /// added a handful of times a session and iterated on every request, which is what makes
    /// copying the cheap side of the trade.
    /// </remarks>
    public IReadOnlyList<Script> Scripts => _scripts;

    private volatile Script[] _scripts = [];
    private readonly Lock _sync = new();

    /// <summary>Adds a script, or replaces the one already here under that name.</summary>
    /// <returns>True when it replaced one.</returns>
    public bool AddOrReplace(Script script)
    {
        lock (_sync)
        {
            var existing = Array.FindIndex(_scripts, s => Matches(s.Name, script.Name));

            if (existing < 0)
            {
                _scripts = [.. _scripts, script];
                return false;
            }

            // Kept in place rather than appended, so replacing a script does not quietly move it
            // to the end of the order.
            var replaced = (Script[])_scripts.Clone();
            replaced[existing] = script;
            _scripts = replaced;
            return true;
        }
    }

    /// <summary>Removes a script by name.</summary>
    public bool Remove(string name)
    {
        lock (_sync)
        {
            var remaining = Array.FindAll(_scripts, s => !Matches(s.Name, name));
            if (remaining.Length == _scripts.Length) return false;

            _scripts = remaining;
            return true;
        }
    }

    /// <summary>Names match trimmed and case-insensitively, as everywhere else in ReqTree.</summary>
    private static bool Matches(string a, string b) =>
        a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
}
