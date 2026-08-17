using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReqTree.Proxy;
using Serilog;

namespace ReqTree.Mcp;

/// <summary>
/// Hosts the MCP server that an LLM connects to, over HTTP on loopback.
/// </summary>
/// <remarks>
/// Streamable HTTP rather than stdio, because ReqTree is a long-running process the user starts
/// once and several sessions may talk to at the same time. Nothing has to launch it, and no bridge
/// process is needed to reach it.
/// </remarks>
public static class McpEndpoint
{
    /// <summary>
    /// Starts the server and returns once it is listening.
    /// </summary>
    /// <param name="proxy">
    /// Registered as a singleton so the tools can take it as a parameter. The SDK resolves tool
    /// parameters from the container, which keeps it out of the schema the LLM sees.
    /// </param>
    /// <param name="port">Loopback port to listen on.</param>
    public static async Task<WebApplication> StartAsync(CaptureProxy proxy, int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // Otherwise the content root is whatever directory the user happened to run reqtree
            // from, which decides where the host looks for configuration files. Pinning it to the
            // binary makes the server behave the same wherever it is started.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Loopback only, and deliberately. This endpoint can start and stop a system-wide
        // intercepting proxy on the user's machine; it has no business being reachable from
        // anywhere else on the network.
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        // ASP.NET Core brings its own logging providers. Cleared and replaced so everything ends up
        // in the same place, rather than the host writing one format and ReqTree another.
        builder.Logging.ClearProviders();
        // The host's own chatter is quietened in Logging.Configure rather than here: filters added
        // to this pipeline do not reach Serilog's sinks, because Serilog decides for itself what to
        // write. Setting it here looks like it works and does nothing.
        builder.Logging.AddSerilog();

        builder.Services.AddSingleton(proxy);

        builder.Services
            .AddMcpServer(options => options.ServerInstructions = Instructions)
            .WithHttpTransport(http =>
            {
                // The SDK defaults this to true, which gives every HTTP request a fresh server
                // context and therefore no memory of who connected. ReqTree is a single process on
                // loopback, so statelessness buys nothing here and costs the ability to tell one
                // connected session from another.
                http.Stateless = false;
            })
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.MapMcp();

        try
        {
            await app.StartAsync();
        }
        catch
        {
            // Disposed here because the caller never receives the reference when this throws, and
            // so has nothing to clean up. A busy port would otherwise leave a built host holding
            // its resources for the life of the process.
            await app.DisposeAsync();
            throw;
        }

        Log.Information("MCP server listening on http://localhost:{Port}", port);
        Log.Information("  claude mcp add --transport http reqtree http://localhost:{Port}", port);

        return app;
    }

    /// <summary>
    /// What an LLM is told about ReqTree when it connects.
    /// </summary>
    /// <remarks>
    /// This is the only orientation a session gets. It has no access to this repository, so
    /// anything it needs to know about how ReqTree works has to be said here.
    /// </remarks>
    private const string Instructions =
        """
        ReqTree is a system-wide HTTP/HTTPS capture proxy. It intercepts traffic from the whole
        machine and exposes it to you. You are the interface: ReqTree supplies the data, and you
        do the interpreting.

        Two things are separately controllable, and the difference matters:

        - The proxy is what intercepts traffic at all. While it is stopped, nothing passes
          through ReqTree and the user's applications are unaffected by it.
        - Capture is whether exchanges are recorded. While it is stopped, traffic still flows
          normally but nothing is kept.

        The usual pattern is to leave the proxy running and stop capture until the user is about
        to do the thing you want to see, so the capture holds that and not an hour of background
        noise. capture_window automates this: arm a stopping condition, tell the user to go
        ahead, and recording stops the moment it matches.

        Rules and scripts change traffic and run whether or not recording is on - stopping capture
        stops what is kept, not what ReqTree does.

        Reading: get_stats first to see whether what you want is even in there, then
        search_exchanges to narrow, then get_exchange_detail for the one you care about. Detail is
        the only tool that returns bodies. Every read tool takes a 'capture' argument; omit it for
        the live one, or name a file opened with open_capture.

        The buffer is bounded, so a long session drops its oldest exchanges. get_proxy_status says
        how many have been dropped - check it before concluding something was never captured.
        Nothing reaches disk until someone calls save_capture.

        Several sessions may be connected to the same ReqTree at once, so treat starting, stopping
        and adding rules as changes to shared state that someone else may be relying on. get_logs
        shows what everyone has done, and log_note puts your own intentions on that record.
        """;
}
