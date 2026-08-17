# ReqTree

A system-wide HTTP/HTTPS capture proxy with no GUI. It intercepts traffic from the whole machine and
exposes it to any LLM over MCP. **The LLM is the interface** — ReqTree supplies the data and the
controls; the reasoning happens on the other end.

---

## Starting it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Build once, from the
repository root:

```
dotnet build ReqTree.sln
```

The binary lands at `src\ReqTree\bin\Debug\net10.0\reqtree.exe`. Put that on your PATH if you intend
to point other tools at it, then pick a mode.

Everything below is also available from `reqtree help`, which is self-contained — no repository and
no running server needed. That is the thing to point an LLM at when it has to choose its own
arguments.

### The usual one — capture everything on this machine

```
reqtree start
```

Points the machine's proxy settings at ReqTree, installs the root certificate into the current
user's trust store on first run, and starts recording. Every browser and app on the machine goes
through it. **Ctrl+C to stop** — that is what restores the proxy settings, so use it rather than
closing the window or killing the process.

### Capture without touching the machine

```
reqtree start --no-system-proxy --no-cert-trust
```

Listens on the port and changes nothing else. Point a client at it yourself
(`curl -x http://localhost:8888`). The root certificate is still exported to
`%LOCALAPPDATA%\ReqTree\reqtree-root.cer` so you can hand it to that client. Use this when you only
want one program's traffic, or when you do not want a trust prompt.

### Just the tools, no interception yet

```
reqtree start --no-proxy
```

Brings up the MCP server alone. The proxy can be started later with the `start_proxy` tool, without
restarting. Useful when you want an LLM connected and ready before deciding what to record.

### Read a saved capture

```
reqtree open C:\path\to\capture.reqtree
```

A detached session: nothing is intercepted or recorded, and the read tools are pointed at the file.
Pass `capture='<file name without extension>'` to any read tool.

### Everything else

```
reqtree help
```

Options take their value with `=`, e.g. `--mcp-port=9000`. The ones worth knowing:

| Option | What it does |
|---|---|
| `--port=8888` | Port the capture proxy listens on |
| `--mcp-port=9999` | Port the MCP server listens on |
| `--console-view` | Print one line per exchange, live, for a human watching |
| `--paused` | Start with recording off; traffic flows but is not kept |
| `--buffer=5000` | Exchanges held in memory; past this the oldest are dropped (0 = unlimited) |
| `--buffer-mb=512` | Approximate ceiling on captured bodies (0 = unlimited) |
| `--stop-after=n` | Stop recording after n exchanges |

---

## Connecting an LLM

Register the server **once**:

```
claude mcp add --transport http reqtree http://localhost:9999
```

Add `--scope user` to make it available in every project rather than just the current one.

That registration is a URL in your Claude config. It is **not** tied to a particular ReqTree
process, so:

- **You do not re-add it when ReqTree restarts.** Stop ReqTree, start it again on the same
  `--mcp-port`, and the same registration keeps working.
- **Start ReqTree before the Claude session**, or at least before the first tool call. The MCP
  client connects when it needs the server; if nothing is listening on that port it reports the
  server as unavailable, and you may need to reconnect that session once ReqTree is up.
- **If you change `--mcp-port`, the registration is stale** — it points at the old port. Either keep
  the port stable or re-add with the new one.

Nothing else is needed. There is no bridge process: ReqTree speaks Streamable HTTP directly, and the
server is bound to `127.0.0.1` only — it can start a system-wide intercepting proxy, so it has no
business being reachable from the network.

Several sessions can be connected at once. They share one capture, one set of rules, and one log,
and every change is attributed — see `get_logs`.

---

## Where things end up

Everything lives under `%LOCALAPPDATA%\ReqTree`:

| | |
|---|---|
| `reqtree-root.pfx` / `.cer` | The MITM root certificate, generated once and reused |
| `logs\reqtree-YYYYMMDD.log` | What ReqTree did, kept for seven days. `get_logs` reads this |
| `proxy-state.json` | Present only while ReqTree owns the machine's proxy settings |

**Captured traffic is not here.** It lives in memory and reaches disk only when someone calls
`save_capture`. A capture is lost when the process exits unless it was saved.

## If the internet stops working

ReqTree points the machine's proxy settings at itself. If it dies without restoring them — a hard
kill, a crash, a power cut — every application is left pointed at a port nothing is listening on.

**Run `reqtree start` again.** It detects the state left behind and puts the settings back before
doing anything else. The `clean_stale_proxy_state` tool does the same thing on demand. Failing both,
the settings are under Internet Options → Connections → LAN settings.

## For LLMs reading this repo

`AGENTS.md` is the orientation: architecture, the traps, and the house rules. `DECISIONS.md` says why
each choice was made. `PROGRESS.md` is the status and the list of bugs already found, which is worth
reading before assuming something is broken.
