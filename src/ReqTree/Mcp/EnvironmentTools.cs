using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ReqTree.Proxy;

namespace ReqTree.Mcp;

/// <summary>
/// Turning named sets of scripts on and off together.
/// </summary>
/// <remarks>
/// An environment owns a list of scripts and nothing else — a script can express anything a rule
/// can, so there is no second collection to keep in step. Adding one is passing an environment name
/// to add_script; the environment is created on first use.
///
/// Environment scripts run before standalone ones, so a set assembled for the work in hand gets
/// first say. Nothing is forbidden after it: a later script may still change what an environment
/// script decided, and the log warns when it does.
/// </remarks>
[McpServerToolType]
public static class EnvironmentTools
{
    [McpServerTool(Name = "list_environments")]
    [Description(
        "The environments that exist, in the order they run, the scripts in each, and whether each "
        + "is switched on. Environment scripts run before standalone ones.")]
    public static string ListEnvironments(CaptureProxy proxy)
    {
        if (proxy.Environments.Count == 0)
            return "No environments. Every script is standalone. Pass an environment name to "
                 + "add_script to start one - it is created on first use.";

        var report = new StringBuilder(
            $"{proxy.Environments.Count} environment(s), their scripts running in this order and "
            + "all before the standalone scripts:\n");

        var position = 0;

        foreach (var environment in proxy.Environments)
        {
            position++;

            report.AppendLine(
                $"  {position}. {environment.Name} - {environment.Scripts.Count} script(s), "
                + $"{(environment.Enabled ? "ENABLED" : "disabled")}. "
                + $"Created by {environment.AddedBy ?? "unidentified"} at {environment.AddedAt:HH:mm:ss}.");

            foreach (var script in environment.Scripts)
                report.AppendLine($"       {script.Name}{(script.Enabled ? "" : " (disabled)")} "
                    + $"on {script.Hook} - ran {script.RunCount} time(s), threw {script.ErrorCount}.");
        }

        report.Append($"Then {proxy.Scripts.Count} standalone script(s). "
            + $"All {proxy.Rules.Count} rule(s) run before any of this - rules are never in an "
            + "environment, because a script can do everything a rule can.");

        return report.ToString();
    }

    [McpServerTool(Name = "set_environment_enabled")]
    [Description(
        "Switch a whole environment on or off. Disabling skips all its scripts on every request "
        + "without touching them, so they keep their counts and come back exactly as they were. "
        + "This is the reversible way to put a set of behaviour aside.")]
    public static string SetEnvironmentEnabled(
        CaptureProxy proxy,
        [Description("The environment name.")] string environment,
        [Description("True to run it again, false to skip it.")] bool enabled,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (proxy.SetEnvironmentEnabled(environment, enabled, who) is not { } scripts)
            return $"There is no environment called '{environment}'. "
                 + "Call list_environments to see what exists.";

        return $"{(enabled ? "Enabled" : "Disabled")} environment '{environment}' as {who}: "
             + $"{scripts} script(s) {(enabled ? "will run again" : "are now skipped entirely")}.";
    }

    [McpServerTool(Name = "unload_environment")]
    [Description(
        "Remove an environment and every script in it. Unlike disabling, this discards them - they "
        + "would have to be added again. Traffic they were acting on goes back to passing "
        + "through untouched.")]
    public static string UnloadEnvironment(
        CaptureProxy proxy,
        [Description("The environment name.")] string environment,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (proxy.RemoveEnvironment(environment, who) is not { } scripts)
            return $"There is no environment called '{environment}'. "
                 + "Call list_environments to see what exists.";

        return $"Unloaded environment '{environment}' as {who}, taking {scripts} script(s) with "
             + $"it. {proxy.Environments.Count} environment(s) left.";
    }
}
