using System.ComponentModel;
using ModelContextProtocol.Server;
using ReqTree.Proxy;
using ReqTree.Proxy.Objects;
using Serilog;

namespace ReqTree.Mcp;

/// <summary>
/// Starting and stopping the two things that can be turned on and off: interception, and recording.
/// </summary>
/// <remarks>
/// Every tool here says what state it found as well as what it did, because several sessions can be
/// connected at once and "already running" is a genuinely different answer from "started". A tool
/// that silently succeeds either way leaves an LLM unable to tell that someone else got there
/// first — and logs who did it, for the same reason.
/// </remarks>
[McpServerToolType]
public static class ControlTools
{
    [McpServerTool(Name = "start_proxy")]
    [Description(
        "Start intercepting traffic. Points the machine's proxy settings at ReqTree so traffic "
        + "from any application passes through it. Recording is separate - see start_capture.")]
    public static string StartProxy(
        CaptureProxy proxy,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (proxy.IsRunning)
        {
            Log.Information("{Actor} called start_proxy, but it was already running on {Port}.",
                who, proxy.Port);
            return $"The proxy was already running on port {proxy.Port}. Nothing changed.";
        }

        if (!proxy.TryStart())
        {
            Log.Warning("{Actor} called start_proxy on port {Port} and it failed.", who, proxy.Port);
            return $"Could not start the proxy on port {proxy.Port}. The reason was written to "
                 + "ReqTree's log; the usual cause is that something else already holds the port.";
        }

        Log.Information(
            "Proxy started by {Actor} on port {Port}. Recording is {State}, with {Rules} of "
            + "{TotalRules} rule(s) and {Scripts} of {TotalScripts} script(s) enabled.",
            who, proxy.Port, proxy.CaptureEnabled ? "on" : "off",
            proxy.Rules.Count(r => r.Enabled), proxy.Rules.Count,
            proxy.Scripts.Count(s => s.Enabled), proxy.Scripts.Count);

        return $"Proxy started on port {proxy.Port}. "
             + (proxy.IsSystemProxy
                 ? "The machine's proxy settings now point at ReqTree, so traffic from any "
                   + "application is passing through it."
                 : "The machine's proxy settings were left alone, so only clients pointed at this "
                   + "port explicitly will pass through it.")
             // Enabled counts, not totals. "N rules in force" counting disabled ones tells a
             // session behaviour is acting on its traffic when some of it is switched off.
             + $" Recording is {(proxy.CaptureEnabled ? "on" : "off")}, with "
             + $"{proxy.Rules.Count(r => r.Enabled)} of {proxy.Rules.Count} rule(s) and "
             + $"{proxy.Scripts.Count(s => s.Enabled)} of {proxy.Scripts.Count} script(s) enabled.";
    }

    [McpServerTool(Name = "stop_proxy")]
    [Description(
        "Stop intercepting traffic and put the machine's proxy settings back as they were. "
        + "Exchanges already captured are kept, as are rules and scripts.")]
    public static string StopProxy(
        CaptureProxy proxy,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        // Read before stopping. Afterwards it is false whether the settings were restored or were
        // never taken, and those need different answers.
        var hadSystemProxy = proxy.IsSystemProxy;

        if (!proxy.Stop())
        {
            Log.Information("{Actor} called stop_proxy, but it was already stopped.", who);
            return "The proxy was already stopped. Nothing changed.";
        }

        Log.Information("Proxy stopped by {Actor} with {Count} exchange(s) captured.",
            who, proxy.Capture.Count);

        var kept = $"The {proxy.Capture.Count} exchange(s) already captured are still here.";

        // Still set means Stop caught a failure putting the registry back. Reporting success here
        // would leave a session telling the user their machine is fine while it is not — the same
        // false claim that made Titanium's own restore dangerous.
        if (proxy.IsSystemProxy)
            return "Proxy stopped, but the machine's proxy settings could NOT be restored and "
                 + $"still point at port {proxy.Port}. Applications will lose connectivity. "
                 + "Starting the proxy again and stopping it, or restarting ReqTree, will repair "
                 + $"it; the reason is in get_logs. {kept}";

        return hadSystemProxy
            ? $"Proxy stopped and the machine's proxy settings restored. {kept}"
            : $"Proxy stopped. The machine's proxy settings were never changed. {kept}";
    }

    [McpServerTool(Name = "start_capture")]
    [Description(
        "Start recording exchanges. Traffic already flows through the proxy whether or not "
        + "recording is on; this decides whether it is kept.")]
    public static string StartCapture(
        CaptureProxy proxy,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);
        var wasEnabled = proxy.CaptureEnabled;
        proxy.CaptureEnabled = true;

        Log.Information(
            "{Actor} called start_capture. Recording was {Before}, is now on, with {Count} "
            + "exchange(s) held and the proxy {ProxyState}.",
            who, wasEnabled ? "already on" : "off", proxy.Capture.Count,
            proxy.IsRunning ? "running" : "stopped");

        var note = proxy.IsRunning
            ? ""
            : " Note that the proxy itself is stopped, so nothing is passing through ReqTree to be "
              + "recorded. Call start_proxy as well.";

        return (wasEnabled
                   ? "Recording was already on. Nothing changed."
                   : "Recording started.")
             + $" {proxy.Capture.Count} exchange(s) held so far.{note}";
    }

    [McpServerTool(Name = "capture_window")]
    [Description(
        "Record until something happens, then stop automatically. Describe the stopping condition "
        + "exactly as you would a rule's condition.\n\n"
        + "Use this when you want a clean capture of one thing: arm it, tell the user to go ahead, "
        + "and recording stops the moment the condition matches - so the capture holds the flow "
        + "you asked for rather than that plus whatever background traffic arrived while you were "
        + "deciding to call stop_capture. The matching exchange is itself captured.")]
    public static string CaptureWindow(
        CaptureProxy proxy,
        [Description("What to test: url, host, path, method, request_body, or header.")]
        string when_field,
        [Description("How to test it: contains, equals, starts_with, ends_with, or regex.")]
        string when_operator,
        [Description("The value to test against.")] string when_value,
        [Description("Header name. Required when when_field is 'header'.")] string? header_name = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        Func<Exchange, bool> condition;

        try
        {
            condition = RuleTools.BuildCondition(when_field, when_operator, when_value, header_name);
        }
        catch (ArgumentException ex)
        {
            return $"That condition could not be built: {ex.Message}";
        }

        var described = $"{when_field} {when_operator} '{when_value}'";
        proxy.ArmCaptureWindow(new CaptureProxy.CaptureWindow(condition, described, who));

        // Arming a window while recording is off would look armed and never fire, because the
        // condition is only tested against exchanges that are actually being recorded.
        var note = proxy.CaptureEnabled
            ? ""
            : " Recording is currently OFF, so nothing will be tested against it - call "
              + "start_capture or the window will never close.";

        return $"Capture window armed as {who}: recording stops when {described}. "
             + $"{proxy.Capture.Count} exchange(s) held so far.{note}";
    }

    [McpServerTool(Name = "disarm_capture_window")]
    [Description("Cancel an armed capture window. Recording carries on until you stop it yourself.")]
    public static string DisarmCaptureWindow(
        CaptureProxy proxy,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);
        var was = proxy.DisarmCaptureWindow();

        if (was is null) return "No capture window was armed. Nothing changed.";

        Log.Information("Capture window disarmed by {Actor}; it would have stopped on {Description}.",
            who, was.Description);

        return $"Disarmed the capture window as {who}. It would have stopped recording when "
             + $"{was.Description}.";
    }

    [McpServerTool(Name = "clean_stale_proxy_state")]
    [Description(
        "Undo system proxy settings left behind by a ReqTree that died without cleaning up. "
        + "This runs automatically at startup; call it if the user reports that their internet "
        + "stopped working after a crash and a restart has not been done.")]
    public static string CleanStaleProxyState(
        CaptureProxy proxy,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);
        var result = proxy.CleanStaleState();

        if (result is null)
            return "Nothing stale to clean up. No previous ReqTree left the machine's proxy "
                 + "settings pointed at itself.";

        Log.Warning("{Actor} ran clean_stale_proxy_state: {Result}", who, result);
        return result;
    }

    [McpServerTool(Name = "stop_capture")]
    [Description(
        "Stop recording exchanges. Traffic carries on flowing normally, and rules and scripts "
        + "still run; it just is not kept. Exchanges already captured remain available.")]
    public static string StopCapture(
        CaptureProxy proxy,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);
        var wasEnabled = proxy.CaptureEnabled;
        proxy.CaptureEnabled = false;

        Log.Information(
            "{Actor} called stop_capture. Recording was {Before}, is now off, with {Count} "
            + "exchange(s) held.",
            who, wasEnabled ? "on" : "already off", proxy.Capture.Count);

        // "Traffic still flows" is only true while the proxy is up. Said unconditionally it told a
        // session its rules were still acting on live traffic when nothing was passing through.
        var stillFlowing = proxy.IsRunning
            ? " Traffic still flows through the proxy, and rules and scripts still run on it; it is "
              + "just no longer kept."
            : " The proxy is also stopped, so nothing is passing through ReqTree at all.";

        return (wasEnabled
                   ? "Recording stopped." + stillFlowing
                   : "Recording was already off. Nothing changed.")
             + $" {proxy.Capture.Count} exchange(s) remain available.";
    }
}
