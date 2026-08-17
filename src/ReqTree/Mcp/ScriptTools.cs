using System.ComponentModel;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using ModelContextProtocol.Server;
using ReqTree.Proxy;
using ReqTree.Proxy.Objects;

// Roslyn has a Script type of its own, and this file mentions both. Ours is the one worth the
// short name here, since it is what the tools actually build.
using Script = ReqTree.Proxy.Objects.Script;

namespace ReqTree.Mcp;

/// <summary>What a script can see. One exchange, named so a script can just say <c>exchange</c>.</summary>
public sealed class ScriptGlobals
{
    public required Exchange exchange { get; init; }
}

/// <summary>
/// Adding, listing and removing scripts.
/// </summary>
/// <remarks>
/// The escape hatch for what the rule vocabulary cannot express. Source arrives as text, is
/// compiled here, and the resulting delegate is handed to the proxy — so a script that does not
/// compile is rejected at the tool call, with the compiler's own errors, rather than failing
/// silently on every request afterwards.
/// </remarks>
[McpServerToolType]
public static class ScriptTools
{
    /// <summary>
    /// Compiled once per script, not per request. The references are what a script is allowed to
    /// reach without fully qualifying names.
    /// </summary>
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Exchange).Assembly)
        .WithImports(
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "ReqTree.Proxy.Objects");

    [McpServerTool(Name = "add_script")]
    [Description(
        "Add a C# script that runs on every exchange at one hook. Scripts run after all rules, "
        + "in the order they were added. Adding one with an existing name replaces it.\n\n"
        + "The script body has a variable 'exchange' in scope. Reading it tells you about the "
        + "traffic; assigning to it changes the traffic. On before_request, setting "
        + "exchange.StatusCode and exchange.ResponseBody answers the request without it going "
        + "upstream; changing exchange.Url redirects it. Headers and bodies must be ASSIGNED, not "
        + "edited in place, or the change is recorded but never reaches the wire.\n\n"
        + "Example: if (exchange.Url.Contains(\"/track\")) { exchange.StatusCode = 204; "
        + "exchange.ResponseBody = Array.Empty<byte>(); }\n\n"
        + "Scripts are not sandboxed. What is guaranteed is only that one that throws cannot "
        + "break traffic - it is caught, logged, and the next one runs - and that one which never "
        + "returns is abandoned after timeout_ms instead of holding the request open forever.")]
    public static async Task<string> AddScript(
        CaptureProxy proxy,
        [Description("A short name for this script. Reused to remove it, and appears in every log line it produces.")]
        string script_name,
        [Description("Which hook it runs at: before_request or before_response.")]
        string hook,
        [Description("The C# body. 'exchange' is in scope. No class or method wrapper needed.")]
        string code,
        [Description(
            "Group this script into a named environment, so it can be enabled, disabled or "
            + "removed together with everything else carrying that name.")]
        string? environment = null,
        [Description(
            "How long this script may run on one exchange, in milliseconds. Defaults to 5000. "
            + "Raise it for a script that legitimately does heavy work on large bodies. 0 means no "
            + "limit and runs the script inline - only use it if you are certain the code always "
            + "returns, because an endless loop then holds that request open for good.")]
        int timeout_ms = 5000,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (string.IsNullOrWhiteSpace(script_name))
            return "A script needs a name, so that you and other sessions can refer to it later.";

        if (string.IsNullOrWhiteSpace(code))
            return "A script needs a body.";

        if (timeout_ms < 0)
            return "timeout_ms cannot be negative. Use 0 for no limit, or leave it out for the "
                 + "default of 5000.";

        ProxyHook target;

        switch (hook.Trim().ToLowerInvariant())
        {
            case "before_request": target = ProxyHook.BeforeRequest; break;
            case "before_response": target = ProxyHook.BeforeResponse; break;
            default:
                return $"'{hook}' is not a hook I know. Use before_request or before_response.";
        }

        var compiled = CSharpScript.Create(code, Options, typeof(ScriptGlobals));
        var diagnostics = compiled.Compile();

        // Errors come back verbatim, with line and column. A session that wrote a script that does
        // not compile needs the compiler's account of why, not a summary of it.
        var errors = diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        if (errors.Length > 0)
        {
            var report = new StringBuilder(
                $"Script '{script_name}' did not compile, so it was not added. "
                + $"{errors.Length} error(s):\n");

            foreach (var error in errors)
                report.AppendLine($"  {error.Id} at {error.Location.GetLineSpan().StartLinePosition}: "
                    + error.GetMessage());

            return report.ToString().TrimEnd();
        }

        var runner = compiled.CreateDelegate();

        // Proved to run before it is installed, so a script that throws on its very first call
        // fails here, where the session can see it, rather than on a request it cannot observe.
        // A script with no timeout still gets probed against one. Otherwise "no limit" would mean
        // add_script never returns, which is not a trade anyone would choose knowingly.
        var probeTimeout = timeout_ms > 0
            ? TimeSpan.FromMilliseconds(timeout_ms)
            : Script.DefaultTimeout;

        var probe = await ProbeAsync(runner, target, probeTimeout);

        if (probe.TimedOut)
            return $"Script '{script_name}' was still running after "
                 + $"{probeTimeout.TotalMilliseconds:F0}ms against a single sample exchange, so it "
                 + "was NOT added.\n\n"
                 + "That almost always means a loop with no way out. Every real request would have "
                 + "hit the same wall, so it is refused here rather than installed and disabled on "
                 + "the first piece of traffic.\n\n"
                 + "The thread running it cannot be stopped and will use CPU until ReqTree is "
                 + "restarted - so fix the loop rather than retrying with a bigger timeout_ms, "
                 + "unless the script genuinely needs longer than this on one exchange.";

        if (probe.Error is { } failure)
            return $"Script '{script_name}' compiled but threw when run against a sample "
                 + $"exchange, so it was not added: {failure}\n\n"
                 + "The sample is a POST to http://reqtree.invalid/probe?sample=1 with a Host and "
                 + "Content-Type header and a small JSON body"
                 + (target is ProxyHook.BeforeResponse
                     ? ", plus a 200 response with two headers and the same body."
                     : ", and no response half - that is what a before_request script sees.")
                 + " If your script assumes a particular header or body shape, guard it: it has to "
                 + "cope with every exchange that goes through, not only the one you have in mind.";

        var replaced = proxy.AddScript(new Script
        {
            Name = script_name.Trim(),
            Hook = target,
            AddedBy = who,
            Source = code,
            Timeout = TimeSpan.FromMilliseconds(timeout_ms),

            // The delegate itself is synchronous: a script is ordinary code, so this await completes
            // without ever yielding. Whether it is given a thread of its own is the proxy's decision,
            // made from Timeout, not this one's.
            Run = exchange => runner(new ScriptGlobals { exchange = exchange })
                .GetAwaiter().GetResult(),
        }, string.IsNullOrWhiteSpace(environment) ? null : environment.Trim());

        var onHook = proxy.AllScripts.Count(s => s.Script.Hook == target);
        var warning = proxy.IsRunning
            ? ""
            : " Note that the proxy is stopped, so nothing is passing through for it to run on.";

        var where = string.IsNullOrWhiteSpace(environment)
            ? "It is standalone, so it runs after every environment."
            : $"It belongs to environment '{environment.Trim()}', which runs first.";

        var limit = timeout_ms == 0
            ? " It has NO timeout and runs inline, so a loop that cannot exit will hold every "
              + "request open until ReqTree is restarted."
            : $" It is abandoned and disabled if one exchange takes it longer than {timeout_ms}ms.";

        return $"{(replaced ? "Replaced" : "Added")} script '{script_name}' as {who}, on "
             + $"{hook}. {where} {onHook} script(s) run at that hook, after all rules.{limit}{warning}";
    }

    [McpServerTool(Name = "list_scripts")]
    [Description(
        "List the scripts currently installed, in the order they run, with who added each one, "
        + "its source, and how often it has run or thrown.")]
    public static string ListScripts(CaptureProxy proxy)
    {
        // Every script, not just the standalone ones — listing only those would hide anything in an
        // environment from the tool whose whole job is to say what is installed.
        var all = proxy.AllScripts;

        if (all.Count == 0)
            return "No scripts are installed.";

        var report = new StringBuilder(
            $"{all.Count} script(s), in run order - environment scripts first, then standalone:\n");

        foreach (var (script, environment) in all)
        {
            // An environment that is switched off skips everything in it, so a script can be
            // enabled and still never run. Saying only "enabled" there would be misleading.
            var inactive = environment is not null
                && proxy.Environments.FirstOrDefault(e =>
                    e.Name.Equals(environment, StringComparison.OrdinalIgnoreCase)) is { Enabled: false };

            // Being disabled by a timeout is said as its own thing. "Disabled" alone reads like
            // somebody turned it off, and the next session would simply switch it back on and hit
            // the same runaway loop again.
            var state = script.TimeoutCount > 0 && !script.Enabled
                    ? " (DISABLED - it ran past its timeout and was abandoned)"
                : !script.Enabled ? " (disabled)"
                : inactive ? " (enabled, but its environment is disabled, so it does not run)"
                : "";

            report.AppendLine(
                $"  {script.Name}{state}"
                + (environment is null ? " [standalone]" : $" [environment: {environment}]")
                + $" on {script.Hook} - "
                + $"added by {script.AddedBy ?? "unidentified"} at {script.AddedAt:HH:mm:ss}, "
                + $"ran {script.RunCount} time(s), threw {script.ErrorCount} time(s)"
                + (script.TimeoutCount > 0 ? $", timed out {script.TimeoutCount} time(s)" : "")
                + (script.Timeout <= TimeSpan.Zero
                    ? ", no timeout."
                    : $", timeout {script.Timeout.TotalMilliseconds:F0}ms."));

            if (script.Source is { } source)
                report.AppendLine($"      {source.ReplaceLineEndings(" ")}");
        }

        return report.ToString().TrimEnd();
    }

    [McpServerTool(Name = "set_script_enabled")]
    [Description(
        "Turn a script on or off without removing it. A disabled script keeps its position and "
        + "its counts but is not run. To change what a script does, call add_script again with "
        + "the same name.")]
    public static string SetScriptEnabled(
        CaptureProxy proxy,
        [Description("The name the script was added under.")] string script_name,
        [Description("True to run it again, false to skip it.")] bool enabled,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (!proxy.SetScriptEnabled(script_name, enabled, who))
            return $"There is no script called '{script_name}'. Call list_scripts to see what is installed.";

        return $"{(enabled ? "Enabled" : "Disabled")} script '{script_name}' as {who}. "
             + $"{proxy.Scripts.Count(s => s.Enabled)} of {proxy.Scripts.Count} script(s) are now enabled.";
    }

    [McpServerTool(Name = "describe_script_format")]
    [Description(
        "How to write a script for add_script: what is in scope, what changing it does, and the "
        + "traps. Read this before writing your first one - the reference-not-mutation rule in "
        + "particular is not guessable, and getting it wrong produces a script that appears to "
        + "work and changes nothing on the wire.")]
    public static string DescribeScriptFormat() =>
        """
        A script is a plain C# statement body. No class, no method, no return value.

        IN SCOPE
          exchange   the one exchange being processed, of type Exchange.

        IMPORTED
          System, System.Collections.Generic, System.Linq, System.Text, ReqTree.Proxy.Objects.
          Anything else must be fully qualified. Scripts are NOT sandboxed - System.IO compiles
          and works. What is guaranteed is only that a script that throws cannot break traffic.

        READING
          exchange.Method, .Url, .Host, .Path, .QueryString, .HttpVersion
          exchange.RequestHeaders          IReadOnlyList<(string Name, string Value)>
          exchange.RequestBody             byte[]?      .RequestBodyText   string
          exchange.StatusCode              int?         .HasResponse       bool
          exchange.ResponseHeaders, .ResponseBody, .ResponseBodyText, .DurationMs

        CHANGING TRAFFIC - before_request
          exchange.Url = "..."                    sends the request somewhere else
          exchange.StatusCode = 204;              answers it yourself; it never goes upstream
          exchange.ResponseBody = ...             the body of that answer
          exchange.RequestHeaders = [...]         rewrites the headers actually sent
          exchange.RequestBody = ...              rewrites the body actually sent

        CHANGING TRAFFIC - before_response
          exchange.StatusCode, .ResponseHeaders, .ResponseBody
          These change what the client receives.

        THE TRAP
          Headers and bodies are compared BY REFERENCE to decide whether you changed them. You
          must ASSIGN a new list or array. Casting RequestHeaders to a List and adding to it
          alters what ReqTree records and leaves the real request untouched - it will look like
          it worked.

          Right:  exchange.RequestHeaders = [.. exchange.RequestHeaders, ("X-Mine", "1")];
          Wrong:  ((List<(string, string)>)exchange.RequestHeaders).Add(("X-Mine", "1"));

        ORDER
          Every rule runs first, then scripts in the order they were added. A script sees whatever
          the rules before it did.

        WHEN IT IS REJECTED
          Source that does not compile comes back with the compiler's errors. A script that throws
          when run once against a sample exchange is rejected too, so a script that always fails
          never gets installed.

        EXAMPLE
          if (exchange.Path.StartsWith("/api/") && exchange.RequestBodyText.Contains("debug"))
              exchange.RequestHeaders = [.. exchange.RequestHeaders, ("X-Debug", "true")];
        """;

    [McpServerTool(Name = "remove_script")]
    [Description("Remove a script by name. It stops running immediately.")]
    public static string RemoveScript(
        CaptureProxy proxy,
        [Description("The name the script was added under.")] string script_name,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (!proxy.RemoveScript(script_name, who))
            return $"There is no script called '{script_name}'. Call list_scripts to see what is installed.";

        return $"Removed script '{script_name}' as {who}. {proxy.Scripts.Count} script(s) left.";
    }

    /// <summary>
    /// Runs a freshly compiled script once against a throwaway exchange shaped like the one it
    /// will really see.
    /// </summary>
    /// <remarks>
    /// The shape matters. A before_response script reading <c>exchange.ResponseHeaders</c> is
    /// ordinary and correct, but against a request-shaped sample that property is null and the
    /// script throws — so a working script would be rejected for a fault in the test rather than
    /// in itself. The sample carries a header and a small body for the same reason: an empty one
    /// makes reasonable code look broken.
    /// </remarks>
    /// <returns>The failure, or null when it ran cleanly.</returns>
    private static async Task<ProbeResult> ProbeAsync(
        ScriptRunner<object> runner, ProxyHook hook, TimeSpan timeout)
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

        Exception? thrown = null;

        var work = Task.Run(async () =>
        {
            try
            {
                await runner(new ScriptGlobals { exchange = sample });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        // Raced against a delay rather than awaited. Without this an endless loop hangs add_script
        // itself: the tool call never returns, and the session that made it is stuck with no error
        // and nothing to read. Catching it here is also strictly better than catching it at
        // request time — the script is refused instead of installed and then disabled on the first
        // piece of real traffic.
        if (await Task.WhenAny(work, Task.Delay(timeout)) != work)
            return new ProbeResult(TimedOut: true, Error: null);

        return new ProbeResult(false, thrown is null ? null : $"{thrown.GetType().Name}: {thrown.Message}");
    }

    /// <summary>How the sample run went.</summary>
    private sealed record ProbeResult(bool TimedOut, string? Error);
}
