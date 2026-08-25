using Microsoft.AspNetCore.Builder;
using ReqTree.App;
using ReqTree.App.Objects;
using ReqTree.Mcp;
using ReqTree.Persistence;
using ReqTree.Proxy;
using Serilog;

// Entry point. Its whole job is: read the command line, start the pieces, wait, stop cleanly.
// Anything more interesting than that belongs in one of the classes it wires together.

// First, before anything that might want to report a problem. Serilog drops everything logged
// before this line without a word, and argument parsing is the very next thing to run.
Logging.Configure();

// Declared out here so the finally below can stop them no matter where we leave from. Nothing must
// be able to exit this program with the machine's proxy settings still pointed at us.
CaptureProxy? proxy = null;
WebApplication? mcp = null;

try
{
    var options = new ReqTreeOptions();

    if (!options.TryLoadFromArgs(args))
    {
        // TryLoadFromArgs has already said what was wrong with the arguments; this only points at
        // where the full list lives, so the two messages do not repeat each other.
        Log.Information("Run 'reqtree help' for usage.");
        return 1;
    }

    if (options.HelpRequested)
    {
        // Straight to stdout rather than through the logger: help is the program's output, not a
        // record of something that happened, and timestamping it would only make it harder to read.
        Console.WriteLine(ReqTreeOptions.HelpText);
        return 0;
    }

    proxy = new CaptureProxy(
        options.ProxyPort,
        registerAsSystemProxy: !options.NoSystemProxy,
        installCertificateTrust: !options.NoCertificateTrust,
        capture: new ExchangeStore(
            capacity: options.BufferSize,
            maxBytes: options.MaxBufferBytes,
            stopAfter: options.StopAfter))
    {
        CaptureEnabled = !options.StartPaused,
    };

    Log.Information(
        "Buffer: {Capacity} exchange(s), ~{Megabytes} MB of bodies{StopAfter}. "
        + "Past that the oldest are dropped.",
        proxy.Capture.Capacity == 0 ? "unlimited" : proxy.Capture.Capacity.ToString(),
        proxy.Capture.MaxBytes == 0 ? "unlimited" : (proxy.Capture.MaxBytes / 1024 / 1024).ToString(),
        proxy.Capture.StopAfter == 0 ? "" : $", stopping after {proxy.Capture.StopAfter}");

    // Said out loud because --paused is otherwise invisible: traffic flows, the log looks healthy,
    // and nothing explains why the capture is empty an hour later.
    if (options.StartPaused)
        Log.Warning("Recording is PAUSED (--paused). Traffic will flow and rules and scripts will "
                  + "run, but nothing is kept until start_capture is called.");

    // Before anything else: if a previous run died without restoring the machine's proxy settings,
    // undo that now. Leaving it would point every application on the machine at a port nothing is
    // listening on, which to the user looks like the internet has simply stopped working.
    if (proxy.CleanStaleState() is { } healed)
        Log.Warning("{CleanupResult}", healed);

    if (options.ConsoleView)
        proxy.ExchangeCompleted += ConsoleView.Print;

    if (options.Command is ReqTreeCommand.Open)
    {
        // A detached session: nothing is intercepted or recorded, the proxy is never started, and
        // the read tools are pointed at a saved capture instead of a live one.
        var file = options.OpenFile!;
        var full = Path.GetFullPath(file);
        var resolvedName = proxy.ResolveOpenedCaptureName(full, requestedName: null);

        // Checked before the file is read, for the same reason the open_capture tool checks it: a
        // blank or "live" label resolves back to the live capture, so a file opened under either
        // name would be unreachable through every read tool.
        if (resolvedName.Problem is CaptureNameProblem.LiveReserved)
        {
            Log.Error("'{0}' is the name of the live capture. Rename the file and open it again.", file);
            return 1;
        }

        if (resolvedName.Problem is CaptureNameProblem.Empty)
        {
            Log.Error("Could not work out a name for '{0}' - its file name is empty. Rename it.", file);
            return 1;
        }

        try
        {
            var label = resolvedName.Value!;
            var opened = CaptureFile.Open(full);

            if (!proxy.AddOpenedCapture(label, opened))
            {
                Log.Error("A capture called '{Label}' is already open.", label);
                return 1;
            }

            proxy.CaptureEnabled = false;

            Log.Information(
                "Detached session: '{Label}' holds {Count} exchange(s). Nothing is being "
                + "intercepted or recorded. Read tools take capture='{Label}'.",
                label, opened.Count, label);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open {File}: {Reason}", file, ex.Message);
            return 1;
        }
    }
    else if (!options.NoProxy)
    {
        // A proxy that will not start is not fatal: the MCP server is still worth having on its
        // own, and it exposes start_proxy so the problem can be fixed without a restart.
        if (!proxy.TryStart())
            Log.Warning("Continuing without the proxy. Fix the problem and start it again.");
    }

    // Started in every mode, including --no-proxy and open: querying is the whole point, and the
    // proxy can be brought up later from a tool without restarting.
    try
    {
        mcp = await McpEndpoint.StartAsync(proxy, options.McpPort);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Could not start the MCP server on port {Port}.", options.McpPort);
        return 1;
    }

    // Wait here until the user asks us to stop, so the proxy stays up while traffic flows.
    var stopping = new TaskCompletionSource();

    Console.CancelKeyPress += (_, e) =>
    {
        // Cancel the default "kill the process now" behaviour so the cleanup below gets to run.
        // Killed here, the system proxy would stay pointed at a port about to go silent.
        e.Cancel = true;
        stopping.TrySetResult();
    };

    // Covers what Ctrl+C does not: a closed terminal window. Stop is idempotent and takes the same
    // lock, so it racing the finally below is harmless.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => proxy.Stop();

    Log.Information("Press Ctrl+C to stop.");
    await stopping.Task;

    return 0;
}
catch (Exception ex)
{
    // Last resort. Without this an unhandled exception prints a raw stack trace and skips the
    // finally below, taking the log file's account of what led up to it with it.
    Log.Fatal(ex, "ReqTree stopped because of an unhandled error.");
    return 1;
}
finally
{
    // Each step is guarded on its own. This is the last code to run, and one of these throwing
    // must not stop the others: leaving the machine's proxy settings pointed at a port about to go
    // silent is the worst thing ReqTree can do, and losing the log that explains why is next.

    // Proxy first, so that if stopping the web host hangs, networking is already back to normal.
    // Disposed rather than merely stopped: it owns the Titanium server, and DisposeAsync is the
    // path that gives it up. Stop on its own left an IAsyncDisposable that nothing ever disposed.
    try
    {
        if (proxy is not null) await proxy.DisposeAsync();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to stop the proxy cleanly.");
    }

    try
    {
        if (mcp is not null) await mcp.StopAsync();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to stop the MCP server cleanly.");
    }

    // The last word on what this session actually got. A run that ends "0 exchanges" says plainly
    // that nothing was recorded, and one that dropped some says that too — otherwise a capped
    // buffer quietly discards most of a long session and the count still looks healthy.
    try
    {
        if (proxy is not null && proxy.Capture.TotalSeen > 0)
        {
            var store = proxy.Capture;
            var captured = store.Snapshot();

            Log.Information("Captured {Count} exchange(s), {Answered} with responses.",
                captured.Count, captured.Count(exchange => exchange.HasResponse));

            if (store.Dropped > 0)
                Log.Warning(
                    "{Dropped} of {Seen} exchange(s) were dropped to stay within the buffer caps "
                    + "and are gone. Raise --buffer or --buffer-mb to keep more of a session this "
                    + "long.", store.Dropped, store.TotalSeen);
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to summarise the capture.");
    }

    // Last, always: everything above writes to the log, so flushing has to come after all of it.
    Logging.Shutdown();
}
