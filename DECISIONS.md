# Decisions

Why things are the way they are. Check here before proposing a change to any of it — these are
settled, and several were settled the expensive way.

---

### The store is memory; SQLite is save-and-open only

The first ReqTree wrote every exchange to SQLite as it arrived and queried the database. That put a
disk round-trip in front of data that was milliseconds old, and made "find every request carrying
this token" a self-join instead of a dictionary lookup. It was reversed there, and this build starts
from the reversed position: `ExchangeStore` is the capture, and `Persistence/CaptureFile` exists
only to write a copy out and read one back.

The cost is real and accepted: **a capture is lost on exit unless someone saves it.** The benefit is
that there is no live writer whose schema has to be kept in step, no session table, no partial
state, and no ordering contract between a request row and its response row.

### Rules and scripts change traffic by changing the Exchange

A rule's action is `Action<Exchange>`. After rules and scripts run, the proxy compares `Url`,
`RequestHeaders` and `RequestBody` against what arrived and writes back the difference. Setting the
response half during the request hook means "answer this yourself".

Considered and rejected: passing the live Titanium session to every action. It would let a rule call
`Respond()` explicitly, but it leaks proxy plumbing into every rule and script and gives an LLM a
second API to learn. Block, mock and redirect all fall out of the chosen design with no separate
concept for any of them.

The consequence to know: **the comparison is by reference.** A rule must assign a new headers list,
not mutate the existing one. Mutating changes the record and not the wire.

### Rules run before scripts, both in insertion order

Rules are the declarative path — another session can read one and know what it does. A script is for
what rules cannot express, so it gets the last word rather than the first.

Order is therefore part of what a rule *means*, which rules out `HashSet` and `Dictionary` for
holding them: both reuse the slot freed by a removal, so removing one rule silently moves the next
one added into its position. `BehaviourList` is an ordered, name-keyed, copy-on-write array —
copy-on-write because every request enumerates it while adds happen a handful of times a session.

### Rules are request-phase only

Their point is deciding what happens to a request before it goes anywhere. Response-side behaviour
is what `before_response` scripts are for. If declarative response rules are ever wanted, that is a
new decision, not an oversight.

### Restoring the system proxy is ours, not Titanium's

`RestoreOriginalProxySettings()` restores only what *that instance* of `ProxyServer` replaced. A
throwaway server in a later process therefore restores nothing — **and reports success while doing
it.** The first ReqTree had this bug, cleared its own marker file afterwards, and would have left a
machine permanently pointed at a dead port with no record that anything was wrong.

`CaptureProxy` now reads `ProxyEnable` and `ProxyServer` from the registry *before* taking over,
records them in the marker file, and writes them back itself — through the same code on both the
graceful path and the crash-recovery path, so there is one mechanism and it is the tested one. The
marker is cleared only when the restore actually succeeded.

### Every tool takes an optional actor, and the log is the timeline

Several LLM sessions share one ReqTree and change things the others rely on. Rather than a second
timeline with its own storage and query language, every mutating tool logs who did what, and
`get_logs` reads the same file a person would.

This is why logging is deliberately verbose, and why `McpEndpoint` sets `Stateless = false`: the
SDK's default rebuilds the server per HTTP request and leaves `ClientInfo` null, so the fallback
from "no actor given" to "the client's own name" would silently produce "unidentified" every time.

### Environments own their rules and scripts, and run first

An environment is a real object holding its own `BehaviourList` of each. It was briefly a name
tagged onto shared rules, and that was worse: enabling meant filtering a mixed list on every
request, and unloading meant sifting two other lists for entries carrying the right label. Owning
the contents makes all three operations trivial — enabling is "iterate it or do not", unloading is
dropping it, and the hot path walks environments then the standalone lists.

Environments run **before** standalone rules and scripts, so a set assembled deliberately for the
work in hand gets first say over whatever is lying around.

That ordering only means something because **a request answered outright stops everything after
it**. Every matching rule used to run, so a standalone rule matching the same request would execute
after the environment's and overwrite its answer — the last rule won, not the first. Now, once a
rule or script sets the response half during the request hook, no later rule or script runs on that
exchange. Header edits and redirects still stack; only answering is final, which is right, because
the request is by then decided.

The markdown environment-file format from the first ReqTree is deliberately not carried over.

### Scripts compile at add-time and are probe-run before installing

Source that does not compile comes back with the compiler's own errors and line positions. Source
that compiles but throws is run once against a sample exchange and rejected there. Both mean a
session finds out at the tool call rather than on requests it cannot observe.

Scripts run inline on the proxy thread, not via `Task.Run`. The first ReqTree queued script work to
the thread pool and blocked pool threads waiting on it, which starved the pool under concurrency and
made healthy scripts look like they were timing out.

### Four ways to query, and no more

Everything, a time window, the last N, and a keyword search. Narrower selection is the LLM's job: it
can read a hundred summary lines and pick the three that matter better than a query language can.
Only `get_exchange_detail` returns bodies, one exchange at a time, because a few hundred exchanges
hold more body text than a context window.

### No value/token extractor

The first ReqTree indexed values that looked like tokens and tracked where each appeared. It is not
carried over: `search_exchanges` with `search_in=all` answers the same question — "every exchange
carrying this value" — against a store small enough that scanning it is free. Revisit if captures
get large enough for the scan to hurt, or if *discovering* which values are interesting (rather than
following one you already have) turns out to matter.

### No breakpoints

Considered and dropped. A breakpoint that holds a request mid-flight blocks a proxy thread and the
client until an LLM notices and releases it, which means polling and a timeout. And as the owner
observed, it does not let you do anything rules and scripts cannot already do — the only unique
capability is an interactive pause, which is exactly the part that fits an LLM badly.

### net10.0 and Titanium 5.x

Titanium.Web.Proxy 5.x ships a `net10.0` target only, and 5.x is where WebSocket, HTTP/2 and HTTP/3
support lives. Choosing it before the proxy layer was built avoided rewriting that layer later.

`EnableHttp2` is explicitly set to **false**, because 5.x defaults it on and the capture hooks only
handle HTTP/1.1 framing. Turning it on means capturing traffic we cannot read. That is the flag to
revisit when h2 capture is actually built.

### Titanium logs through Serilog

5.x replaced `ExceptionFunc` with a `Microsoft.Extensions.Logging` pipeline and, left alone, writes
its own coloured output to the console. Its `LoggerFactory` is pointed at Serilog so proxy failures
land in the same file as everything else.

Related trap: `builder.Logging.AddFilter(...)` on the ASP.NET pipeline does **not** reach Serilog's
sinks. Host verbosity is controlled by `MinimumLevel.Override` in `App/Logging.cs`.
