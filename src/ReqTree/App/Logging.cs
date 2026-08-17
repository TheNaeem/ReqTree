using Serilog;
using Serilog.Events;

namespace ReqTree.App;

/// <summary>
/// Sets up the one logger the rest of the program writes to through Serilog's static
/// <see cref="Log"/>.
/// </summary>
/// <remarks>
/// Static rather than injected on purpose: ReqTree is a single process, every class in it wants to
/// log, and threading a logger through constructors to reach a proxy event handler buys nothing.
/// The thing to know is that Serilog's <see cref="Log.Logger"/> starts out as a silent logger, so
/// anything logged before <see cref="Configure"/> runs is dropped without a word — which is why it
/// is the first statement in Program.
/// </remarks>
public static class Logging
{
    /// <summary>
    /// Console for the person watching, a rolling file for the incident they only notice later.
    /// </summary>
    public static void Configure()
    {
        // The file half is attempted separately and allowed to fail. This runs before anything
        // else in the program and outside its error handling, so an unwritable data directory —
        // a locked-down machine, a full disk, a roaming profile that has not arrived — would
        // otherwise kill ReqTree with a raw stack trace from a static constructor, at the one
        // moment when there is no logger to explain it. Console logging alone is enough to run.
        string? fileProblem = null;
        string? logDirectory = null;

        try
        {
            logDirectory = DirectoryManager.LogsDirectory;
        }
        catch (Exception ex)
        {
            fileProblem = ex.InnerException?.Message ?? ex.Message;
        }

        Log.Logger = Build(logDirectory).CreateLogger();

        if (fileProblem is not null)
            Log.Warning(
                "Logging to file is unavailable ({Reason}). ReqTree will run, but get_logs will "
                + "have nothing to read and this session leaves no record on disk.", fileProblem);
    }

    private static LoggerConfiguration Build(string? logDirectory)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()

            // ASP.NET Core narrates every request over five lines at Information, and a session
            // doing real work makes a tool call every few seconds. Left alone it buries ReqTree's
            // own log in HTTP plumbing. Warnings and above still come through, which is the part
            // worth reading. Overrides match on the logger's category name, so this covers the
            // whole host; the equivalent filter on the MEL pipeline does not reach these sinks.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions", LogEventLevel.Warning)
            .WriteTo.Console(
                // Short timestamp only. This is a foreground tool a user is watching, and the date
                // is never in question when the run started thirty seconds ago.
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                // Warnings and errors go to stderr so they can be redirected away from, or kept
                // apart from, ReqTree's ordinary output.
                standardErrorFromLevel: LogEventLevel.Warning);

        // No directory means it could not be created, so there is nowhere to write and the console
        // sink above is all this logger gets.
        if (logDirectory is null) return configuration;

        return configuration
            .WriteTo.File(
                // Serilog appends the date where the dash is: reqtree-20260815.log.
                path: Path.Combine(logDirectory, "reqtree-.log"),
                rollingInterval: RollingInterval.Day,
                // A week is long enough to look into "it broke on Tuesday" and short enough that
                // an unattended machine never fills up with logs nobody reads.
                retainedFileCountLimit: 7,
                // Two instances on different ports is a normal way to use ReqTree, and the default
                // sink takes the file exclusively: the second process silently rolls to
                // reqtree-20260815_001.log, so one session's story ends up split across files with
                // nothing saying which is which. Shared lets both append to the same file. It costs
                // a little speed and rules out buffering, which is a fair trade for a readable log.
                shared: true,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
    }

    /// <summary>
    /// Flushes and closes the sinks. The file sink buffers, so skipping this loses the last few
    /// events written — usually the ones explaining why the process was exiting.
    /// </summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
