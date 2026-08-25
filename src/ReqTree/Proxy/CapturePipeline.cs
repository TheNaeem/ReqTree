using System.Net;
using ReqTree.Proxy.Objects;
using Serilog;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace ReqTree.Proxy;

/// <summary>
/// Turns Titanium request and response hooks into exchanges, then writes deliberate changes back.
/// </summary>
/// <remarks>
/// The request and response handlers share state through Titanium's session user data. Keeping
/// both halves here makes the record-first, behavior-second ordering explicit: rules and scripts
/// may change traffic, but the capture remains what actually arrived from each peer.
/// </remarks>
internal sealed class CapturePipeline
{
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly string[] CapturedContentTypes =
    [
        "text/",
        "application/json",
        "application/ld+json",
        "application/problem+json",
        "application/xml",
        "application/xhtml+xml",
        "application/javascript",
        "application/x-javascript",
        "application/x-www-form-urlencoded",
        "application/graphql",
        "application/csp-report",
        "multipart/form-data",
    ];

    private readonly ExchangeStore _capture;
    private readonly Action<Exchange, ProxyHook> _applyBehaviour;
    private readonly Action<Exchange> _exchangeCompleted;
    private long _nextExchangeId;
    private volatile bool _captureEnabled = true;
    private CaptureWindowState? _captureWindow;
    private long _windowClosingExchangeId;

    internal CapturePipeline(
        ExchangeStore capture,
        Action<Exchange, ProxyHook> applyBehaviour,
        Action<Exchange> exchangeCompleted)
    {
        _capture = capture;
        _applyBehaviour = applyBehaviour;
        _exchangeCompleted = exchangeCompleted;
    }

    internal bool CaptureEnabled
    {
        get => _captureEnabled;
        set => _captureEnabled = value;
    }

    internal CaptureWindowState? ArmedWindow => Volatile.Read(ref _captureWindow);

    internal void Arm(CaptureWindowState window)
    {
        Volatile.Write(ref _captureWindow, window);
        Log.Information("Capture window armed by {Actor}: recording stops when {Description}.",
            window.ArmedBy ?? "unidentified", window.Description);
    }

    internal CaptureWindowState? Disarm() => Interlocked.Exchange(ref _captureWindow, null);

    internal void Attach(ProxyServer server)
    {
        server.BeforeRequest += OnBeforeRequestAsync;
        server.BeforeResponse += OnBeforeResponseAsync;
    }

    private async Task OnBeforeRequestAsync(object sender, SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        var uri = request.RequestUri;
        var exchange = new Exchange
        {
            Id = Interlocked.Increment(ref _nextExchangeId),
            StartedAt = DateTimeOffset.Now,
            Method = request.Method,
            Url = request.Url,
            Host = uri.Host,
            Path = uri.AbsolutePath,
            QueryString = uri.Query,
            HttpVersion = request.HttpVersion.ToString(),
            RequestHeaders = ReadHeaders(request.Headers),
            RequestContentType = request.ContentType,
        };

        e.UserData = exchange;

        if (request.HasBody && ShouldCaptureExchange(request.ContentType, request.ContentLength))
        {
            try
            {
                request.KeepBody = true;
                (exchange.RequestBody, exchange.RequestBodyTruncated) = Truncate(await e.GetRequestBody());
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the request body for {Url}: {Reason}", request.Url, ex.Message);
            }
        }

        if (_captureEnabled)
        {
            _capture.AddExchange(exchange);
            CheckCaptureWindow(exchange);
        }

        var originalUrl = exchange.Url;
        var originalHeaders = exchange.RequestHeaders;
        var originalBody = exchange.RequestBody;

        _applyBehaviour(exchange, ProxyHook.BeforeRequest);

        var recordedRequest = ExchangeSnapshot.CopyOf(exchange);
        if (_captureEnabled || Interlocked.Read(ref _windowClosingExchangeId) == exchange.Id)
            _capture.AddExchange(recordedRequest);

        if (exchange.StatusCode is not null)
        {
            exchange.CompletedAt ??= DateTimeOffset.Now;
            exchange.ResponseSizeBytes = exchange.ResponseBody?.Length ?? 0;

            var headers = new List<HttpHeader>
            {
                new("Content-Type", exchange.ResponseContentType ?? "application/json"),
            };

            if (exchange.ResponseHeaders is { } extra)
                foreach (var (name, value) in extra)
                    if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        headers.Add(new HttpHeader(name, value));

            e.GenericResponse(
                exchange.ResponseBody ?? [],
                (HttpStatusCode)exchange.StatusCode.Value,
                headers,
                closeServerConnection: true);

            // There is no response hook for a locally answered request. Update the same held
            // exchange after its response half is complete, or the capture keeps the status and
            // body but reports it as unfinished.
            var completedExchange = ExchangeSnapshot.CopyOf(exchange);
            if (_captureEnabled || Interlocked.Read(ref _windowClosingExchangeId) == exchange.Id)
            {
                _capture.AddExchange(completedExchange);
                Interlocked.Exchange(ref _windowClosingExchangeId, 0);
            }
            else
            {
                _capture.UpdateExisting(completedExchange);
            }

            e.UserData = null;

            Log.Information(
                "Answered {Method} {Url} locally with {Status} ({Bytes} byte body); it was not "
                + "sent upstream. Exchange {Id}.",
                exchange.Method, exchange.Url, exchange.StatusCode,
                exchange.ResponseBody?.Length ?? 0, exchange.Id);

            _exchangeCompleted(exchange);
            return;
        }

        WriteRequestChangesBack(exchange, e, originalUrl, originalHeaders, originalBody);
    }

    private async Task OnBeforeResponseAsync(object sender, SessionEventArgs e)
    {
        if (e.UserData is not Exchange exchange)
            return;

        var response = e.HttpClient.Response;
        exchange.CompletedAt = DateTimeOffset.Now;
        exchange.StatusCode = response.StatusCode;
        exchange.ResponseHeaders = ReadHeaders(response.Headers);
        exchange.ResponseContentType = response.ContentType;
        exchange.ResponseSizeBytes = response.ContentLength > 0 ? response.ContentLength : 0;

        if (response.HasBody && ShouldCaptureExchange(response.ContentType, response.ContentLength))
        {
            try
            {
                response.KeepBody = true;
                var body = await e.GetResponseBody();
                (exchange.ResponseBody, exchange.ResponseBodyTruncated) = Truncate(body);

                if (exchange.ResponseSizeBytes == 0)
                    exchange.ResponseSizeBytes = body.Length;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the response body for {Url}: {Reason}", exchange.Url, ex.Message);
            }
        }

        var originalStatus = exchange.StatusCode;
        var originalHeaders = exchange.ResponseHeaders;
        var originalBody = exchange.ResponseBody;
        var recordedResponse = ExchangeSnapshot.CopyOf(exchange);

        _applyBehaviour(exchange, ProxyHook.BeforeResponse);

        if (_captureEnabled || Interlocked.Read(ref _windowClosingExchangeId) == exchange.Id)
        {
            _capture.AddExchange(recordedResponse);
            Interlocked.Exchange(ref _windowClosingExchangeId, 0);
        }
        else
        {
            _capture.UpdateExisting(recordedResponse);
        }

        if (exchange.StatusCode != originalStatus && exchange.StatusCode is { } status)
        {
            response.StatusCode = status;
            Log.Information("Response {Id} status rewritten from {From} to {To}.",
                exchange.Id, originalStatus, status);
        }

        if (!ReferenceEquals(exchange.ResponseHeaders, originalHeaders)
            && exchange.ResponseHeaders is { } headers)
        {
            if (originalHeaders is not null)
                foreach (var (name, _) in originalHeaders)
                    response.Headers.RemoveHeader(name);

            foreach (var (name, value) in headers)
            {
                response.Headers.RemoveHeader(name);
                response.Headers.AddHeader(name, value);
            }

            Log.Information("Response {Id} headers rewritten: {Before} became {After} header(s).",
                exchange.Id, originalHeaders?.Count ?? 0, headers.Count);
        }

        if (!ReferenceEquals(exchange.ResponseBody, originalBody))
        {
            e.SetResponseBody(exchange.ResponseBody ?? []);
            Log.Information("Response {Id} body rewritten: {Before} bytes became {After} bytes.",
                exchange.Id, originalBody?.Length ?? 0, exchange.ResponseBody?.Length ?? 0);
        }

        _exchangeCompleted(exchange);
    }

    private void CheckCaptureWindow(Exchange exchange)
    {
        if (Volatile.Read(ref _captureWindow) is not { } window) return;

        bool closes;
        try
        {
            closes = window.StopWhen(exchange);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The armed capture window threw while testing {Method} {Url}. "
                + "Recording continues.", exchange.Method, exchange.Url);
            return;
        }

        if (!closes) return;
        if (Interlocked.CompareExchange(ref _captureWindow, null, window) != window) return;

        _captureEnabled = false;
        Interlocked.Exchange(ref _windowClosingExchangeId, exchange.Id);

        Log.Information(
            "Capture window armed by {Actor} closed on {Method} {Url} (exchange {Id}): "
            + "{Description}. Recording is now off with {Count} exchange(s) held.",
            window.ArmedBy ?? "unidentified", exchange.Method, exchange.Url, exchange.Id,
            window.Description, _capture.Count);
    }

    private static void WriteRequestChangesBack(
        Exchange exchange,
        SessionEventArgs e,
        string originalUrl,
        IReadOnlyList<(string Name, string Value)> originalHeaders,
        byte[]? originalBody)
    {
        var request = e.HttpClient.Request;

        if (!string.Equals(exchange.Url, originalUrl, StringComparison.Ordinal))
        {
            request.Url = exchange.Url;

            if (Uri.TryCreate(exchange.Url, UriKind.Absolute, out var target))
            {
                request.Headers.RemoveHeader("Host");
                request.Headers.AddHeader("Host", target.Authority);
            }

            Log.Information("Request {Id} redirected from {From} to {To}.",
                exchange.Id, originalUrl, exchange.Url);
        }

        if (!ReferenceEquals(exchange.RequestHeaders, originalHeaders))
        {
            foreach (var (name, _) in originalHeaders)
                request.Headers.RemoveHeader(name);

            foreach (var (name, value) in exchange.RequestHeaders)
            {
                request.Headers.RemoveHeader(name);
                request.Headers.AddHeader(name, value);
            }

            Log.Information("Request {Id} headers rewritten: {Before} became {After} header(s).",
                exchange.Id, originalHeaders.Count, exchange.RequestHeaders.Count);
        }

        if (!ReferenceEquals(exchange.RequestBody, originalBody))
        {
            e.SetRequestBody(exchange.RequestBody ?? []);
            Log.Information("Request {Id} body rewritten: {Before} bytes became {After} bytes.",
                exchange.Id, originalBody?.Length ?? 0, exchange.RequestBody?.Length ?? 0);
        }
    }

    private static List<(string Name, string Value)> ReadHeaders(IEnumerable<HttpHeader> headers)
    {
        var result = new List<(string Name, string Value)>();
        foreach (var header in headers)
            result.Add((header.Name, header.Value));
        return result;
    }

    private static bool ShouldCaptureExchange(string? contentType, long contentLength)
    {
        if (contentType is not null
            && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(contentType))
            return contentLength is > 0 and <= MaxBodyBytes;

        var bareType = contentType.Split(';')[0].Trim();
        if (!CapturedContentTypes.Any(
                candidate => bareType.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)))
            return false;

        return contentLength <= MaxBodyBytes;
    }

    private static (byte[] Body, bool WasTruncated) Truncate(byte[] body) =>
        body.Length <= MaxBodyBytes ? (body, false) : (body[..MaxBodyBytes], true);
}

/// <summary>An armed stop condition and the context needed to explain it.</summary>
internal sealed record CaptureWindowState(Func<Exchange, bool> StopWhen, string Description, string? ArmedBy);
