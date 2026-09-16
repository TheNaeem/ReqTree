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
        set { _requestBody = value; _requestBodyText = null; _cachedRequestBody = null; }
    }

    private byte[]? _requestBody;

    /// <summary>True when the body was longer than the cap and only the head of it was kept.</summary>
    public bool RequestBodyTruncated { get; set; }

    // --- Response half, filled in when the response arrives ---

    public int? StatusCode { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>The protocol spoken on the response side, which may differ after proxy translation.</summary>
    public string? ResponseHttpVersion { get; set; }
    public IReadOnlyList<(string Name, string Value)>? ResponseHeaders { get; set; }
    public string? ResponseContentType { get; set; }
    public byte[]? ResponseBody
    {
        get => _responseBody;
        set { _responseBody = value; _responseBodyText = null; _cachedResponseBody = null; }
    }

    private byte[]? _responseBody;
    public bool ResponseBodyTruncated { get; set; }

    /// <summary>
    /// Size of the response body as the server reported it, which is what a person wants to see
    /// even when the body itself was not stored.
    /// </summary>
    public long ResponseSizeBytes { get; set; }

    // WebSocket frames arrive after the HTTP upgrade response and can arrive from both directions
    // at once. Keep the mutable collection behind its own short lock so a read tool or save can
    // snapshot it while the relay threads append without exposing a List during mutation.
    private readonly Lock _webSocketSync = new();
    private readonly List<CapturedWebSocketFrame> _webSocketFrames = [];
    private long _webSocketFrameBytes;
    private int _webSocketFramesOmitted;

    /// <summary>Decoded WebSocket frames in arrival order.</summary>
    public IReadOnlyList<CapturedWebSocketFrame> WebSocketFrames
    {
        get { lock (_webSocketSync) return [.. _webSocketFrames]; }
    }

    /// <summary>Payload bytes retained across all WebSocket frames on this exchange.</summary>
    public long WebSocketFrameBytes
    {
        get { lock (_webSocketSync) return _webSocketFrameBytes; }
    }

    /// <summary>Frames omitted after this exchange reached its per-socket safety cap.</summary>
    public int WebSocketFramesOmitted
    {
        get { lock (_webSocketSync) return _webSocketFramesOmitted; }
        internal set { lock (_webSocketSync) _webSocketFramesOmitted = value; }
    }

    /// <summary>
    /// Adds one frame if doing so stays inside the per-socket caps. Returns false when omitted.
    /// </summary>
    internal bool AddWebSocketFrame(
        CapturedWebSocketFrame frame, int maxFrames = int.MaxValue, long maxBytes = long.MaxValue)
    {
        lock (_webSocketSync)
        {
            if (_webSocketFrames.Count >= maxFrames
                || _webSocketFrameBytes + frame.Data.Length > maxBytes)
            {
                _webSocketFramesOmitted++;
                return false;
            }

            _webSocketFrames.Add(frame);
            _webSocketFrameBytes += frame.Data.Length;
            return true;
        }
    }

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

    /// <summary>Which body the cached request text was decoded from. Null when nothing is cached.</summary>
    private byte[]? _cachedRequestBody;

    /// <summary>Which body the cached response text was decoded from. Null when nothing is cached.</summary>
    private byte[]? _cachedResponseBody;

    /// <summary>Request body as text, or empty when absent or binary.</summary>
    public string RequestBodyText =>
        BodyText(_requestBody, ref _requestBodyText, ref _cachedRequestBody);

    /// <summary>Response body as text, or empty when absent or binary.</summary>
    public string ResponseBodyText =>
        BodyText(_responseBody, ref _responseBodyText, ref _cachedResponseBody);

    /// <summary>
    /// Decodes <paramref name="body"/> once and caches the result against that exact array.
    /// </summary>
    /// <remarks>
    /// The cached text is only trusted when the body reference still matches the one it was decoded
    /// from. A rule or script can replace a body on a proxy thread while a search reads its text on
    /// another, and with a plain <c>??=</c> that race could leave the pre-rewrite text cached
    /// indefinitely — the original body would stay findable through search_exchanges after a
    /// redaction rule had already replaced it. Keying the cache to the body reference makes a stale
    /// pair visible and forces a re-decode on the very next read.
    /// </remarks>
    private static string BodyText(byte[]? body, ref string? cachedText, ref byte[]? cachedBody)
    {
        var text = cachedText;
        if (text is not null && ReferenceEquals(cachedBody, body))
            return text;

        var decoded = DecodeUtf8(body) ?? "";
        cachedText = decoded;
        cachedBody = body;
        return decoded;
    }

    /// <summary>Built once: <see cref="Encoding.GetString"/> is thread-safe for reading.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

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
            return StrictUtf8.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>Direction of one captured WebSocket frame.</summary>
public enum WebSocketFrameDirection
{
    ClientToServer,
    ServerToClient,
}

/// <summary>One decoded WebSocket frame attached to its HTTP upgrade exchange.</summary>
public sealed record CapturedWebSocketFrame(
    DateTimeOffset CapturedAt,
    WebSocketFrameDirection Direction,
    string OpCode,
    bool IsFinal,
    byte[] Data,
    bool DataTruncated)
{
    /// <summary>Payload as UTF-8 for text/continuation frames, or null for binary data.</summary>
    public string? Text => OpCode is "Text" or "Continuation" ? Exchange.DecodeUtf8(Data) : null;
}
