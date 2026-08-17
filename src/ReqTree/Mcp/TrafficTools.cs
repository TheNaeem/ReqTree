using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ReqTree.Persistence;
using ReqTree.Proxy;
using ReqTree.Proxy.Objects;

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

        ExchangeStore.SearchIn where;

        switch (search_in.Trim().ToLowerInvariant())
        {
            case "url": where = ExchangeStore.SearchIn.Url; break;
            case "request_headers": where = ExchangeStore.SearchIn.RequestHeaders; break;
            case "response_headers": where = ExchangeStore.SearchIn.ResponseHeaders; break;
            case "request_body": where = ExchangeStore.SearchIn.RequestBody; break;
            case "response_body": where = ExchangeStore.SearchIn.ResponseBody; break;
            case "all": where = ExchangeStore.SearchIn.All; break;
            default:
                return $"'{search_in}' is not somewhere I can search. Use url, request_headers, "
                     + "response_headers, request_body, response_body or all.";
        }

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
                : ", none dropped."));

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
        [Description("The name it was opened under.")] string name)
    {
        // Trimmed to match how open_capture stored it, so a name copied out of list_captures with
        // stray whitespace still finds its capture.
        name = name.Trim();

        if (name.Equals("live", StringComparison.OrdinalIgnoreCase))
            return "The live capture cannot be closed. Use stop_capture to stop recording into it.";

        return proxy.CloseCapture(name)
            ? $"Closed '{name}'."
            : $"There is no open capture called '{name}'. Call list_captures to see what is open.";
    }

    // -------------------------------------------------------------------------------------

    private static string Name(string? capture) =>
        string.IsNullOrWhiteSpace(capture) ? "the live capture" : $"capture '{capture}'";

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
