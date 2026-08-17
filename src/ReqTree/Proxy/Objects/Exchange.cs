using System.Text;

namespace ReqTree.Proxy.Objects;

/// <summary>
/// One request and its response, as ReqTree sees it.
/// </summary>
/// <remarks>
/// This is the only shape that crosses from the proxy layer to everything downstream. It is
/// deliberately a plain mutable class: the request half is filled in when the request is seen, and
/// the response half is filled in later, on the same instance, when the response comes back.
/// </remarks>
public sealed class Exchange
{
    /// <summary>
    /// Identity within a store, assigned when it is first added. Zero means it has not been added
    /// anywhere yet, which is how AddExchange tells a new exchange from one coming back to have its
    /// response filled in.
    /// </summary>
    public long Id { get; set; }

    // --- Request half, known immediately ---
    //
    // Url, RequestHeaders and RequestBody are settable rather than init-only, because changing one
    // is how a rule or script reaches real traffic: the proxy compares them against what arrived
    // and writes any change back onto the live request before sending it upstream.
    //
    // Headers and body are compared by reference, so a change has to be *assigned* to be noticed.
    // Casting RequestHeaders back to a List and mutating it in place alters what ReqTree records
    // and leaves the real request untouched.

    public required DateTimeOffset StartedAt { get; init; }
    public required string Method { get; init; }
    public required string Url { get; set; }
    public required string Host { get; init; }
    public required string Path { get; init; }
    public required string QueryString { get; init; }
    public required string HttpVersion { get; init; }
    /// <summary>Headers as sent on the wire. A list, not a dictionary, because headers repeat.</summary>
    public required IReadOnlyList<(string Name, string Value)> RequestHeaders { get; set; }
    public string? RequestContentType { get; set; }

    /// <summary>Request body, if one was present and we chose to capture it.</summary>
    public byte[]? RequestBody
    {
        get => _requestBody;
        set { _requestBody = value; _requestBodyText = null; }
    }

    private byte[]? _requestBody;

    /// <summary>True when the body was longer than the cap and only the head of it was kept.</summary>
    public bool RequestBodyTruncated { get; set; }

    // --- Response half, filled in when the response arrives ---

    public int? StatusCode { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public IReadOnlyList<(string Name, string Value)>? ResponseHeaders { get; set; }
    public string? ResponseContentType { get; set; }
    public byte[]? ResponseBody
    {
        get => _responseBody;
        set { _responseBody = value; _responseBodyText = null; }
    }

    private byte[]? _responseBody;
    public bool ResponseBodyTruncated { get; set; }

    /// <summary>
    /// Size of the response body as the server reported it, which is what a person wants to see
    /// even when the body itself was not stored.
    /// </summary>
    public long ResponseSizeBytes { get; set; }

    /// <summary>True once the response half has been filled in.</summary>
    public bool HasResponse => CompletedAt is not null;

    /// <summary>Wall-clock time from request seen to response seen. Null until the response arrives.</summary>
    public double? DurationMs =>
        CompletedAt is null ? null : (CompletedAt.Value - StartedAt).TotalMilliseconds;

    /// <summary>Host and path together, which is how a request reads in a log line.</summary>
    public string HostAndPath => Host + Path;

    // Bodies are decoded once and kept: a keyword search re-reads every body in the capture, and
    // decoding several thousand of them per query would dominate the cost.
    //
    // The cache is cleared whenever a body is assigned, and that is not optional. Rules and scripts
    // rewrite bodies — redacting a request, mocking a response — and a cache filled in before the
    // rewrite would go on returning the old text forever. A rule matching on `request_body` reads
    // this property, so a redaction rule placed after one would leave the original body findable
    // through search_exchanges and printed by get_exchange_detail, while the wire carried the
    // redacted version.
    private string? _requestBodyText;
    private string? _responseBodyText;

    /// <summary>Request body as text, or empty when absent or binary.</summary>
    public string RequestBodyText => _requestBodyText ??= DecodeUtf8(RequestBody) ?? "";

    /// <summary>Response body as text, or empty when absent or binary.</summary>
    public string ResponseBodyText => _responseBodyText ??= DecodeUtf8(ResponseBody) ?? "";

    /// <summary>
    /// Bytes as text, or null when they are not valid UTF-8.
    /// </summary>
    /// <remarks>
    /// Strict decoding is the point: <see cref="Encoding.UTF8"/> normally replaces invalid bytes
    /// with U+FFFD and returns cheerful nonsense. Throwing instead lets callers report "this body
    /// is binary" honestly rather than handing back replacement characters.
    /// </remarks>
    public static string? DecodeUtf8(byte[]? body)
    {
        if (body is null) return null;
        if (body.Length == 0) return "";

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
