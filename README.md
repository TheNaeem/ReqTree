# ReqTree

ReqTree is a no-GUI HTTP/HTTPS capture proxy. It captures traffic in memory and exposes it to an
LLM through MCP; the LLM is the interface for inspecting, saving, and changing traffic.

## Quick start — the normal setup

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then build once from
the repository root:

```powershell
dotnet build ReqTree.sln
```

Start ReqTree with its default, system-wide setup:

```powershell
.\src\ReqTree\bin\Debug\net10.0\reqtree.exe start
```

Or, after putting `reqtree.exe` on your `PATH`:

```powershell
reqtree start
```

This is the simplest mode. ReqTree trusts its root certificate for the current user, points the
machine's proxy settings at itself, and starts recording traffic from browsers and applications.
Use **Ctrl+C** to stop it cleanly; that restores the previous system-proxy settings.

Then add ReqTree as an HTTP MCP server in your LLM client's MCP settings. The portable connection
details are in [Connecting an MCP client](#connecting-an-mcp-client).

## Commands

| Command | Purpose |
|---|---|
| `reqtree start [options]` | Start MCP and, by default, the system-wide capture proxy. |
| `reqtree open <file.reqtree>` | Open a saved capture for reading; it does not intercept or record traffic. |
| `reqtree help` | Print the built-in manual. It works without a repository or a running server. |

## Start options

Option values always use `=`, for example `--mcp-port=9000`.

| Option | Default | Purpose |
|---|---:|---|
| `--port=<n>` | `8888` | TCP port for the capture proxy. |
| `--mcp-port=<n>` | `9999` | TCP port for the localhost MCP server. |
| `--console-view` | off | Print one summary line per completed exchange. |
| `--paused` | off | Start the proxy with recording off. Traffic, rules, and scripts still run. |
| `--buffer=<n>` | `5000` | Maximum exchanges held in memory; drops the oldest when full. `0` is unlimited. |
| `--buffer-mb=<n>` | `512` | Approximate body-memory limit in MB; drops the oldest when full. `0` is unlimited. |
| `--stop-after=<n>` | unlimited | Stop recording after this many exchanges. Traffic continues to flow. |
| `--no-proxy` | off | Start MCP only. Start interception later with the `start_proxy` MCP tool. |
| `--no-system-proxy` | off | Listen without changing the machine's proxy settings; configure one client manually. |
| `--no-cert-trust` | off | Generate and export the root certificate without adding it to the current user's trust store. |
| `-h` or `--help` | off | Show the built-in manual. `reqtree help` is the clearest form. |

## Common start modes

| Goal | Command |
|---|---|
| Capture everything on this machine | `reqtree start` |
| Capture one manually configured client | `reqtree start --no-system-proxy --no-cert-trust` |
| Connect an LLM before intercepting traffic | `reqtree start --no-proxy` |
| Start recording only when asked | `reqtree start --paused` |
| Read an earlier capture | `reqtree open C:\path\to\capture.reqtree` |

For manual-client mode, point the client at `http://localhost:8888`. The root certificate is still
exported to `%LOCALAPPDATA%\ReqTree\reqtree-root.cer` so that client can trust HTTPS traffic.

## Connecting an MCP client

ReqTree speaks Streamable HTTP directly, so any LLM client that supports HTTP MCP servers can use
it. There is no bridge process and no command to run from the client configuration.

1. Start ReqTree: `reqtree start`.
2. Open your LLM client's MCP-server settings and add a remote HTTP server.
3. Enter these values:

   | Setting | Value |
   |---|---|
   | Name | `reqtree` |
   | Transport | Streamable HTTP (some clients label this simply **HTTP**) |
   | URL | `http://127.0.0.1:9999` |
   | Authentication / headers | None |

4. Save or reconnect the MCP client, then call `get_proxy_status` to confirm it is connected.

Keep the URL's port in sync with `--mcp-port`. For example, if ReqTree starts with
`--mcp-port=9000`, configure `http://127.0.0.1:9000` instead. The endpoint is loopback-only, so the
client must run on the same machine as ReqTree.

Several LLM sessions can connect at once; they share one capture, one set of rules and scripts, and
one coordination log. `get_logs` shows who changed what.

## Data and recovery

ReqTree stores its certificate, logs, and proxy-recovery marker in `%LOCALAPPDATA%\ReqTree`:

| Path | Contents |
|---|---|
| `reqtree-root.pfx` / `reqtree-root.cer` | The generated MITM root certificate. |
| `logs\reqtree-YYYYMMDD.log` | The activity log read by `get_logs`. |
| `proxy-state.json` | Present only while ReqTree owns the system-proxy settings. |

Captured traffic is **not** written there automatically. It lives in memory until `save_capture` is
called, and is lost when ReqTree exits if it was not saved.

If the internet appears to stop after a crash or hard kill, run `reqtree start` again. ReqTree sees
the stale recovery marker and restores the prior system-proxy settings before starting. The
`clean_stale_proxy_state` tool provides the same repair on demand.

## For contributors and LLMs

`AGENTS.md` explains the architecture and repository rules. `DECISIONS.md` explains key tradeoffs.
`PROGRESS.md` records the current state and prior bugs. Keep this README and `reqtree help` aligned
whenever the CLI changes.
