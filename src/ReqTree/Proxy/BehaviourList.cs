namespace ReqTree.Proxy;

/// <summary>
/// An ordered, name-keyed collection of things the proxy runs on every exchange.
/// </summary>
/// <remarks>
/// Three requirements, and together they rule out the obvious containers.
///
/// **Order is part of the contract.** Rules and scripts run in the order they were added, so the
/// collection has to keep that. A HashSet does not: it reuses the slot freed by a removal, so
/// removing one rule and adding another silently moves the new one into the old one's position.
/// A Dictionary is no better for the same reason.
///
/// **The read path is the hot one.** Every request enumerates the whole thing, on proxy threads,
/// while adds and removes happen a handful of times a session from MCP tools. So writes copy the
/// whole array under a lock and swap it in, and readers take the array as it was when they started
/// and walk it with no lock and no allocation at all.
///
/// **Names are the identity.** Adding something whose name is already present replaces it in
/// place, keeping its position, rather than appending a second one with the same name.
/// </remarks>
internal sealed class BehaviourList<T>(Func<T, string> nameOf)
{
    private readonly Lock _sync = new();

    /// <summary>Volatile so a swap on one thread is visible to proxy threads already running.</summary>
    private volatile T[] _items = [];

    /// <summary>
    /// The current contents. Safe to enumerate without a lock: a write replaces this reference
    /// rather than altering the array, so an enumeration in progress finishes over what it started
    /// with instead of seeing a half-applied change.
    /// </summary>
    public T[] Items => _items;

    public int Count => _items.Length;

    /// <summary>Adds an item, or replaces the one with the same name, keeping its position.</summary>
    /// <returns>True when it replaced one, false when it was new.</returns>
    public bool AddOrReplace(T item)
    {
        lock (_sync)
        {
            var existing = IndexOf(nameOf(item));

            if (existing < 0)
            {
                _items = [.. _items, item];
                return false;
            }

            var replaced = (T[])_items.Clone();
            replaced[existing] = item;
            _items = replaced;
            return true;
        }
    }

    /// <summary>Removes the item with this name.</summary>
    /// <returns>True when something was removed.</returns>
    public bool Remove(string name)
    {
        lock (_sync)
        {
            if (IndexOf(name) < 0) return false;

            _items = Array.FindAll(_items, held =>
                !nameOf(held).Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

            return true;
        }
    }

    /// <summary>How many items satisfy a predicate. Used for "how many run at this hook".</summary>
    public int CountWhere(Func<T, bool> predicate) => _items.Count(predicate);

    /// <summary>
    /// Only ever called with the lock held. Names are compared trimmed as well as case-insensitively,
    /// so a rule added as " auth " is still found by "auth" — an LLM that pads a name once and not
    /// the next time would otherwise be unable to remove what it just created.
    /// </summary>
    private int IndexOf(string name) =>
        Array.FindIndex(_items, held =>
            nameOf(held).Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}
