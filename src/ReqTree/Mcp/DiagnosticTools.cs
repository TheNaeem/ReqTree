using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using ReqTree.App;
using Serilog;

namespace ReqTree.Mcp;

/// <summary>
/// Reading ReqTree's own log.
/// </summary>
/// <remarks>
/// This is the whole coordination story, and it is deliberately just the log. Every mutating tool
/// already records who did what, so rather than a second timeline with its own storage and its own
/// query language, a session that wants to know what happened reads the same lines a person would.
///
/// It is also what makes the verbose logging worth having: a rule matching, a script throwing, a
/// header being rewritten, another session stopping the proxy — all of it is already written down,
/// and this is how an LLM gets at it when something is not behaving as it expects.
/// </remarks>
[McpServerToolType]
public static class DiagnosticTools
{
    [McpServerTool(Name = "get_logs")]
    [Description(
        "Read the tail of ReqTree's log. Everything ReqTree does is in here: which session started "
        + "or stopped the proxy, every rule that matched and what it did, every script run and any "
        + "that threw, and every request that was rewritten, redirected or answered locally.\n\n"
        + "Call this first when something is not behaving as you expect - the answer is usually "
        + "already written down. Use 'contains' to narrow it, for example to a rule's name, an "
        + "exchange id, or another session's actor name.")]
    public static string GetLogs(
        [Description("How many lines from the end to return. Defaults to 100.")] int lines = 100,
        [Description("Only return lines containing this text. Case-insensitive.")] string? contains = null,
        [Description("Only return lines at this level or above: verbose, debug, information, warning, error.")]
        string? min_level = null)
    {
        if (lines <= 0) return "lines must be greater than zero.";

        // The two most recent files rather than today's by name. A session that started yesterday
        // evening and is still running at half past midnight has everything worth reading in
        // yesterday's file, and asking for today's would answer "nothing has been logged" while
        // the log sat right there. Names are yyyyMMdd, so they sort chronologically.
        string[] files;

        try
        {
            files = [.. Directory.GetFiles(DirectoryManager.LogsDirectory, "reqtree-*.log")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .TakeLast(2)];
        }
        catch (Exception ex)
        {
            return $"Could not list the log directory {DirectoryManager.LogsDirectory}: {ex.Message}";
        }

        if (files.Length == 0)
            return $"No log files in {DirectoryManager.LogsDirectory}. Nothing has been logged yet.";

        var collected = new List<string>();

        foreach (var file in files)
        {
            try
            {
                // Opened with full sharing because Serilog holds the same file open for writing.
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                collected.AddRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (Exception ex)
            {
                return $"Could not read the log at {file}: {ex.GetType().Name}: {ex.Message}";
            }
        }

        var all = collected.ToArray();

        // Checked before filtering. An unrecognised level used to fall through to "no filter",
        // which returned every line while the caller believed it had asked for errors only.
        var (levelKnown, tags) = LevelTags(min_level);

        if (!levelKnown)
            return $"'{min_level}' is not a level I know. Use verbose, debug, information, "
                 + "warning or error.";

        IEnumerable<string> selected = all;

        if (!string.IsNullOrWhiteSpace(contains))
            selected = selected.Where(line =>
                line.Contains(contains, StringComparison.OrdinalIgnoreCase));

        if (tags is not null)
            selected = AtLevel(selected, tags);

        var matched = selected.ToArray();
        var tail = matched.Length <= lines ? matched : matched[^lines..];

        if (tail.Length == 0)
            return $"Nothing in the log matched. {all.Length} line(s) were searched"
                 + (string.IsNullOrWhiteSpace(contains) ? "." : $" for '{contains}'.");

        // "read" rather than "logged today": this reads the two most recent files, so a session
        // running past midnight is looking at two days at once.
        var report = new StringBuilder(
            $"{tail.Length} line(s) of {matched.Length} matching, out of {all.Length} read from "
            + $"{files.Length} log file(s):\n");

        foreach (var line in tail) report.AppendLine(line.TrimEnd('\r'));

        return report.ToString().TrimEnd();
    }

    [McpServerTool(Name = "log_note")]
    [Description(
        "Write a note into ReqTree's log. Use it to tell other sessions what you are doing and "
        + "why, especially before changing shared state - another session reading get_logs will "
        + "see it in order alongside everything that actually happened.")]
    public static string LogNote(
        [Description("What you want on the record.")] string note,
        [Description(Actor.Description)] string? actor = null,
        McpServer? mcpServer = null)
    {
        var who = Actor.Resolve(actor, mcpServer);

        // Flattened for the same reason actor names are: a note with a newline in it would end its
        // log line and start one that reads like a genuine entry, letting a session write history
        // it did not perform. Long notes are kept whole — only the line breaks go.
        var flattened = new string([.. note.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();

        if (flattened.Length == 0)
            return "That note is empty once line breaks are removed, so nothing was recorded.";

        Log.Information("Note from {Actor}: {Note}", who, flattened);

        return $"Recorded as {who}. Other sessions will see it in get_logs.";
    }

    /// <summary>
    /// The level tag as Serilog actually writes it: after the timestamp and offset, at the start
    /// of the line.
    /// </summary>
    /// <remarks>
    /// Anchored, not searched for. Looking for "[WRN]" anywhere in the line meant a note or an
    /// actor name containing that text passed a level filter it should not have — and both come
    /// from an LLM, so a session could put its own INFO line into another session's error-only
    /// view. Anchoring makes the level something only ReqTree can set.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex LevelLine = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[(?<level>[A-Z]{3})\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Keeps lines at or above a level, and the continuation lines that belong to them.
    /// </summary>
    /// <remarks>
    /// An exception is written as a header line followed by its stack trace, and those trailing
    /// lines carry no timestamp of their own. Filtering them out would leave an error message with
    /// its explanation removed, which is the opposite of what someone asking for errors wants.
    /// </remarks>
    private static IEnumerable<string> AtLevel(IEnumerable<string> lines, string[] tags)
    {
        var keepingCurrent = false;

        foreach (var line in lines)
        {
            var match = LevelLine.Match(line);

            if (match.Success)
                keepingCurrent = tags.Contains($"[{match.Groups["level"].Value}]");

            if (keepingCurrent) yield return line;
        }
    }

    /// <summary>
    /// The three-letter tags Serilog writes, from the given level upwards.
    /// </summary>
    /// <returns>
    /// Whether the level was understood, and the tags to keep. Null tags mean "everything", which
    /// is a different answer from "I did not recognise that" — conflating the two is what let an
    /// unknown level quietly return the whole log.
    /// </returns>
    private static (bool Known, string[]? Tags) LevelTags(string? minimum) =>
        minimum?.Trim().ToLowerInvariant() switch
        {
            null or "" => (true, null),
            "verbose" or "trace" => (true, null),
            "debug" => (true, ["[DBG]", "[INF]", "[WRN]", "[ERR]", "[FTL]"]),
            "information" or "info" => (true, ["[INF]", "[WRN]", "[ERR]", "[FTL]"]),
            "warning" or "warn" => (true, ["[WRN]", "[ERR]", "[FTL]"]),
            "error" => (true, ["[ERR]", "[FTL]"]),
            "fatal" or "critical" => (true, ["[FTL]"]),
            _ => (false, null),
        };
}
