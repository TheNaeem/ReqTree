# Progress

Append-only. Newest at the bottom. Never rewrite a past entry — if something turned out to be
wrong, say so in a new one.

---

## Where it stands

ReqTree captures system-wide HTTP/HTTPS traffic, lets rules and scripts change it in flight, and
exposes all of it to any number of LLM sessions over MCP. **32 tools.** Everything below has been
run against real traffic, not just compiled.

| Area | State |
|---|---|
| CLI, logging, paths | Done. Serilog to console and a rolling shared file. |
| Proxy lifecycle | Done. Start, stop, restart from MCP, crash recovery of system proxy settings. |
| Capture | Done. In-memory `ExchangeStore`, request and response halves, bodies. |
| Rules | Done. Condition + action delegates, block/mock/redirect/header/redact vocabulary. |
| Scripts | Done. C# compiled at add-time via Roslyn, probe-run before installing. |
| Environments | Done, as a shared name on rules and scripts. No file format. |
| Reading traffic | Done. All, since, recent, keyword search, detail, stats, status. |
| Persistence | Done. Save to SQLite, open alongside the live capture, `reqtree open`. |
| Coordination | Done, as `get_logs` + `log_note` over the same log a person reads. |
| Capture windows | Done. Arm a stop condition; the closing exchange is itself captured. |
| Console view | Done. |
| Buffer caps | Done. `--buffer`, `--buffer-mb`, `--stop-after`. Dropping is reported, never silent. |

## Deliberately not built

- **Value/token extractor.** `search_exchanges` with `search_in=all` answers the same question.
  See DECISIONS.md.
- **Breakpoints.** Dropped: they block a proxy thread pending an LLM poll, and add nothing rules
  and scripts cannot do. See DECISIONS.md.
- **Rules on the response hook.** Rules are request-phase; `before_response` scripts cover the
  other side.
- **Markdown environment files.** Environments are a name, not a file format.
- **WebSocket capture and CONNECT-tunnel reporting.** Titanium 5.x supports WebSockets; ReqTree
  does nothing with them, and an undecrypted tunnel is invisible rather than reported.
- **HTTP/2 capture.** `EnableHttp2` is explicitly false. The hooks only handle HTTP/1.1 framing.

## Bugs found and fixed while building, worth not reintroducing

1. **Stale system-proxy recovery silently did nothing.** Inherited verbatim from the first ReqTree.
   `RestoreOriginalProxySettings()` on a throwaway `ProxyServer` restores only what that instance
   replaced — so it no-opped, logged success, and cleared the marker file that was the only record
   anything was wrong. Fixed by recording the original registry values before taking over and
   writing them back ourselves, through the same code on the graceful and crash paths.
   **This bug is still live in the first ReqTree.**
2. **Rules and scripts logged "exchange 0".** `AddExchange` assigns the id and it ran after
   behaviour, so the most verbose lines could not be correlated to anything. The exchange is now
   added before rules run, and again after.
3. **A redirect rule logged the destination as what it matched.** The log line read
   `exchange.Url` after the action had already rewritten it. The matched URL is now captured first.
4. **`Stop()` claimed to restore system proxy settings it had never taken.**
5. **`builder.Logging.AddFilter(...)` looked like it quietened ASP.NET and did nothing** — MEL
   filters do not reach Serilog's sinks. `MinimumLevel.Override` is the working form; a comment sits
   at the tempting spot.
6. **Titanium 5.x `EnableHttp2` defaults to true**, which would hand the hooks framing they cannot
   parse. Set explicitly to false.

Found in the review that followed the buffer-cap work:

7. **Byte accounting never charged response bodies.** The response half is filled in on the *same
   object* that is already stored, so subtracting `BodyBytes(stored)` on update subtracted the new
   size and added it straight back. `--buffer-mb` would have counted request bodies only — a small
   fraction of the memory — and the ceiling would never have been reached. The store now remembers
   what it charged (`Entry.AccountedBytes`) rather than recomputing it from a reference that may
   have changed underneath.
8. **Rules and scripts did not run when recording was off**, while `stop_capture`'s own description
   promised they did. Recording is about what is *kept*; behaviour is separate. A session pausing
   capture to cut noise was silently losing its blocking rules. Exchanges are now numbered by the
   proxy rather than the store, so they still have an id to log against when unrecorded.
9. **The capture window had a real race**, under a comment claiming it did not. Two threads could
   match the same window, both stop recording and both log it. Claimed with
   `Interlocked.CompareExchange` now.
10. **A response could not be removed by a script.** The request path removed dropped headers; the
    response path only added and replaced, so removing one looked like it worked and did nothing.
11. **`--paused` and `ExchangeStore.Save` were dead.** `--paused` was parsed and never applied;
    `Save` still threw `NotImplementedException` after `CaptureFile` superseded it. Both resolved.
12. **`--save-overflow` was removed rather than implemented.** It meant "write evicted exchanges to
    disk", which contradicts the no-live-writing decision. A flag that cannot be honoured is worse
    than no flag.

Found in a second review pass, under concurrency:

13. **Exchanges were silently lost under load — and the previous pass caused it.** Moving id
    assignment to the proxy (to fix "exchange 0") meant ids are handed out at hook entry while
    `AddExchange` is called later, so under concurrency a lower id routinely arrives after a higher
    one. The rule "an id at or below the highest seen must have been dropped" then refused them. A
    60-request load test captured 56, losing about one in fifteen, silently. `ExchangeStore` now
    tracks a `_droppedWatermark` — the highest id actually discarded — instead of inferring it.
    **Serial tests could never have caught this; it took concurrent traffic.**
14. **`save_capture` to a missing directory returned `SQLite Error 14`.** Parent directories are
    created now — an LLM relaying that error to someone who cannot see the filesystem had nothing
    to work with.
15. **`get_logs` hardcoded today's date.** A session running past midnight reported "nothing has
    been logged" while everything sat in yesterday's file. It now reads the two most recent files.
16. **`CaptureFile.Describe` was dead** — public, never called.

Found across further review passes, each one re-reading code the previous pass had changed:

17. **A rewritten body could still be searched for by its old contents.** `RequestBodyText` cached
    with `??=`, but bodies are settable and rules and scripts set them. A `redact_request_body`
    rule placed after any rule matching on `request_body` — which reads that property, filling the
    cache — left the original body findable through `search_exchanges` and printed by
    `get_exchange_detail`, while the wire carried the redacted version. The cache is cleared by the
    setter now.
18. **`open_capture` did not validate the name it derived.** A file called `.reqtree` yields an
    empty stem, and an empty capture name resolves back to the *live* capture — so it reported as
    opened and every read against it silently served live traffic.
19. **`status_code` was unvalidated.** Zero or 9999 produced a malformed response line and a client
    protocol error with nothing pointing back at the rule. Now 100–599 at add time.
20. **Names were matched untrimmed**, so a rule added as `" auth "` could not be removed as
    `"auth"`. `BehaviourList` and the proxy now compare trimmed and case-insensitively.
21. **`--port` and `--mcp-port` were unvalidated and could be equal**, which failed at bind time as
    "address already in use" — pointing at ReqTree's own proxy without saying so.
22. **`Logging.Configure` ran outside all error handling.** An unwritable data directory killed
    ReqTree with a raw stack trace from a static constructor, at the one moment with no logger to
    explain it. The file sink is now optional and its failure is reported.
23. **The script probe used a request-shaped sample for both hooks**, so an ordinary
    `before_response` script reading `ResponseHeaders` hit a null and was rejected — a fault in the
    test, not the script.
24. **`get_logs` silently ignored an unknown `min_level`**, returning everything while the caller
    believed it had filtered.
25. **The log could be forged.** Actor names and notes come from an LLM and went into the log
    unfiltered; a newline ended the line early and started one that read exactly like a genuine
    entry, letting a session write history attributed to another. Both are flattened now.
26. **Level filtering matched `[WRN]` anywhere in a line**, so a note quoting an error tag appeared
    in another session's error-only view. The level is parsed where Serilog writes it.
27. **`get_all_exchanges` and `get_exchange_detail` had no size limit.** A full buffer is ~500 KB of
    listing and a captured body can be a megabyte — unusable answers arriving exactly when the
    capture is worth reading. Capped at 300 lines and 20 000 characters, both saying what was left
    out.
28. **`Stop()` did not guard the registry restore.** It is called from a `finally`, so a throw there
    left the listener up, the MCP host running and the log unflushed. Each step of Program's
    shutdown is independently guarded too.
29. **A rule action that threw was logged as "the exchange is unchanged"**, which is untrue — an
    action that fails part-way has already applied what it did first, and there is no undo.
30. **The fix for 28 introduced a false success claim**, caught the pass after: `Stop()` reported
    "system proxy settings restored" from a flag captured *before* the attempt, so a failed restore
    still logged success. This is the third time in this project that exact shape has appeared —
    see bug 1. **Report what happened, never what was attempted.**
31. **`stop_proxy` made the same claim one layer up.** Fixing the log line in `Stop()` left the
    tool's own reply still saying "settings restored" unconditionally — and that reply is what an
    LLM actually reads. It now distinguishes restored, never-taken, and could-not-restore.
32. **`stop_capture` said "traffic still flows through the proxy" even with the proxy stopped**,
    telling a session its rules were acting on live traffic when nothing was passing through.
33. **`start_proxy` reported rules "in force" using a count that included disabled ones.**
34. **`CaptureFile.Connect` leaked its connection if `Open()` threw** — the caller's `using` never
    runs because it never receives the object. Reachable through `open_capture` on a locked file.

A confirmation pass then ran the .NET analyzers at `AnalysisMode=All` — a lens nothing else in this
cycle had used — and that found three more that reading had missed:

35. **The `wininet.dll` P/Invoke had no `DefaultDllImportSearchPaths`** (CA5392). Without it the
    loader also searches the application's own directory, so a `wininet.dll` dropped beside
    reqtree.exe would be loaded instead of the system one — in a process holding the user's
    privileges and editing their proxy settings. Pinned to System32.
36. **`BuildServer` leaked its `ProxyServer` if configuration threw** after construction: the caller
    never receives it, so nothing disposes it. Split into create-then-configure with a guard.
37. **`TryStart`'s failure path shared one try between the registry restore and the server
    teardown**, so a restore failure skipped `Stop()` and `Dispose()` — leaving a half-started proxy
    still holding its listener. The same shape as 28, in the other direction. Now two independent
    attempts.

Also tidied: `Program` disposes the `CaptureProxy` rather than only stopping it, so an
`IAsyncDisposable` is no longer implemented and never used.

The remaining analyzer output is opinion, not defects: snake_case tool parameters (CA1707) are the
MCP wire format, broad `catch` (CA1031) is deliberate everywhere a rule, script or cleanup step must
not be able to break traffic, `Url` as a string (CA1056) is deliberate so a malformed URL is
recorded as sent rather than rejected, and the two remaining CA2000s are ownership transfers the
analyzer cannot see through — both were checked by hand, and checking is what found 37.

Before that, a full pass — re-reading the most-edited files, sweeping for `async void`, `.Result`,
empty catches and undisposed resources, checking every Serilog call uses a message template rather
than an interpolated string, and testing unusual orderings (rules and armed windows surviving a
proxy restart, one file opened twice, a replaced rule keeping its position) — turned up nothing new.

**The single most repeated mistake, four times over: reporting an intention rather than an
outcome.** Bug 1, 28→30, 31 and 32 are all the same shape. Anything that tells a caller what
happened to system state must be derived from state read *after* the operation.


## Test harnesses

Not in this repo — they live in the session scratchpad. Worth rebuilding here if this is picked up
again:

- an in-process harness that compiles the real sources and drives a live `CaptureProxy`, used to
  prove capture, dedup, concurrent adds, and HTTPS decryption;
- a PowerShell MCP client that does a real handshake and drives every tool, used for control,
  rules, scripts, reading, persistence, environments, windows and logs.

Three test-harness traps cost real time and would cost it again:
`$args` is an automatic variable in PowerShell; `curl` is an alias for `Invoke-WebRequest` so a
function called `Curl` never runs; and `-like "*[INF]*"` is a character class, not a substring.
Also: any curl through the proxy to an HTTPS URL needs `-k`, or the request never completes and it
looks exactly like a capture bug.

Six harnesses now, and the ones that earn their keep are the ones testing conditions the ordinary
path never reaches:

- **the concurrent load test** (60 simultaneous requests with a rule and script in force) — bug 13
  was invisible to every serial test;
- **the adversarial suite** — bad input to every tool, log-forgery attempts, a pathological regex,
  a corrupt capture file;
- **the size suite** — 350 exchanges and a 60 KB body, which is how 27 was found;
- **the multi-session suite** — two MCP clients at once, which is the premise the whole actor and
  coordination design rests on and had never been exercised until it was.

## Known limits, not bugs

- Under concurrency, exchanges are stored in the order `AddExchange` is reached rather than strict
  id order, so a listing can show ids slightly out of sequence. Timestamps are authoritative.
- Every `add_script` compiles a new Roslyn assembly and every regex rule compiles a pattern; nothing
  is unloaded. Replacing the same script hundreds of times in one session grows memory.
- If recording is turned off between a request and its response, the response is still recorded (the
  store holds the same object) but its bytes are not charged against the buffer ceiling.

## Clearing (added 2026-08-17)

Four tools, mirroring the four ways the read tools select exchanges, because "which ones do I
mean" is the same question whether you are reading them or throwing them away:
`clear_all_exchanges`, `clear_exchanges_by_count` (oldest or newest), `clear_exchanges_matching`
(same keyword and `search_in` as `search_exchanges`), `clear_exchanges_older_than`. 36 tools now.

Two things fell out of building it and are worth keeping in mind:

- Removal has to advance the dropped watermark, or a response arriving for an exchange whose
  request half was just cleared comes back as a response-only ghost. `AGENTS.md` has the detail.
- `Cleared` is counted separately from `Dropped`. Folding them together would have made every
  deliberately emptied capture look like one that had silently lost its contents.

Covered by a seventh harness (26 checks): both ends, over-large counts, keyword and age selection,
ids not restarting after a clear, and every bad argument. Two failures on the first run were the
harness reading the count out of `get_stats`, whose empty-capture reply now also mentions how many
were cleared — the tools were right both times.

## Next

- WebSocket capture — the library supports it and nothing else does this well.
- There is still no way to clear an opened (file-backed) capture other than `close_capture`, which
  is almost certainly the right trade — but the `clear_*` tools do accept a `capture` argument, so
  the asymmetry is now visible where it was not before.
