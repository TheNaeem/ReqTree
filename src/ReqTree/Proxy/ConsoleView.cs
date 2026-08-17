using ReqTree.Proxy.Objects;

namespace ReqTree.Proxy;

/// <summary>
/// One line per finished exchange, for a person watching the terminal.
/// </summary>
/// <remarks>
/// Written straight to the console rather than through Serilog, and that is the point: this is a
/// live view for a human deciding whether the thing they are trying to capture is going through,
/// not a record of what happened. Routing it through the logger would timestamp and level-tag
/// every line and put a copy of every request URL in a file on disk that lives for a week.
/// </remarks>
public static class ConsoleView
{
    /// <summary>Prints one exchange. Attached to <see cref="CaptureProxy.ExchangeCompleted"/>.</summary>
    public static void Print(Exchange exchange)
    {
        var status = exchange.StatusCode?.ToString() ?? "---";
        var duration = exchange.DurationMs is { } ms ? $"{ms,5:F0}ms" : "     -";
        var size = exchange.ResponseBody?.Length ?? 0;

        // Truncated so a URL with a page of query string cannot wrap and make the view unreadable.
        var url = Printable(exchange.Url);
        if (url.Length > 90) url = url[..87] + "...";

        Console.WriteLine(
            $"{exchange.StartedAt:HH:mm:ss} {status,-4} {Printable(exchange.Method),-6} {duration} "
            + $"{size,8}b  {url}");
    }

    /// <summary>
    /// Replaces control characters with a dot.
    /// </summary>
    /// <remarks>
    /// The url and method come off the wire, from whatever the client sent. Writing them to a
    /// terminal unfiltered means an escape sequence in a request could move the cursor, recolour
    /// the view, or overwrite the lines above it — and ReqTree exists to sit in front of traffic
    /// nobody trusts. The stored exchange keeps the original; only what is displayed is filtered.
    /// </remarks>
    private static string Printable(string value)
    {
        if (!value.Any(char.IsControl)) return value;

        return string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = char.IsControl(source[i]) ? '.' : source[i];
        });
    }
}
