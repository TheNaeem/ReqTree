using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using ReqTree.Proxy;
using ReqTree.Proxy.Objects;

namespace ReqTree.Mcp;

/// <summary>
/// Adding, listing and removing rules.
/// </summary>
/// <remarks>
/// A rule is a pair of delegates, and a delegate cannot travel over MCP. So these tools take a
/// description of the condition and the action and build the pair here. The vocabulary is
/// deliberately small: anything it cannot express is what scripts are for.
/// </remarks>
[McpServerToolType]
public static class RuleTools
{
    /// <summary>
    /// A matching condition that runs on a proxy thread, so a pathological pattern must not be
    /// able to hang traffic. A quarter of a second is far longer than any sane match needs.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    [McpServerTool(Name = "add_rule")]
    [Description(
        "Add a rule that acts on requests matching a condition. Rules are evaluated on every "
        + "request, in the order they were added, and always before scripts. Adding a rule with "
        + "an existing name replaces it. "
        + "Blocking and mocking answer the request without it ever leaving the machine; "
        + "redirecting sends it somewhere else without the client knowing.")]
    public static string AddRule(
        CaptureProxy proxy,
        [Description("A short name for this rule. Reused to remove it, and appears in every log line it produces.")]
        string rule_name,
        [Description("What to test: url, host, path, method, request_body, or header.")]
        string when_field,
        [Description("How to test it: contains, equals, starts_with, ends_with, or regex.")]
        string when_operator,
        [Description("The value to test against. For 'regex', a .NET regular expression.")]
        string when_value,
        [Description(
            "What to do when it matches: block, mock, redirect, set_request_header, "
            + "remove_request_header, or redact_request_body.")]
        string action,
        [Description("Header name. Required when when_field is 'header', or for the header actions.")]
        string? header_name = null,
        [Description("Header value, for set_request_header.")]
        string? header_value = null,
        [Description("Status code to answer with, for block (default 403) and mock (default 200).")]
        int? status_code = null,
        [Description("Body to answer with, for block and mock.")]
        string? body = null,
        [Description("Content type of that body. Defaults to application/json.")]
        string? content_type = null,
        [Description("Absolute URL to send the request to instead, for redirect.")]
        string? redirect_to = null,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (string.IsNullOrWhiteSpace(rule_name))
            return "A rule needs a name, so that you and other sessions can refer to it later.";

        Func<Exchange, bool> condition;

        try
        {
            condition = BuildCondition(when_field, when_operator, when_value, header_name);
        }
        catch (ArgumentException ex)
        {
            return $"That condition could not be built: {ex.Message}";
        }

        Action<Exchange> effect;
        string describedAction;

        try
        {
            (effect, describedAction) = BuildAction(
                action, rule_name, header_name, header_value,
                status_code, body, content_type, redirect_to);
        }
        catch (ArgumentException ex)
        {
            return $"That action could not be built: {ex.Message}";
        }

        var description = $"When {when_field} {when_operator} '{when_value}', {describedAction}.";

        var replaced = proxy.AddRule(new Rule
        {
            Name = rule_name.Trim(),
            Condition = condition,
            Action = effect,
            AddedBy = who,
            Description = description,
        });

        var warning = proxy.IsRunning
            ? ""
            : " Note that the proxy is stopped, so nothing is passing through for it to act on.";

        return $"{(replaced ? "Replaced" : "Added")} rule '{rule_name}' as {who}. {description} "
             + $"There are now {proxy.Rules.Count} rule(s), evaluated in the order they were added "
             + $"and all before any script.{warning}";
    }

    [McpServerTool(Name = "set_rule_enabled")]
    [Description(
        "Turn a rule on or off without removing it. A disabled rule keeps its position and its "
        + "hit count but is not evaluated, so this is the reversible way to take one out of play. "
        + "To change what a rule does, call add_rule again with the same name.")]
    public static string SetRuleEnabled(
        CaptureProxy proxy,
        [Description("The name the rule was added under.")] string rule_name,
        [Description("True to evaluate it again, false to skip it.")] bool enabled,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (!proxy.SetRuleEnabled(rule_name, enabled, who))
            return $"There is no rule called '{rule_name}'. Call list_rules to see what is in force.";

        return $"{(enabled ? "Enabled" : "Disabled")} rule '{rule_name}' as {who}. "
             + $"{proxy.Rules.Count(r => r.Enabled)} of {proxy.Rules.Count} rule(s) are now enabled.";
    }

    [McpServerTool(Name = "list_rules")]
    [Description(
        "List the rules currently in force, in evaluation order, with who added each one and "
        + "how many requests it has matched. Worth checking before you add or remove anything, "
        + "because other sessions share this ReqTree.")]
    public static string ListRules(CaptureProxy proxy)
    {
        if (proxy.Rules.Count == 0)
            return "No rules are in force. Every request passes through untouched.";

        var report = new StringBuilder();
        report.AppendLine(
            $"{proxy.Rules.Count} rule(s), evaluated in this order, all before any script:");

        var position = 0;

        foreach (var rule in proxy.Rules)
        {
            position++;
            report.AppendLine(
                $"  {position}. {rule.Name}{(rule.Enabled ? "" : " (disabled)")} - "
                + $"{rule.Description ?? "no description"} "
                + $"Added by {rule.AddedBy ?? "unidentified"} at {rule.AddedAt:HH:mm:ss}, "
                + $"matched {rule.HitCount} time(s).");
        }

        return report.ToString().TrimEnd();
    }

    [McpServerTool(Name = "remove_rule")]
    [Description("Remove a rule by name. Traffic it was acting on goes back to passing through untouched.")]
    public static string RemoveRule(
        CaptureProxy proxy,
        [Description("The name the rule was added under.")] string rule_name,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        if (!proxy.RemoveRule(rule_name, who))
            return $"There is no rule called '{rule_name}'. Call list_rules to see what is in force.";

        return $"Removed rule '{rule_name}' as {who}. {proxy.Rules.Count} rule(s) left.";
    }

    /// <summary>
    /// Turns the described condition into the delegate the proxy will call.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because capture windows describe their condition the same way
    /// a rule does, and having two vocabularies for "when does this match" would mean an LLM
    /// learning the same thing twice and getting it wrong in one of them.
    /// </remarks>
    internal static Func<Exchange, bool> BuildCondition(
        string field, string op, string value, string? headerName)
    {
        // Read once per request from the exchange, so the field selector is resolved here rather
        // than re-parsed on every call.
        Func<Exchange, string> read = field.Trim().ToLowerInvariant() switch
        {
            "url" => exchange => exchange.Url,
            "host" => exchange => exchange.Host,
            "path" => exchange => exchange.Path,
            "method" => exchange => exchange.Method,
            "request_body" => exchange => exchange.RequestBodyText,
            "header" => string.IsNullOrWhiteSpace(headerName)
                ? throw new ArgumentException("when_field 'header' also needs header_name.")
                : exchange => exchange.RequestHeaders
                    .FirstOrDefault(h => h.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                    .Value ?? "",
            _ => throw new ArgumentException(
                $"'{field}' is not a field I can test. Use url, host, path, method, request_body "
                + "or header."),
        };

        var comparison = StringComparison.OrdinalIgnoreCase;

        return op.Trim().ToLowerInvariant() switch
        {
            "contains" => exchange => read(exchange).Contains(value, comparison),
            "equals" => exchange => read(exchange).Equals(value, comparison),
            "starts_with" => exchange => read(exchange).StartsWith(value, comparison),
            "ends_with" => exchange => read(exchange).EndsWith(value, comparison),
            "regex" => BuildRegexCondition(read, value),
            _ => throw new ArgumentException(
                $"'{op}' is not an operator I know. Use contains, equals, starts_with, ends_with "
                + "or regex."),
        };
    }

    private static Func<Exchange, bool> BuildRegexCondition(Func<Exchange, string> read, string pattern)
    {
        Regex regex;

        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"'{pattern}' is not a valid regular expression: {ex.Message}");
        }

        return exchange =>
        {
            try
            {
                return regex.IsMatch(read(exchange));
            }
            catch (RegexMatchTimeoutException)
            {
                // Treated as no match rather than allowed to propagate. A pattern that backtracks
                // badly on one body should cost that one match, not the request.
                return false;
            }
        };
    }

    /// <summary>Turns the described action into the delegate, plus wording for the log and the LLM.</summary>
    private static (Action<Exchange> Effect, string Described) BuildAction(
        string action, string ruleName, string? headerName, string? headerValue,
        int? statusCode, string? body, string? contentType, string? redirectTo)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            // The payload is built once and the same array is handed to every matching exchange.
            // That is deliberate and safe only because nothing mutates a body in place — rules and
            // scripts assign a new array, which is what the proxy detects and what the script
            // documentation insists on. In-place mutation here would rewrite the recorded body of
            // every exchange the rule ever matched.
            case "block":
            {
                var status = ValidStatus(statusCode ?? 403);
                var payload = Encoding.UTF8.GetBytes(body ?? $$"""{"blocked_by":"{{ruleName}}"}""");
                var type = contentType ?? "application/json";

                return (exchange =>
                {
                    exchange.StatusCode = status;
                    exchange.ResponseBody = payload;
                    exchange.ResponseContentType = type;
                }, $"block it with {status}");
            }

            case "mock":
            {
                var status = ValidStatus(statusCode ?? 200);
                var payload = Encoding.UTF8.GetBytes(body ?? "");
                var type = contentType ?? "application/json";

                return (exchange =>
                {
                    exchange.StatusCode = status;
                    exchange.ResponseBody = payload;
                    exchange.ResponseContentType = type;
                }, $"answer it with a mocked {status} ({payload.Length} byte body)");
            }

            case "redirect":
            {
                if (string.IsNullOrWhiteSpace(redirectTo))
                    throw new ArgumentException("redirect needs redirect_to.");

                if (!Uri.TryCreate(redirectTo, UriKind.Absolute, out _))
                    throw new ArgumentException(
                        $"redirect_to must be an absolute URL, but got '{redirectTo}'.");

                return (exchange => exchange.Url = redirectTo, $"send it to {redirectTo} instead");
            }

            case "set_request_header":
            {
                if (string.IsNullOrWhiteSpace(headerName))
                    throw new ArgumentException("set_request_header needs header_name.");
                if (headerValue is null)
                    throw new ArgumentException("set_request_header needs header_value.");

                return (exchange =>
                    // Assigned, not mutated: the proxy notices a header change by reference, so
                    // editing the existing list in place would change the record and not the wire.
                    exchange.RequestHeaders =
                    [
                        .. exchange.RequestHeaders.Where(h =>
                            !h.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase)),
                        (headerName, headerValue),
                    ],
                    $"set the {headerName} header to '{headerValue}'");
            }

            case "remove_request_header":
            {
                if (string.IsNullOrWhiteSpace(headerName))
                    throw new ArgumentException("remove_request_header needs header_name.");

                return (exchange =>
                    exchange.RequestHeaders =
                    [
                        .. exchange.RequestHeaders.Where(h =>
                            !h.Name.Equals(headerName, StringComparison.OrdinalIgnoreCase)),
                    ],
                    $"remove the {headerName} header");
            }

            case "redact_request_body":
            {
                var placeholder = Encoding.UTF8.GetBytes($"[redacted by rule {ruleName}]");
                return (exchange => exchange.RequestBody = placeholder,
                    "replace the request body before it is sent or recorded");
            }

            default:
                throw new ArgumentException(
                    $"'{action}' is not an action I know. Use block, mock, redirect, "
                    + "set_request_header, remove_request_header or redact_request_body.");
        }
    }

    /// <summary>
    /// Checks a status code is one HTTP can actually carry.
    /// </summary>
    /// <remarks>
    /// Without this, a typo becomes a malformed response line — "HTTP/1.1 0" for a status of zero —
    /// and the client reports a protocol error with nothing pointing back at the rule that caused
    /// it. Rejecting at add time turns that into a sentence the session can act on.
    /// </remarks>
    private static int ValidStatus(int status) =>
        status is >= 100 and <= 599
            ? status
            : throw new ArgumentException(
                $"status_code must be between 100 and 599, but got {status}.");
}
