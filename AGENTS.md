# ReqTree — orientation for an LLM opening this repo cold

## What this is

ReqTree is a no-GUI, system-wide HTTP/HTTPS capture proxy that a user starts from the command line.
It intercepts traffic from the whole machine, holds it in memory, and exposes it to any LLM through
MCP tools over localhost HTTP. **The LLM is the user interface.** ReqTree's job is to be an
excellent data layer, not to anticipate every analysis feature — if a user asks something no tool
covers, the answer is almost always "query the captured data and reason over it", not "add a tool".

## Architecture in words

One process. `CaptureProxy` wraps Titanium.Web.Proxy, turns every request and response into an
`Exchange`, runs rules and scripts over it, and stores it in an `ExchangeStore`. An MCP server over
HTTP exposes that store to any number of LLM clients. Nothing is distributed, there is no
background service, and nothing is written to disk unless somebody asks for it.

```
Titanium proxy -> rules -> scripts -> ExchangeStore (memory)
      |                                    |    ^
      v                                    v    |
 console view                    SQLite file (save / open)
                                           |
                            MCP server (HTTP, localhost) <-> LLM clients
```

## Folder map

| Path | What lives there |
|---|---|
| `Program.cs` | Parse args, start the pieces, wait, stop cleanly. Nothing clever. |
| `App/` | `ReqTreeOptions` (CLI), `DirectoryManager` (every path we write), `Logging` (Serilog setup). |
| `Proxy/CaptureProxy.cs` | Titanium wiring, certs, system proxy, the two hooks, rules/scripts/environments/capture windows. |
| `Proxy/ExchangeStore.cs` | Where exchanges live, and every query over them. |
| `Proxy/BehaviourList.cs` | The ordered, name-keyed collection rules and scripts are held in. |
| `Proxy/ConsoleView.cs` | One line per exchange for a human watching. |
| `Proxy/Objects/` | `Exchange`, `Rule`, `Script`, `ProxyHook`. |
| `Persistence/CaptureFile.cs` | Save a store to SQLite, read one back. Save and open only. |
| `Mcp/` | `McpEndpoint` (host), `Actor` (who is calling), and one tools file per area. |
| `WinApi/` | One static class per Windows library. Raw declarations only. |

## The scope test — apply this before adding anything

**ReqTree captures traffic and manipulates it. The LLM interprets it.**

Does the change *capture* (record, index) or *manipulate* (block, mock, redirect, edit)? It belongs
here. Does it *interpret* — decide what something means, which of several things matters, or what
the user should conclude? Expose the data and let the LLM do it.

## Things that will bite you

- **The store is memory. Nothing is written live.** `CaptureFile` is only ever called by
  `save_capture` / `open_capture` and `reqtree open`. There is no background writer, and adding one
  would mean the schema has to be kept in step with a live path — which is exactly what this design
  avoids. A capture is lost on exit unless someone saved it.
- **`AddExchange` is called twice per exchange** — once when the request is seen, once when the
  response is filled in on the same object. It recognises the id and updates in place. The first
  call is what assigns the id, which is why it happens *before* rules run: otherwise every rule and
  script logs "exchange 0".
- **An exchange arriving with an id keeps it**, and `ExchangeStore` moves its counter past it. Drop
  that and traffic captured after opening a file gets ids the file already used.
- **Rules and scripts change traffic by changing the `Exchange`.** After they run, the proxy
  compares `Url`, `RequestHeaders` and `RequestBody` against what arrived and writes back the
  difference. Setting the response half during the request hook means "answer this yourself" — that
  is how block and mock work, with no separate concept for either.
- **Headers and bodies are compared by reference.** A rule or script must *assign* a new list or
  array. Mutating in place changes what is recorded and leaves the wire untouched, which looks like
  it worked. This is the single most likely thing for an LLM to get wrong; `describe_script_format`
  says so explicitly. Assigning is also what clears the decoded-text cache on `Exchange` — mutate a
  body array in place and `RequestBodyText` keeps returning the old contents, which once meant a
  redacted body was still findable through `search_exchanges`.
- **Only one ReqTree may own the system proxy, and a named semaphore enforces it.** Two started
  together used to corrupt the settings between them: the first records the real original and points
  the machine at itself, the second then reads *that* as the original and faithfully restores it on
  the way out, leaving the machine aimed at a port nothing is listening on. The second instance now
  stands down and says so. The semaphore is `Local\ReqTree.SystemProxyOwner`, and it is a semaphore
  rather than a mutex because a mutex must be released by the thread that took it — here it is
  claimed in `TryStart` and released in `Stop`, which are routinely different threads.
- **A script that never returns cannot be killed, so it is refused or disabled instead.** .NET has
  no way to interrupt code that will not yield, and `Thread.Abort` is gone. So `while (true)` is
  handled in two places: `add_script` probes every script against a sample exchange under a timeout
  and refuses one that overruns, and at request time `RunWithinTimeout` gives up waiting and
  **disables** the script. Disabling is the point — one leaked thread is survivable, one per request
  is what takes the machine down. Default five seconds, `timeout_ms` overrides it, and `0` means run
  inline with no limit for someone who is sure.
- **Clearing advances the dropped watermark, and has to.** The four `clear_*` tools remove
  exchanges deliberately. A response normally arrives as a second `AddExchange` for an exchange
  whose request half is already stored — remove that half and the response is no longer recognised,
  so it would be filed as a new exchange holding a response and no request. Every removal path
  moves `_droppedWatermark` past what it took, which is what refuses it. Cleared exchanges are
  counted in `Cleared`, never in `Dropped`: that number means "the caps are biting", and an empty
  capture reporting hundreds recorded and none dropped reads like a bug that is not there.
- **The buffer is bounded and drops the oldest.** `--buffer`, `--buffer-mb` and `--stop-after` are
  enforced by `ExchangeStore`; a store built with no arguments is unbounded, which is what an opened
  capture file gets so that loading one cannot silently discard half of it. Dropping is never
  silent: `get_proxy_status`, `get_stats` and the shutdown summary all report it.
- **Never report what was attempted — report what happened.** This has now caused the same bug three
  times: Titanium's restore claiming success while doing nothing, `Stop()` logging "settings
  restored" from a flag read before the attempt, and `stop_proxy` telling a session the machine was
  fine when the restore had failed. Any message about system proxy settings must be derived from
  state read *after* the operation.
- **Anything an LLM supplies that reaches the log must be flattened.** Actor names and notes with a
  newline in them would start a line that reads exactly like a genuine entry, letting one session
  forge history attributed to another. `Actor.Clean` does this; the level filter in `get_logs` is
  anchored to where Serilog writes the level for the same reason.
- **Order is part of what a rule means.** Rules run before scripts, each in the order added. That is
  why `BehaviourList` exists and why a `HashSet` cannot be used — it reuses the slot freed by a
  removal, so removing one rule silently moves the next one added into its position.
- **Anything touched by proxy threads needs care.** `CaptureEnabled` and the armed capture window
  are `volatile`; `Rule.HitCount` and the script counters use `Interlocked`. A plain `++` there
  undercounts silently.
- **MCP sessions must stay stateful.** `McpEndpoint` sets `Stateless = false`. The SDK defaults it
  to true, which rebuilds the server per HTTP request and leaves `ClientInfo` null, so actor
  resolution degrades to "unidentified" for every caller. Do not remove it to "take the default".
- **Never let a rule or script break traffic.** Both are wrapped: whatever they throw is caught,
  logged against their name, and the next one runs.
- **Scripts are not sandboxed and the docs must not claim they are.** The import list controls
  implicit usings only; fully-qualified `System.IO` compiles fine. What is guaranteed is only that a
  failing script cannot break traffic.
- **Serilog filters, not MEL filters.** `builder.Logging.AddFilter(...)` on the ASP.NET pipeline
  looks like it quietens the host and does nothing, because Serilog decides for itself what to
  write. Use `MinimumLevel.Override` in `App/Logging.cs`.
- **Restoring the system proxy is ours, not Titanium's.** `RestoreOriginalProxySettings()` only
  restores what its own instance replaced, so a fresh server in a new process restores nothing —
  and reports success. `CaptureProxy` records the original registry values itself and writes them
  back. This is the most damaging thing that can go wrong: a machine left pointed at a dead port
  looks to its user like the internet has stopped working.

## Logging is deliberately verbose

Every rule match, script run, rewritten header, redirect and locally-answered request is logged with
the actor who set it up and the exchange id. That is not noise — `get_logs` is the whole
coordination story, and an LLM debugging its own rules reads the same lines a person would. Do not
quieten it.

## Running it

**`reqtree help` is the manual**, and it is deliberately self-contained: which mode suits which
goal, every option, how to connect an LLM, and what to do when things go wrong. It needs no repo, no
running server and nothing else installed, so it is the right thing to point any agent at — the
`README.md` here says the same in longer form for someone who has the repository open.

Keep those two in step when the CLI changes. They are the only instructions a session that has not
connected yet can read; the MCP server instructions are unreachable until it has.

```
reqtree start                                        capture everything on this machine
reqtree start --no-system-proxy --no-cert-trust      capture one client, machine untouched
reqtree start --no-proxy                             MCP only; start the proxy later from a tool
reqtree open <file.reqtree>                          read a saved capture
reqtree help
```

Options take their value with `=`. Data lives in `%LOCALAPPDATA%\ReqTree`: the root certificate,
the proxy-state marker, and `logs/`.

## Connecting an LLM

```
claude mcp add --transport http reqtree http://localhost:9999
```

Registered once, as a URL rather than a process — it survives ReqTree restarting, and is only stale
if `--mcp-port` changes. No bridge process: ReqTree speaks Streamable HTTP, bound to loopback.

## Where the truth lives

- `PROGRESS.md` — status and the next concrete step. **Read this first.** Append-only.
- `DECISIONS.md` — why each choice was made. Check before proposing a change to the storage format,
  the proxy library, the transport, or the scripting host.

## House style

Plain, explicit code over clever abstractions. One job per file, but not one file per job — the
owner dislikes a large file count and would rather read a longer file than chase five small ones.
Comment the *why*, never the *what*. No interfaces for things with one implementation. It should
read like something one careful person wrote by hand, because the owner wants to be able to explain
every file unaided.
