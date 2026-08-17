using ModelContextProtocol.Server;

namespace ReqTree.Mcp;

/// <summary>
/// Works out who is calling a tool, so every change to shared state can be attributed.
/// </summary>
/// <remarks>
/// Several LLM sessions can be connected to one ReqTree at once, and they change things the others
/// are relying on. A rule that appeared from nowhere is a rule nobody can safely remove, so every
/// tool takes an optional name and everything it logs carries it.
///
/// The fallback is why <c>McpEndpoint</c> keeps sessions stateful: with the SDK's default stateless
/// mode there is a fresh server per HTTP request and <c>ClientInfo</c> is always null, so this
/// would quietly degrade to "unidentified" for every caller.
/// </remarks>
internal static class Actor
{
    /// <summary>Shared wording for the parameter, so every tool describes it the same way.</summary>
    internal const string Description =
        "Who you are: a short stable name for this session, e.g. 'auth-flow-investigation'. "
        + "Recorded in the log against everything you change, so other sessions sharing this "
        + "ReqTree can see who did what. Optional - if you omit it the name your MCP client "
        + "announced itself with is used, but that identifies the tool, not what you are doing.";

    /// <summary>The given name, or the connected client's name, or "unidentified".</summary>
    internal static string Resolve(string? name, McpServer? server)
    {
        if (!string.IsNullOrWhiteSpace(name)
            && !name.Trim().Equals("unidentified", StringComparison.OrdinalIgnoreCase))
            return Clean(name);

        var client = server?.ClientInfo?.Name;
        return string.IsNullOrWhiteSpace(client) ? "unidentified" : Clean(client);
    }

    /// <summary>
    /// Trims, flattens and caps a name before it reaches the log.
    /// </summary>
    /// <remarks>
    /// This is the coordination story's integrity, not tidiness. Actor names come from an LLM and
    /// are written into a log that other sessions read back through get_logs to find out what has
    /// been done to shared state. A name carrying a newline would end one line early and start
    /// another that looks exactly like a real entry — so one session could forge history attributed
    /// to another. Control characters become spaces, and the length is capped so a name cannot bury
    /// the rest of the line either.
    /// </remarks>
    internal static string Clean(string value)
    {
        var flattened = new string([.. value.Trim().Select(c => char.IsControl(c) ? ' ' : c)]).Trim();

        if (flattened.Length == 0) return "unidentified";

        return flattened.Length <= MaxNameLength ? flattened : flattened[..MaxNameLength] + "...";
    }

    private const int MaxNameLength = 80;
}
