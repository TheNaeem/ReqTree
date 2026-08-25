using System.Runtime.ExceptionServices;
using ReqTree.Proxy.Objects;

namespace ReqTree.Proxy;

/// <summary>
/// Runs scripts under the two limits ReqTree needs: one to vet new source and one for live traffic.
/// </summary>
/// <remarks>
/// Neither limit cancels a script. Arbitrary .NET code cannot be stopped safely once it does not
/// yield, so the finite runner counts are what keep one bad script from creating an unbounded
/// number of stranded threads. A completed timed run copies its changes back only after its worker
/// has finished, never while that worker is still touching the exchange.
/// </remarks>
internal static class ScriptRuntime
{
    internal const int MaxTimedRunners = 32;
    private const int MaxProbeRunners = 4;
    private static int _activeTimedRunners;
    private static int _activeProbeRunners;

    internal static ScriptRunResult Run(Script script, Exchange exchange)
    {
        if (script.Timeout <= TimeSpan.Zero)
        {
            script.Run(exchange);
            return ScriptRunResult.Completed;
        }

        if (Interlocked.Increment(ref _activeTimedRunners) > MaxTimedRunners)
        {
            Interlocked.Decrement(ref _activeTimedRunners);
            return ScriptRunResult.RunnerLimitReached;
        }

        Exception? thrown = null;
        var isolated = ExchangeSnapshot.CopyOf(exchange);
        var work = Task.Factory.StartNew(() =>
        {
            try
            {
                script.Run(isolated);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                Interlocked.Decrement(ref _activeTimedRunners);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        if (!work.Wait(script.Timeout)) return ScriptRunResult.TimedOut;

        if (thrown is not null)
            ExceptionDispatchInfo.Capture(thrown).Throw();

        ExchangeSnapshot.CopyMutableState(isolated, exchange);
        return ScriptRunResult.Completed;
    }

    internal static async Task<ScriptProbeResult> ProbeAsync(
        Func<Exchange, Task> runner, ProxyHook hook, TimeSpan timeout)
    {
        var sample = new Exchange
        {
            StartedAt = DateTimeOffset.Now.AddMilliseconds(-20),
            Method = "POST",
            Url = "http://reqtree.invalid/probe?sample=1",
            Host = "reqtree.invalid",
            Path = "/probe",
            QueryString = "?sample=1",
            HttpVersion = "1.1",
            RequestHeaders = [("Host", "reqtree.invalid"), ("Content-Type", "application/json")],
            RequestContentType = "application/json",
            RequestBody = """{"sample":true}"""u8.ToArray(),
        };

        if (hook is ProxyHook.BeforeResponse)
        {
            sample.CompletedAt = DateTimeOffset.Now;
            sample.StatusCode = 200;
            sample.ResponseHeaders = [("Content-Type", "application/json"), ("Content-Length", "15")];
            sample.ResponseContentType = "application/json";
            sample.ResponseBody = """{"sample":true}"""u8.ToArray();
            sample.ResponseSizeBytes = 15;
        }

        if (Interlocked.Increment(ref _activeProbeRunners) > MaxProbeRunners)
        {
            Interlocked.Decrement(ref _activeProbeRunners);
            return new ScriptProbeResult(
                TimedOut: false,
                Error: null,
                Rejection: "The install-time probe limit has been reached because earlier scripts did not "
                    + $"return. ReqTree permits at most {MaxProbeRunners} abandoned probes; restart "
                    + "ReqTree before trying to add another runaway script.");
        }

        Exception? thrown = null;
        var work = Task.Factory.StartNew(async () =>
        {
            try
            {
                await runner(sample);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                Interlocked.Decrement(ref _activeProbeRunners);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        if (await Task.WhenAny(work, Task.Delay(timeout)) != work)
            return new ScriptProbeResult(TimedOut: true, Error: null);

        return new ScriptProbeResult(
            TimedOut: false,
            Error: thrown is null ? null : $"{thrown.GetType().Name}: {thrown.Message}");
    }
}

/// <summary>The outcome of a runtime script invocation.</summary>
internal enum ScriptRunResult
{
    Completed,
    TimedOut,
    RunnerLimitReached,
}

/// <summary>The outcome of an install-time script probe.</summary>
internal sealed record ScriptProbeResult(bool TimedOut, string? Error, string? Rejection = null);

/// <summary>Copies exchanges across the boundary between proxy work and isolated script work.</summary>
internal static class ExchangeSnapshot
{
    internal static Exchange CopyOf(Exchange source) =>
        new()
        {
            Id = source.Id,
            StartedAt = source.StartedAt,
            Method = source.Method,
            Url = source.Url,
            Host = source.Host,
            Path = source.Path,
            QueryString = source.QueryString,
            HttpVersion = source.HttpVersion,
            RequestHeaders = [.. source.RequestHeaders],
            RequestContentType = source.RequestContentType,
            RequestBody = source.RequestBody is { } requestBody ? [.. requestBody] : null,
            RequestBodyTruncated = source.RequestBodyTruncated,
            StatusCode = source.StatusCode,
            CompletedAt = source.CompletedAt,
            ResponseHeaders = source.ResponseHeaders is { } responseHeaders
                ? [.. responseHeaders]
                : null,
            ResponseContentType = source.ResponseContentType,
            ResponseBody = source.ResponseBody is { } responseBody ? [.. responseBody] : null,
            ResponseBodyTruncated = source.ResponseBodyTruncated,
            ResponseSizeBytes = source.ResponseSizeBytes,
        };

    internal static void CopyMutableState(Exchange source, Exchange target)
    {
        target.Url = source.Url;
        target.RequestHeaders = [.. source.RequestHeaders];
        target.RequestContentType = source.RequestContentType;
        target.RequestBody = source.RequestBody is { } requestBody ? [.. requestBody] : null;
        target.RequestBodyTruncated = source.RequestBodyTruncated;
        target.StatusCode = source.StatusCode;
        target.CompletedAt = source.CompletedAt;
        target.ResponseHeaders = source.ResponseHeaders is { } responseHeaders
            ? [.. responseHeaders]
            : null;
        target.ResponseContentType = source.ResponseContentType;
        target.ResponseBody = source.ResponseBody is { } responseBody ? [.. responseBody] : null;
        target.ResponseBodyTruncated = source.ResponseBodyTruncated;
        target.ResponseSizeBytes = source.ResponseSizeBytes;
    }
}
