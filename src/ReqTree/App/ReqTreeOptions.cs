using ReqTree.App.Objects;
using Serilog;

namespace ReqTree.App;

/// <summary>
/// The command line, stored as a set of bare flags and a dictionary of flag/value pairs, and
/// read back through getters.
/// </summary>
/// <remarks>
/// Values are written joined to their flag, "--mcp-port=9999", so the parser never holds a loose
/// value that has lost track of which flag it belonged to and never has to look ahead. Adding an
/// option means adding a getter and a line in the help text; there is no parsed state to keep in
/// sync with the arguments.
/// </remarks>
public class ReqTreeOptions
{
    private HashSet<string> _options;
    private Dictionary<string, string> _values;

    /// <summary>The verb, taken from the first argument.</summary>
    public ReqTreeCommand Command { get; private set;}

    /// <summary>The dump file to read, when the command is <see cref="ReqTreeCommand.Open"/>.</summary>
    public string? OpenFile { get; private set; }

    /// <summary>Port the capture proxy listens on.</summary>
    public int ProxyPort => IntValue("--port", fallback: 8888);

    /// <summary>Port the MCP HTTP server listens on.</summary>
    public int McpPort => IntValue("--mcp-port", fallback: 9999);

    /// <summary>When true, print a live one-line-per-request log to the console.</summary>
    public bool ConsoleView => _options.Contains("--console-view");

    /// <summary>
    /// When true, start the MCP server but leave the capture proxy down. Useful for querying an
    /// existing capture without intercepting anything, and for starting the proxy later from an
    /// MCP tool once you know what you want to record.
    /// </summary>
    public bool NoProxy => _options.Contains("--no-proxy");

    /// <summary>
    /// When true, leave the machine's proxy settings alone and only listen on the port. Clients
    /// must then be pointed at that port themselves.
    /// </summary>
    public bool NoSystemProxy => _options.Contains("--no-system-proxy");

    /// <summary>
    /// When true, generate and export the root certificate but do not add it to this machine's
    /// trust store.
    /// </summary>
    public bool NoCertificateTrust => _options.Contains("--no-cert-trust");

    /// <summary>
    /// When true, run with recording paused: traffic is proxied but nothing is recorded until
    /// capture is resumed.
    /// </summary>
    public bool StartPaused => _options.Contains("--paused");

    /// <summary>
    /// How many exchanges to hold in memory. Past this the oldest are dropped.
    /// </summary>
    /// <remarks>
    /// A system-wide proxy catches video segments and CDN assets as readily as API calls, so the
    /// buffer has to be bounded or an afternoon of browsing eats the machine. Five thousand is
    /// generous for a debugging session and cheap to hold.
    /// </remarks>
    public int BufferSize => IntValue("--buffer", fallback: 5000);

    /// <summary>Approximate memory ceiling for captured bodies, in bytes.</summary>
    public long MaxBufferBytes => IntValue("--buffer-mb", fallback: 512) * 1024L * 1024L;

    /// <summary>
    /// Stop recording once this many exchanges have been captured. Zero means no cap. Traffic
    /// still flows and rules and scripts still run; only recording stops.
    /// </summary>
    public int StopAfter => IntValue("--stop-after", fallback: 0);

    /// <summary>True when help was asked for, either as the command or as a flag.</summary>
    public bool HelpRequested =>
        Command is ReqTreeCommand.Help || _options.Contains("-h") || _options.Contains("--help");

    public ReqTreeOptions()
    {
        _options = new(StringComparer.OrdinalIgnoreCase);
        _values = new(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryLoadFromArgs(string[] args)
    {
        _options = new(StringComparer.OrdinalIgnoreCase);
        _values = new(StringComparer.OrdinalIgnoreCase);

        // Cleared too, so a failed call does not leave a file path behind from a previous one.
        OpenFile = null;

        // No arguments at all lands here too: there is no default verb, so "reqtree" on its own is
        // the same mistake as leading with a flag and gets the same answer.
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            Log.Error("The first argument must be a command: start, open, or help.");
            return false;
        }

        ReqTreeCommand? command = args[0].ToLowerInvariant() switch
        {
            "start" => ReqTreeCommand.Start,
            "open" => ReqTreeCommand.Open,
            "help" => ReqTreeCommand.Help,
            _ => null,
        };

        // Start is the enum's zero value, so an unrecognised verb would quietly become "start" and
        // put the machine behind a proxy the user never asked for. It has to be rejected here.
        if (command is null)
        {
            // A message template, not an interpolated string: the verb is whatever the user typed,
            // and Serilog treats a template hole as a value rather than as more template.
            Log.Error("Unknown command {Command}. Expected one of: start, open, help.", args[0]);
            return false;
        }

        Command = command.Value;

        if (Command is ReqTreeCommand.Open)
        {
            if (args.Length < 2 || args[1].StartsWith('-'))
            {
                Log.Error("open needs a file to read, e.g. reqtree open capture.reqtree.");
                return false;
            }

            OpenFile = args[1];
        }

        foreach (var str in args)
        {
            if (!str.StartsWith('-'))
                continue;

            if (str.Contains('='))
            {
                // Split into two, not on every '=', so a value that itself contains one survives.
                var split = str.Split('=', 2);
                _values[split[0]] = split[1];
            }
            else _options.Add(str);
        }

        // Checked here rather than left to fail at bind time. The same port for both means the
        // proxy takes it, the MCP server then cannot, and the error reads "address already in use"
        // — pointing at ReqTree's own proxy without ever saying so.
        if (ProxyPort == McpPort)
        {
            Log.Error(
                "--port and --mcp-port are both {Port}. They are two different servers and need "
                + "two different ports.", ProxyPort);
            return false;
        }

        foreach (var (flag, value) in new[] { ("--port", ProxyPort), ("--mcp-port", McpPort) })
            if (value is < 1 or > 65535)
            {
                Log.Error("{Flag} must be a port between 1 and 65535, but got {Value}.", flag, value);
                return false;
            }

        // Every numeric flag, not just the ports. A value that does not parse ("--mcp-port=808o")
        // or is out of range ("--buffer=-5", which would quietly mean "unlimited") is a usage error
        // worth saying, not a fallback worth guessing at.
        if (!ValidateNumber("--port", 1, 65535)
            | !ValidateNumber("--mcp-port", 1, 65535)
            | !ValidateNumber("--buffer", 0, int.MaxValue)
            | !ValidateNumber("--buffer-mb", 0, int.MaxValue)
            | !ValidateNumber("--stop-after", 0, int.MaxValue))
            return false;

        return true;
    }

    /// <summary>
    /// True when <paramref name="flag"/> is absent, or present and a whole number within
    /// <paramref name="min"/>..<paramref name="max"/>. Anything else is logged as a usage error.
    /// </summary>
    private bool ValidateNumber(string flag, int min, int max)
    {
        if (!_values.TryGetValue(flag, out var raw)) return true;

        if (!long.TryParse(raw, out var parsed))
        {
            Log.Error("{Flag} takes a whole number, but got '{Raw}'.", flag, raw);
            return false;
        }

        if (parsed < min || parsed > max)
        {
            Log.Error("{Flag} must be between {Min} and {Max}, but got {Value}.", flag, min, max, parsed);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The number joined to a flag, or the fallback when the flag was absent or not a number.
    /// </summary>
    private int IntValue(string flag, int fallback) =>
        _values.TryGetValue(flag, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// The whole manual, in one command.
    /// </summary>
    /// <remarks>
    /// Written for an LLM that has been told "start ReqTree" and has to choose the arguments
    /// itself, possibly from a directory that is not this repository. It leads with which mode
    /// suits which goal rather than an alphabetical list of flags, because picking the mode is the
    /// decision — the flags follow from it. Anyone can read it without the repo, without the MCP
    /// server running, and without anything else being installed.
    /// </remarks>
    public static string HelpText =>
        """
        ReqTree - system-wide HTTP/HTTPS capture proxy driven by an LLM over MCP.

        It intercepts traffic, holds it in memory, and exposes it through MCP tools. You start it
        from a terminal; the analysis happens in whatever LLM connects to it.

        WHICH MODE DO YOU WANT?

          Capture everything this machine does (the usual one):
              reqtree start
          Points the machine's proxy settings at ReqTree and trusts its root certificate, so every
          browser and app goes through it. Stop with Ctrl+C - that is what puts the settings back.

          Capture one program, without touching the machine:
              reqtree start --no-system-proxy --no-cert-trust
          Only listens. Point the client at it yourself, e.g. curl -x http://localhost:8888, and
          trust %LOCALAPPDATA%\ReqTree\reqtree-root.cer if it needs HTTPS. Nothing machine-wide
          changes, and there is no certificate prompt.

          Connect an LLM now, decide what to record later:
              reqtree start --no-proxy
          Brings up the MCP server alone. Interception can be started later with the start_proxy
          tool, without restarting.

          Read a capture saved earlier:
              reqtree open <file.reqtree>
          Nothing is intercepted or recorded; the read tools point at the file.

        OPTIONS take their value with an equals sign, e.g. --mcp-port=9000.

          --port=<n>         Port the capture proxy listens on    (default 8888)
          --mcp-port=<n>     Port the MCP HTTP server listens on  (default 9999)
          --console-view     Print a live one-line log of captured traffic
          --paused           Proxy traffic but do not record it until start_capture is called
          --buffer=<n>       Exchanges held in memory             (default 5000, 0 = unlimited)
          --buffer-mb=<n>    Approximate body memory ceiling, MB  (default 512, 0 = unlimited)
          --stop-after=<n>   Stop recording after n exchanges     (default: no limit)
          --no-proxy         Start the MCP server only; leave the capture proxy stopped
          --no-system-proxy  Listen only; do not touch the machine's proxy settings
          --no-cert-trust    Do not install the root certificate into the trust store
          -h, --help         Show this help

        CONNECTING AN MCP CLIENT

          Add a remote HTTP MCP server in your LLM client's settings:
            name:      reqtree
            transport: Streamable HTTP (some clients call this simply HTTP)
            URL:       http://127.0.0.1:9999
            headers:   none

        Any client that supports HTTP MCP can connect. There is no bridge process or client-side
        command to run. Start ReqTree before connecting, then call get_proxy_status to verify it.
        Keep the URL's port in sync with --mcp-port. The server listens on 127.0.0.1 only, so the
        client must run on this machine.

        NOTES WORTH KNOWING

          Captured traffic lives in memory and reaches disk only when save_capture is called. It is
          lost when this process exits unless somebody saved it.

          If ReqTree is killed rather than stopped, the machine's proxy settings are left pointing
          at it and the internet appears to stop working. Run reqtree start again - it detects that
          and puts them back before doing anything else.

          Data lives in %LOCALAPPDATA%\ReqTree: the root certificate, and logs\ which get_logs reads.
        """;
}
