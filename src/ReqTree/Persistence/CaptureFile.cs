using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReqTree.Proxy;
using ReqTree.Proxy.Objects;
using Serilog;

namespace ReqTree.Persistence;

/// <summary>
/// Writes a capture to a SQLite file and reads one back.
/// </summary>
/// <remarks>
/// Save and open only. Nothing is written while traffic is flowing — the store in memory is the
/// capture, and this is how a session ends up with a copy of it that outlives the process. That is
/// what keeps the schema this small: there is no live writer to keep in step with it, no session
/// table, and no partial state to recover from.
///
/// Headers are JSON in a text column rather than a table of their own. They are only ever read
/// back whole, alongside the exchange they belong to, so normalising them would buy a join and
/// cost the ability to read the file with a single query.
/// </remarks>
public static class CaptureFile
{
    /// <summary>
    /// Bumped when the schema changes shape. Opening a newer file than we understand is refused
    /// rather than half-read, because a capture that silently loses columns is worse than one that
    /// will not open.
    /// </summary>
    private const int FormatVersion = 1;

    private const string Schema =
        """
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE exchanges (
            id                       INTEGER PRIMARY KEY,
            started_at               TEXT    NOT NULL,
            completed_at             TEXT,
            method                   TEXT    NOT NULL,
            url                      TEXT    NOT NULL,
            host                     TEXT    NOT NULL,
            path                     TEXT    NOT NULL,
            query_string             TEXT    NOT NULL,
            http_version             TEXT    NOT NULL,
            request_headers          TEXT    NOT NULL,
            request_content_type     TEXT,
            request_body             BLOB,
            request_body_truncated   INTEGER NOT NULL,
            status_code              INTEGER,
            response_headers         TEXT,
            response_content_type    TEXT,
            response_body            BLOB,
            response_body_truncated  INTEGER NOT NULL,
            response_size_bytes      INTEGER NOT NULL
        );

        CREATE INDEX exchanges_host ON exchanges (host);
        CREATE INDEX exchanges_started ON exchanges (started_at);
        """;

    /// <summary>Writes every exchange in <paramref name="store"/> to a new file.</summary>
    /// <returns>How many exchanges were written.</returns>
    public static int Save(ExchangeStore store, string path)
    {
        var exchanges = store.Snapshot();

        // Created rather than demanded. Asked to save to a folder that does not exist, SQLite
        // reports "unable to open database file" with no hint that the directory is the problem —
        // and the caller is usually an LLM relaying that to someone who cannot see the filesystem.
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        // Overwritten rather than appended to. A capture file is a snapshot of one store, and
        // opening a half-overwritten one would be worse than refusing.
        if (File.Exists(path)) File.Delete(path);

        using var connection = Connect(path);

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            schema.ExecuteNonQuery();
        }

        // One transaction for the lot. Row-at-a-time commits make saving a few thousand exchanges
        // take minutes rather than moments, because each one waits on a disk flush.
        using var transaction = connection.BeginTransaction();

        WriteMeta(connection, transaction, "format_version", FormatVersion.ToString());
        WriteMeta(connection, transaction, "saved_at", DateTimeOffset.Now.ToString("o"));
        WriteMeta(connection, transaction, "exchange_count", exchanges.Count.ToString());

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO exchanges (
                id, started_at, completed_at, method, url, host, path, query_string, http_version,
                request_headers, request_content_type, request_body, request_body_truncated,
                status_code, response_headers, response_content_type, response_body,
                response_body_truncated, response_size_bytes)
            VALUES (
                $id, $started_at, $completed_at, $method, $url, $host, $path, $query_string,
                $http_version, $request_headers, $request_content_type, $request_body,
                $request_body_truncated, $status_code, $response_headers, $response_content_type,
                $response_body, $response_body_truncated, $response_size_bytes);
            """;

        // Parameters created once and reassigned per row, rather than rebuilt each time.
        foreach (var name in new[]
        {
            "$id", "$started_at", "$completed_at", "$method", "$url", "$host", "$path",
            "$query_string", "$http_version", "$request_headers", "$request_content_type",
            "$request_body", "$request_body_truncated", "$status_code", "$response_headers",
            "$response_content_type", "$response_body", "$response_body_truncated",
            "$response_size_bytes",
        })
            insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));

        foreach (var exchange in exchanges)
        {
            insert.Parameters["$id"].Value = exchange.Id;
            insert.Parameters["$started_at"].Value = exchange.StartedAt.ToString("o");
            insert.Parameters["$completed_at"].Value = Null(exchange.CompletedAt?.ToString("o"));
            insert.Parameters["$method"].Value = exchange.Method;
            insert.Parameters["$url"].Value = exchange.Url;
            insert.Parameters["$host"].Value = exchange.Host;
            insert.Parameters["$path"].Value = exchange.Path;
            insert.Parameters["$query_string"].Value = exchange.QueryString;
            insert.Parameters["$http_version"].Value = exchange.HttpVersion;
            insert.Parameters["$request_headers"].Value = HeadersToJson(exchange.RequestHeaders);
            insert.Parameters["$request_content_type"].Value = Null(exchange.RequestContentType);
            insert.Parameters["$request_body"].Value = Null(exchange.RequestBody);
            insert.Parameters["$request_body_truncated"].Value = exchange.RequestBodyTruncated ? 1 : 0;
            insert.Parameters["$status_code"].Value = Null(exchange.StatusCode);
            insert.Parameters["$response_headers"].Value =
                exchange.ResponseHeaders is null ? DBNull.Value : HeadersToJson(exchange.ResponseHeaders);
            insert.Parameters["$response_content_type"].Value = Null(exchange.ResponseContentType);
            insert.Parameters["$response_body"].Value = Null(exchange.ResponseBody);
            insert.Parameters["$response_body_truncated"].Value = exchange.ResponseBodyTruncated ? 1 : 0;
            insert.Parameters["$response_size_bytes"].Value = exchange.ResponseSizeBytes;

            insert.ExecuteNonQuery();
        }

        transaction.Commit();

        Log.Information("Saved {Count} exchange(s) to {Path}.", exchanges.Count, path);
        return exchanges.Count;
    }

    /// <summary>Reads a capture file back into a store that behaves exactly like a live one.</summary>
    public static ExchangeStore Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"No capture file at {path}.", path);

        using var connection = Connect(path);

        var version = ReadMeta(connection, "format_version");

        if (version is null)
            throw new InvalidDataException(
                $"{path} is not a ReqTree capture file - it has no format version.");

        if (!int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed > FormatVersion)
            throw new InvalidDataException(
                $"{path} was written in format version {version}, but this build only understands "
                + $"up to {FormatVersion}. Refusing rather than reading it partially.");

        var store = new ExchangeStore();

        using var read = connection.CreateCommand();
        read.CommandText =
            """
            SELECT id, started_at, completed_at, method, url, host, path, query_string,
                   http_version, request_headers, request_content_type, request_body,
                   request_body_truncated, status_code, response_headers, response_content_type,
                   response_body, response_body_truncated, response_size_bytes
            FROM exchanges
            ORDER BY id;
            """;

        using var reader = read.ExecuteReader();

        while (reader.Read())
        {
            var exchange = new Exchange
            {
                // Kept, not reissued, so an id quoted from this capture still means the same
                // exchange the next time the file is opened. ExchangeStore moves its counter past
                // whatever it is handed, so nothing captured later collides with it.
                Id = reader.GetInt64(0),
                StartedAt = ParseTimestamp(reader.GetString(1)),
                CompletedAt = reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)),
                Method = reader.GetString(3),
                Url = reader.GetString(4),
                Host = reader.GetString(5),
                Path = reader.GetString(6),
                QueryString = reader.GetString(7),
                HttpVersion = reader.GetString(8),
                RequestHeaders = HeadersFromJson(reader.GetString(9)),
                RequestContentType = reader.IsDBNull(10) ? null : reader.GetString(10),
                RequestBody = reader.IsDBNull(11) ? null : (byte[])reader["request_body"],
                RequestBodyTruncated = reader.GetInt32(12) != 0,
                StatusCode = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                ResponseHeaders = reader.IsDBNull(14) ? null : HeadersFromJson(reader.GetString(14)),
                ResponseContentType = reader.IsDBNull(15) ? null : reader.GetString(15),
                ResponseBody = reader.IsDBNull(16) ? null : (byte[])reader["response_body"],
                ResponseBodyTruncated = reader.GetInt32(17) != 0,
                ResponseSizeBytes = reader.GetInt64(18),
            };

            store.AddExchange(exchange);
        }

        Log.Information("Opened {Path}: {Count} exchange(s).", path, store.Count);
        return store;
    }

    /// <summary>
    /// Reads a timestamp written with the round-trip "o" format.
    /// </summary>
    /// <remarks>
    /// Explicitly invariant, and explicitly round-trip. This is belt and braces rather than a fix:
    /// a plain <c>DateTimeOffset.Parse</c> was checked under ar-SA, th-TH and fa-IR and read the
    /// "o" format correctly in all of them, because .NET recognises ISO 8601 whatever the current
    /// culture is. Stating the intent here means nobody has to re-establish that.
    /// </remarks>
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,

            // Pooling keeps a handle on the file past Dispose, which then breaks deleting or
            // moving it. Nothing here is hot enough for pooling to be worth that.
            Pooling = false,
        }.ToString());

        try
        {
            connection.Open();
        }
        catch
        {
            // Disposed here because the caller never receives it when this throws, so its `using`
            // never runs. Reachable: open_capture takes a path from an LLM, and a locked or corrupt
            // file fails right here.
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction,
        string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v);";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $k;";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // No meta table at all, which means this is not one of our files.
            return null;
        }
    }

    // Serialised as an array of two-element arrays rather than as tuples. System.Text.Json has no
    // useful handling for ValueTuple - it would write Item1/Item2 or nothing at all - and the flat
    // form is also readable by anything else that opens the file.
    private static string HeadersToJson(IReadOnlyList<(string Name, string Value)> headers) =>
        JsonSerializer.Serialize(headers.Select(h => new[] { h.Name, h.Value }));

    private static List<(string Name, string Value)> HeadersFromJson(string json)
    {
        var pairs = JsonSerializer.Deserialize<string[][]>(json) ?? [];
        return [.. pairs.Where(p => p.Length == 2).Select(p => (p[0], p[1]))];
    }

    private static object Null(object? value) => value ?? DBNull.Value;
}
