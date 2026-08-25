namespace ReqTree.Proxy;

/// <summary>
/// Names the live capture and the captures opened from files.
/// </summary>
/// <remarks>
/// A capture name is part of the MCP contract: null, whitespace, and <c>live</c> mean the active
/// in-memory store. Keeping that rule beside the file-capture collection prevents a tool from
/// accepting a name that every later read silently resolves somewhere else.
/// </remarks>
internal sealed class CaptureCatalog
{
    private readonly ExchangeStore _live;
    private readonly Dictionary<string, ExchangeStore> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    internal CaptureCatalog(ExchangeStore live) => _live = live;

    internal IReadOnlyList<string> OpenedNames
    {
        get { lock (_lock) return [.. _opened.Keys]; }
    }

    internal bool AddOpened(string name, ExchangeStore store)
    {
        lock (_lock)
        {
            if (_opened.ContainsKey(name)) return false;
            _opened.Add(name, store);
            return true;
        }
    }

    internal bool Close(string name)
    {
        lock (_lock) return _opened.Remove(name);
    }

    internal ExchangeStore? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || IsLiveName(name)) return _live;

        lock (_lock) return _opened.GetValueOrDefault(name);
    }

    internal CaptureName ResolveOpenedName(string fullPath, string? requestedName)
    {
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : requestedName.Trim();

        if (IsLiveName(name)) return new CaptureName(null, CaptureNameProblem.LiveReserved);
        if (string.IsNullOrWhiteSpace(name)) return new CaptureName(null, CaptureNameProblem.Empty);

        return new CaptureName(name, CaptureNameProblem.None);
    }

    internal static bool IsLiveName(string name) =>
        name.Equals("live", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The result of deriving a usable name for a file-backed capture.</summary>
internal readonly record struct CaptureName(string? Value, CaptureNameProblem Problem);

internal enum CaptureNameProblem
{
    None,
    LiveReserved,
    Empty,
}
