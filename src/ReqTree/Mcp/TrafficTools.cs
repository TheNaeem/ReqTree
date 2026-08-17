using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ReqTree.Persistence;
using ReqTree.Proxy;
using ReqTree.Proxy.Objects;
using Serilog;

namespace ReqTree.Mcp;

/// <summary>
/// Reading what was captured, saving it, and opening it again.
/// </summary>
/// <remarks>
/// Four ways to select exchanges and no more: everything, a time window, the last few, and a
/// keyword search. Anything narrower is the LLM's job — it can read a hundred summaries and decide
/// which three matter far better than a query language can.
///
/// Everything returns a compact one-line-per-exchange summary. Bodies are only ever returned by
/// <c>get_exchange_detail</c>, for one exchange at a time, because a capture of a few hundred
/// exchanges holds more body text than a context window can take.
/// </remarks>
[McpServerToolType]
public static class TrafficTools
{
    private const string CaptureDescription =
        "Which capture to read: omit or 'live' for what is being recorded now, or the name a "
        + "saved capture was opened under. Call list_captures to see what is open.";

    [McpServerTool(Name = "get_all_exchanges")]
    [Description(
        "Every exchange in the capture, oldest first, one summary line each. Start here when you "
        + "do not yet know what you are looking for. Use get_exchange_detail for the bodies and "
        + "headers of any that look interesting.")]
    public static string GetAllExchanges(
        CaptureProxy proxy,
        [Description(CaptureDescription)] string? capture = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);
        return Format(store.Snapshot(), $"all exchanges in {Name(capture)}");
    }

    [McpServerTool(Name = "get_exchanges_since")]
    [Description(
        "Exchanges from the last N minutes, oldest first. Use this when the user describes a "
        + "window in time - 'what I just did', 'the last few minutes' - rather than a count. "
        + "Fractions are fine: 0.5 is the last thirty seconds.")]
    public static string GetExchangesSince(
        CaptureProxy proxy,
        [Description("How many minutes back to look, counting from now.")] double minutes,
        [Description(CaptureDescription)] string? capture = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (minutes <= 0)
            return "minutes must be greater than zero - it is how far back to look from now.";

        var found = store.Since(minutes);

        // Said plainly, because "nothing in the last 3 minutes" and "nothing captured at all" call
        // for completely different next moves.
        if (found.Count == 0 && store.Count > 0)
            return $"No exchanges in the last {minutes} minute(s), though {store.Count} were "
                 + "captured earlier. Try a longer window, or get_all_exchanges.";

        return Format(found, $"exchanges from the last {minutes} minute(s)");
    }

    [McpServerTool(Name = "get_recent_exchanges")]
    [Description(
        "The most recent N exchanges, still oldest first so a flow reads forwards. Use this when "
        + "you want a bounded look at what just happened without pulling the whole capture.")]
    public static string GetRecentExchanges(
        CaptureProxy proxy,
        [Description("How many of the most recent exchanges to return.")] int count = 50,
        [Description(CaptureDescription)] string? capture = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (count <= 0) return "count must be greater than zero.";

        return Format(store.Recent(count), $"the last {count} exchange(s)");
    }

    [McpServerTool(Name = "search_exchanges")]
    [Description(
        "Find exchanges containing a keyword. Search 'all' when hunting for a token or id and you "
        + "do not know where it appears - that is how you find every request carrying the same "
        + "value. Narrow to one place when the keyword is common enough to match noise. "
        + "Case-insensitive substring matching; header names and values are both searched.")]
    public static string SearchExchanges(
        CaptureProxy proxy,
        [Description("The text to look for.")] string keyword,
        [Description(
            "Where to look: url, request_headers, response_headers, request_body, response_body, "
            + "or all. Defaults to all.")]
        string search_in = "all",
        [Description(CaptureDescription)] string? capture = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (string.IsNullOrEmpty(keyword)) return "keyword cannot be empty.";

        if (ParseSearchIn(search_in) is not { } where)
            return $"'{search_in}' is not somewhere I can search. Use url, request_headers, "
                 + "response_headers, request_body, response_body or all.";

        var found = store.Search(keyword, where);

        if (found.Count == 0)
            return $"Nothing in {Name(capture)} contains '{keyword}' in {search_in}. "
                 + $"{store.Count} exchange(s) were searched."
                 + (where == ExchangeStore.SearchIn.All
                     ? ""
                     : " Searching 'all' would look in the other places too.");

        return Format(found, $"exchanges containing '{keyword}' in {search_in}");
    }

    [McpServerTool(Name = "get_exchange_detail")]
    [Description(
        "Everything about one exchange: every header both ways, and both bodies in full. This is "
        + "the only tool that returns bodies, so it is how you actually read a request or "
        + "response once a search has pointed you at it.")]
    public static string GetExchangeDetail(
        CaptureProxy proxy,
        [Description("The exchange id, as shown in any of the listing tools.")] long id,
        [Description(CaptureDescription)] string? capture = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (store.GetById(id) is not { } exchange)
            return $"There is no exchange {id} in {Name(capture)}, which holds {store.Count}.";

        var report = new StringBuilder();
        report.AppendLine($"#{exchange.Id}  {exchange.Method} {exchange.Url}");
        report.AppendLine($"started_at: {exchange.StartedAt:O}");
        report.AppendLine($"status: {exchange.StatusCode?.ToString() ?? "(no response recorded)"}"
            + (exchange.DurationMs is { } ms ? $"   duration: {ms:F0}ms" : ""));
        report.AppendLine();

        report.AppendLine("--- request headers ---");
        foreach (var (name, value) in exchange.RequestHeaders)
            report.AppendLine($"{name}: {value}");

        report.AppendLine();
        report.AppendLine(BodySection("request", exchange.RequestBody, exchange.RequestBodyText,
            exchange.RequestBodyTruncated, exchange.RequestContentType));

        if (exchange.ResponseHeaders is { } responseHeaders)
        {
            report.AppendLine("--- response headers ---");
            foreach (var (name, value) in responseHeaders)
                report.AppendLine($"{name}: {value}");

            report.AppendLine();
            report.AppendLine(BodySection("response", exchange.ResponseBody, exchange.ResponseBodyText,
                exchange.ResponseBodyTruncated, exchange.ResponseContentType));
        }

        return report.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_stats")]
    [Description(
        "A summary of a capture: how many exchanges, how many got responses, the busiest hosts, "
        + "the spread of status codes, and the time span covered. Worth calling first to see "
        + "whether what you are looking for is likely to be in there at all.")]
    public static string GetStats(
        CaptureProxy proxy,
        [Description(CaptureDescription)] string? capture = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        var stats = store.Stats();

        if (stats.Total == 0)

            return $"{Name(capture)} is empty. "
                 + (store.Cleared > 0
                     ? $"{store.Cleared} exchange(s) were removed with the clear tools. "
                     : "")
                 + (proxy.CaptureEnabled
                     ? "Recording is on, so either nothing has gone through yet or the proxy is stopped."
                     : "Recording is off - call start_capture.");

        var report = new StringBuilder();
        report.AppendLine($"{Name(capture)}: {stats.Total} exchange(s), "
            + $"{stats.WithResponse} with responses, ~{stats.ApproximateBytes / 1024} KB of bodies.");
        report.AppendLine($"Span: {stats.FirstAt:HH:mm:ss} to {stats.LastAt:HH:mm:ss}. "
            + $"Median duration {stats.MedianDurationMs:F0}ms.");

        // Only when it has happened. A line saying "0 dropped" on every call is noise; a line
        // saying some were dropped changes what the numbers above mean.
        if (store.Dropped > 0)
            report.AppendLine(
                $"NOTE: {store.Dropped} older exchange(s) have been dropped to stay within the "
                + $"buffer caps, out of {store.TotalSeen} recorded. What is here is the most recent "
                + "window, not the whole session.");

        report.AppendLine("Busiest hosts:");
        foreach (var (host, count) in stats.TopHosts)
            report.AppendLine($"  {count,5}  {host}");

        report.AppendLine("Status codes:");
        foreach (var (status, count) in stats.StatusCodes)
            report.AppendLine($"  {count,5}  {status}");

        return report.ToString().TrimEnd();
    }

    [McpServerTool(Name = "get_proxy_status")]
    [Description(
        "What ReqTree is doing right now: whether the proxy is intercepting, whether recording is "
        + "on, what rules, scripts and environments are in force, and which captures are open. "
        + "Call this when something is not behaving as you expect before assuming why.")]
    public static string GetProxyStatus(CaptureProxy proxy)
    {
        var report = new StringBuilder();

        report.AppendLine(proxy.IsRunning
            ? $"Proxy: listening on port {proxy.Port}. "
              + (proxy.IsSystemProxy
                  ? "The machine's proxy settings point at ReqTree, so all traffic passes through it."
                  : "The machine's proxy settings were not changed, so only clients pointed here explicitly pass through.")
            : "Proxy: stopped. Nothing is being intercepted. Call start_proxy.");

        report.AppendLine(proxy.CaptureEnabled
            ? $"Recording: on. {proxy.Capture.Count} exchange(s) held."
            : $"Recording: off. {proxy.Capture.Count} exchange(s) held from earlier."
              + (proxy.Capture.StoppedByLimit ? " Recording stopped because a capture limit was reached." : ""));

        // Said plainly, because an LLM that cannot find an exchange it saw earlier needs to know
        // the buffer dropped it rather than concluding capture is broken.
        var store = proxy.Capture;
        report.AppendLine(
            $"Buffer: {store.Count} held of "
            + (store.Capacity == 0 ? "unlimited" : $"{store.Capacity} max")
            + $", ~{store.ApproximateBytes / 1024} KB of "
            + (store.MaxBytes == 0 ? "unlimited" : $"{store.MaxBytes / 1024 / 1024} MB max")
            + $". {store.TotalSeen} recorded in total"
            + (store.Dropped > 0
                ? $", of which {store.Dropped} were DROPPED as the oldest to stay within the caps."
                : ", none dropped.")
            // Otherwise a capture emptied on purpose reads as one that lost its contents by itself,
            // and the next move is to go hunting for a bug that is not there.
            + (store.Cleared > 0
                ? $" {store.Cleared} were removed deliberately by one of the clear tools."
                : ""));

        if (proxy.ArmedWindow is { } window)
            report.AppendLine($"Capture window: armed by {window.ArmedBy ?? "unidentified"} - "
                + $"recording stops when {window.Description}.");

        report.AppendLine($"Rules: {proxy.Rules.Count} ({proxy.Rules.Count(r => r.Enabled)} enabled).");
        report.AppendLine($"Scripts: {proxy.Scripts.Count} ({proxy.Scripts.Count(s => s.Enabled)} enabled).");

        report.AppendLine(proxy.Environments.Count == 0
            ? "Environments: none."
            : "Environments (their scripts run before the standalone ones): "
              + string.Join(", ", proxy.Environments.Select(e =>
                  $"{e.Name} ({e.Scripts.Count} script(s), {(e.Enabled ? "on" : "OFF")})")));

        var opened = proxy.OpenedCaptures;
        report.AppendLine(opened.Count == 0
            ? "Open captures: none besides the live one."
            : "Open captures: " + string.Join(", ", opened));

        return report.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------------------
    // Saving and opening
    // -------------------------------------------------------------------------------------

    [McpServerTool(Name = "save_capture")]
    [Description(
        "Write a capture to a SQLite file so it outlives this ReqTree process. Nothing is written "
        + "to disk until you call this, so a capture you care about is not saved until you say so. "
        + "The file can be reopened later with open_capture, or with 'reqtree open <path>'.")]
    public static string SaveCapture(
        CaptureProxy proxy,
        [Description("Where to write it. An absolute path is safest; .reqtree is the usual extension.")]
        string path,
        [Description(CaptureDescription)] string? capture = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        var who = Actor.Resolve(actor, mcpServer);

        if (store.Count == 0)
            return $"{Name(capture)} is empty, so there is nothing to save. Nothing was written.";

        try
        {
            var full = Path.GetFullPath(path);
            var written = CaptureFile.Save(store, full);

            Log.Information("{Actor} saved {Count} exchange(s) from {Capture} to {Path}.",
                who, written, Name(capture), full);

            return $"Saved {written} exchange(s) from {Name(capture)} to {full} as {who}. "
                 + "Reopen it with open_capture or 'reqtree open'.";
        }
        catch (Exception ex)
        {
            return $"Could not save to {path}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "open_capture")]
    [Description(
        "Open a previously saved capture file and make it queryable alongside the live one. Every "
        + "read tool takes a 'capture' argument naming which to read. The live capture is not "
        + "disturbed, so you can compare a recorded session against what is happening now.")]
    public static string OpenCapture(
        CaptureProxy proxy,
        [Description("Path to the saved capture file.")] string path,
        [Description("A short name to refer to it by. Defaults to the file name.")]
        string? name = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        try
        {
            var full = Path.GetFullPath(path);

            var label = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(full)
                : name.Trim();

            if (label.Equals("live", StringComparison.OrdinalIgnoreCase))
                return "'live' is the name of the capture being recorded now. Pick another.";

            // A blank label resolves back to the live capture, because that is what an omitted
            // 'capture' argument means. Left unchecked, a file called ".reqtree" would report as
            // opened and then every read against it would quietly serve live traffic instead.
            if (string.IsNullOrWhiteSpace(label))
                return $"Could not work out a name for {full} — its file name is empty. "
                     + "Pass an explicit name.";

            // Opened only once the name is known to be usable, so a rejected call has not spent
            // time and memory reading a file it is about to refuse.
            var store = CaptureFile.Open(full);
            proxy.AddOpenedCapture(label, store);

            Log.Information("{Actor} opened {Path} as '{Label}' ({Count} exchange(s)).",
                who, full, label, store.Count);

            var stats = store.Stats();

            return $"Opened {full} as '{label}' for {who}: {store.Count} exchange(s) "
                 + $"from {stats.FirstAt:HH:mm:ss} to {stats.LastAt:HH:mm:ss}. "
                 + $"Pass capture='{label}' to any read tool. The live capture is untouched.";
        }
        catch (Exception ex)
        {
            return $"Could not open {path}: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_captures")]
    [Description("The captures available to read: the live one, plus any opened from files.")]
    public static string ListCaptures(CaptureProxy proxy)
    {
        var report = new StringBuilder();

        // Said as one fact rather than two. "being recorded now (recording off)" contradicted
        // itself, which is worse than saying less.
        report.AppendLine($"live - {proxy.Capture.Count} exchange(s). "
            + (proxy.CaptureEnabled
                ? proxy.IsRunning
                    ? "Recording, and the proxy is intercepting."
                    : "Recording is on, but the proxy is stopped so nothing is arriving."
                : "Recording is off - nothing new is being kept."));

        foreach (var name in proxy.OpenedCaptures)
            if (proxy.ResolveCapture(name) is { } store)
                report.AppendLine($"{name} - {store.Count} exchange(s), opened from a file.");

        return report.ToString().TrimEnd();
    }

    [McpServerTool(Name = "close_capture")]
    [Description("Forget a capture opened from a file, freeing the memory it holds. The live capture cannot be closed.")]
    public static string CloseCapture(
        CaptureProxy proxy,
        [Description("The name it was opened under.")] string name,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        // Trimmed to match how open_capture stored it, so a name copied out of list_captures with
        // stray whitespace still finds its capture.
        name = name.Trim();

        if (name.Equals("live", StringComparison.OrdinalIgnoreCase))
            return "The live capture cannot be closed. Use stop_capture to stop recording into it.";

        if (!proxy.CloseCapture(name))
            return $"There is no open capture called '{name}'. Call list_captures to see what is open.";

        Log.Information("{Actor} closed capture '{Name}'.", who, name);
        return $"Closed '{name}'.";
    }

    // -------------------------------------------------------------------------------------
    // Clearing. The same four ways of choosing exchanges the read tools offer, because the
    // question "which ones do I mean" is the same question either way. There is no undo: the
    // capture is memory, so what these remove is gone unless save_capture was called first.
    // -------------------------------------------------------------------------------------

    private const string ClearWarning =
        "This cannot be undone - the capture lives in memory, so anything removed is gone unless "
        + "save_capture was called first.";

    [McpServerTool(Name = "clear_all_exchanges")]
    [Description(
        "Remove every exchange from a capture, leaving it empty and ready to record a clean run. "
        + "Recording and the proxy are left exactly as they are, so traffic arriving next is kept. "
        + "Use this to get a clean slate before reproducing something, rather than restarting "
        + "ReqTree. " + ClearWarning)]
    public static string ClearAllExchanges(
        CaptureProxy proxy,
        [Description(CaptureDescription)] string? capture = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        var who = Actor.Resolve(actor, mcpServer);
        var removed = store.Clear();

        Log.Warning("{Actor} cleared all {Count} exchange(s) from {Capture}.",
            who, removed, Name(capture));

        return removed == 0
            ? $"{Name(capture)} was already empty. Nothing was removed."
            : $"Removed all {removed} exchange(s) from {Name(capture)} as {who}. " + Recording(proxy, capture);
    }

    [McpServerTool(Name = "clear_exchanges_by_count")]
    [Description(
        "Remove a number of exchanges from one end: the oldest, to prune what has piled up, or the "
        + "newest, to undo a run you have just made and try again. " + ClearWarning)]
    public static string ClearExchangesByCount(
        CaptureProxy proxy,
        [Description("How many to remove. More than are held removes everything.")] int count,
        [Description("Which end to take them from: 'oldest' or 'newest'. Defaults to oldest.")]
        string from = "oldest",
        [Description(CaptureDescription)] string? capture = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (count <= 0) return "count must be greater than zero.";

        bool oldest;

        switch (from.Trim().ToLowerInvariant())
        {
            case "oldest": case "front": case "start": oldest = true; break;
            case "newest": case "back": case "end": oldest = false; break;
            default:
                return $"'{from}' is not an end I can take from. Use 'oldest' or 'newest'.";
        }

        var who = Actor.Resolve(actor, mcpServer);
        var held = store.Count;
        var removed = oldest ? store.RemoveOldest(count) : store.RemoveNewest(count);

        Log.Warning("{Actor} cleared the {Count} {End} exchange(s) from {Capture}; {Left} left.",
            who, removed, oldest ? "oldest" : "newest", Name(capture), store.Count);

        if (removed == 0)
            return $"{Name(capture)} is empty, so there was nothing to remove.";

        // Said when it happened, because "remove 500" against 300 held is a different outcome from
        // the one asked for, and the caller should not have to infer it from the count.
        var short_ = removed < count
            ? $" That was all of them - only {held} were held, fewer than the {count} asked for."
            : "";

        return $"Removed the {removed} {(oldest ? "oldest" : "newest")} exchange(s) from "
             + $"{Name(capture)} as {who}.{short_} {store.Count} remain.";
    }

    [McpServerTool(Name = "clear_exchanges_matching")]
    [Description(
        "Remove every exchange a search would find - same keyword and same places to look as "
        + "search_exchanges. Use it to drop noise you do not care about, such as a CDN host or a "
        + "telemetry endpoint, so what is left is the flow you are actually reading. Run "
        + "search_exchanges with the same arguments first to see exactly what will go. " + ClearWarning)]
    public static string ClearExchangesMatching(
        CaptureProxy proxy,
        [Description("The text to look for. Every exchange containing it is removed.")] string keyword,
        [Description(
            "Where to look: url, request_headers, response_headers, request_body, response_body, "
            + "or all. Defaults to url, which is the one that matches what you meant most often.")]
        string search_in = "url",
        [Description(CaptureDescription)] string? capture = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (string.IsNullOrEmpty(keyword)) return "keyword cannot be empty.";

        if (ParseSearchIn(search_in) is not { } where)
            return $"'{search_in}' is not somewhere I can search. Use url, request_headers, "
                 + "response_headers, request_body, response_body or all.";

        var who = Actor.Resolve(actor, mcpServer);

        // Flattened before it is matched against or logged: a newline here would forge a log line.
        keyword = Actor.Flatten(keyword);

        var removed = store.RemoveMatching(keyword, where);

        Log.Warning("{Actor} cleared {Count} exchange(s) matching {Keyword} in {Where} from {Capture}.",
            who, removed, keyword, search_in, Name(capture));

        return removed == 0
            ? $"Nothing in {Name(capture)} matched '{keyword}' in {search_in}, so nothing was "
              + $"removed. {store.Count} still held."
            : $"Removed {removed} exchange(s) matching '{keyword}' in {search_in} from "
              + $"{Name(capture)} as {who}. {store.Count} remain.";
    }

    [McpServerTool(Name = "clear_exchanges_older_than")]
    [Description(
        "Remove exchanges that started more than N minutes ago, keeping only the recent window. "
        + "The mirror of get_exchanges_since: that one shows you the last N minutes, this one "
        + "throws away everything before them. Fractions are fine. " + ClearWarning)]
    public static string ClearExchangesOlderThan(
        CaptureProxy proxy,
        [Description("Exchanges that started further back than this many minutes are removed.")]
        double minutes,
        [Description(CaptureDescription)] string? capture = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        if (proxy.ResolveCapture(capture) is not { } store) return NoSuchCapture(proxy, capture);

        if (minutes <= 0)
            return "minutes must be greater than zero. To remove everything, call clear_all_exchanges.";

        var who = Actor.Resolve(actor, mcpServer);
        var removed = store.RemoveOlderThan(minutes);

        Log.Warning("{Actor} cleared {Count} exchange(s) older than {Minutes} minute(s) from {Capture}.",
            who, removed, minutes, Name(capture));

        return removed == 0
            ? $"Nothing in {Name(capture)} was older than {minutes} minute(s), so nothing was "
              + $"removed. {store.Count} still held."
            : $"Removed {removed} exchange(s) older than {minutes} minute(s) from {Name(capture)} "
              + $"as {who}. {store.Count} remain.";
    }

    /// <summary>
    /// What happens next, said after a capture is emptied. An LLM clearing to start a clean run
    /// needs to know whether the run will actually be recorded, and the two switches are separate.
    /// </summary>
    private static string Recording(CaptureProxy proxy, string? capture)
    {
        if (!string.IsNullOrWhiteSpace(capture) && !capture.Equals("live", StringComparison.OrdinalIgnoreCase))
            return "That capture was read from a file, so nothing will refill it.";

        if (!proxy.CaptureEnabled)
            return "Recording is OFF, so it will stay empty until start_capture is called.";

        return proxy.IsRunning
            ? "Recording is on and the proxy is intercepting, so new traffic will land here."
            : "Recording is on, but the proxy is STOPPED, so nothing will arrive until start_proxy.";
    }

    // -------------------------------------------------------------------------------------

    private static string Name(string? capture) =>
        string.IsNullOrWhiteSpace(capture) ? "the live capture" : $"capture '{capture}'";

    /// <summary>
    /// The <c>search_in</c> argument, or null if it names nowhere. Shared by searching and
    /// clearing so the two cannot come to accept different words for the same place.
    /// </summary>
    private static ExchangeStore.SearchIn? ParseSearchIn(string search_in) =>
        search_in.Trim().ToLowerInvariant() switch
        {
            "url" => ExchangeStore.SearchIn.Url,
            "request_headers" => ExchangeStore.SearchIn.RequestHeaders,
            "response_headers" => ExchangeStore.SearchIn.ResponseHeaders,
            "request_body" => ExchangeStore.SearchIn.RequestBody,
            "response_body" => ExchangeStore.SearchIn.ResponseBody,
            "all" => ExchangeStore.SearchIn.All,
            _ => null,
        };

    private static string NoSuchCapture(CaptureProxy proxy, string? capture)
    {
        var open = proxy.OpenedCaptures;

        return $"There is no capture called '{capture}'. Available: live"
             + (open.Count > 0 ? ", " + string.Join(", ", open) : "")
             + ". Use open_capture to open a saved file.";
    }

    /// <summary>
    /// The one-line-per-exchange summary every listing tool returns.
    /// </summary>
    /// <remarks>
    /// Deliberately fixed rather than configurable. It carries the id (so detail can be asked
    /// for), the method and url (so it can be recognised), and the status and size (so it can be
    /// judged) — which is everything needed to decide what to look at next and nothing more.
    /// </remarks>
    /// <summary>
    /// Most exchanges any one listing will return.
    /// </summary>
    /// <remarks>
    /// A full buffer is five thousand exchanges, and one line each is around half a megabyte —
    /// which is not a long answer, it is an unusable one, arriving exactly when the capture has
    /// enough in it to be worth reading. The newest are kept and the rest are counted, with the
    /// tools that narrow named in the reply.
    /// </remarks>
    private const int MaxListed = 300;

    private static string Format(IReadOnlyList<Exchange> exchanges, string what)
    {
        if (exchanges.Count == 0)
            return $"No {what}.";

        var omitted = Math.Max(exchanges.Count - MaxListed, 0);
        var shown = omitted == 0 ? exchanges : [.. exchanges.Skip(omitted)];

        var report = new StringBuilder(
            omitted == 0
                ? $"{exchanges.Count} {what}:\n"
                : $"{exchanges.Count} {what}. Showing the {MaxListed} most recent; "
                  + $"{omitted} older one(s) are not listed:\n");

        foreach (var exchange in shown)
            report.AppendLine(
                $"#{exchange.Id,-5} {exchange.StartedAt:HH:mm:ss} "
                + $"{exchange.StatusCode?.ToString() ?? "---",-4} "
                + $"{exchange.Method,-6} {exchange.Url} "
                + $"[req {exchange.RequestBody?.Length ?? 0}b, resp {exchange.ResponseBody?.Length ?? 0}b"
                + (exchange.DurationMs is { } ms ? $", {ms:F0}ms]" : "]"));

        report.AppendLine();

        if (omitted > 0)
            report.AppendLine(
                "To see the ones left out, narrow rather than widen: search_exchanges for a "
                + "keyword, or get_exchanges_since for a window in time.");

        report.Append("Call get_exchange_detail with an id for its headers and bodies.");

        return report.ToString();
    }

    /// <summary>
    /// Most body text shown for one exchange. Bodies are captured up to a megabyte, and returning
    /// one of those whole would spend a context window on a single response.
    /// </summary>
    private const int MaxBodyShown = 20_000;

    private static string BodySection(
        string which, byte[]? body, string text, bool truncated, string? contentType)
    {
        if (body is null || body.Length == 0)
            return $"--- {which} body: none ---";

        var header = $"--- {which} body: {body.Length} bytes"
            + (contentType is null ? "" : $", {contentType}")
            + (truncated ? ", TRUNCATED at the capture cap" : "")
            + " ---";

        // Empty text from a non-empty body means it did not decode as UTF-8, which is worth saying
        // rather than printing nothing and letting it read as an empty body.
        if (text.Length == 0)
            return $"{header}\n(binary - not valid UTF-8, so not shown as text)";

        if (text.Length <= MaxBodyShown)
            return $"{header}\n{text}";

        // Cut for display only. The whole body is still in the capture, and search_exchanges reads
        // all of it — so a keyword that is not in this excerpt can still be confirmed to be there.
        return $"{header}\n{text[..MaxBodyShown]}\n\n"
             + $"[... cut for display after {MaxBodyShown} of {text.Length} characters. The whole "
             + "body is still captured, and search_exchanges searches all of it.]";
    }
}
