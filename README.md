# ReqTree

ReqTree is a no-GUI HTTP/HTTPS and WebSocket capture proxy. It captures HTTP/1.x and HTTP/2
traffic plus decoded WebSocket frames in memory and exposes them to an LLM through MCP; the LLM is
the interface for inspecting, saving, and changing traffic. It is a data layer for understanding
an API, not a GUI or an API client generator by itself.

## What an LLM can do through ReqTree

| Capability | MCP tools | What it enables |
|---|---|---|
| Inspect captured traffic | `get_stats`, `search_exchanges`, `get_exchange_detail` | Map endpoints, methods, protocols, headers, JSON bodies, WebSocket frames, status codes, and request order. |
| Control capture | `start_capture`, `stop_capture`, `capture_window`, `clear_*` | Keep only a reproduction or sign-in flow instead of background traffic. |
| Save and compare sessions | `save_capture`, `open_capture`, `list_captures` | Preserve a useful capture or compare it with a later run. |
| Change matching requests | `add_rule`, `list_rules`, `set_rule_enabled` | Block, mock, redirect, set or remove request headers, and redact request bodies. |
| Run custom C# logic | `add_script`, `list_scripts`, `describe_script_format` | Inspect or rewrite requests before they leave and responses before the client receives them. |
| Coordinate sessions | `get_logs`, `log_note` | See who changed shared rules, scripts, or capture state. |

Rules run first and are the simple, declarative option. Scripts are the escape hatch: a
`before_request` script can rewrite a URL, request headers, or a request body, or answer a request
locally by assigning `exchange.StatusCode` and `exchange.ResponseBody`. A `before_response` script
can rewrite the status, headers, or body delivered to the client.

This also makes ReqTree useful for API testing: an LLM can capture a real flow, identify its
contract, generate a client or test cases from the evidence, and use rules or scripts to exercise
success, failure, and edge-case responses.

Headers and bodies must be **assigned**, not mutated in place. For example, assign a new header list
with `exchange.RequestHeaders = [...]`; do not cast and edit the existing list. Call
`describe_script_format` before asking an LLM to write its first script.

When a response script changes traffic, ReqTree keeps the original upstream response in the capture
and sends the modified version only to the client. A locally mocked response is stored as the
response, because there is no upstream version.

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

This is the normal system-wide mode. Run it from an Administrator terminal: ReqTree trusts its root
certificate for the current user and Local Machine, points the Windows proxy settings at itself,
and uses a WFP-backed redirect for local IPv4 HTTP/HTTPS from applications that ignore those
settings. ReqTree must remain elevated while this mode runs. Use **Ctrl+C** to stop it cleanly; that
restores the previous proxy settings and closes the network redirect.

Then add ReqTree as an HTTP MCP server in your LLM client's MCP settings. The portable connection
details are in [Connecting an MCP client](#connecting-an-mcp-client).

## Examples

### Capture a sign-in flow and build a client

Use this only for a website, account, and traffic you are authorized to inspect. Captures can
contain passwords, cookies, bearer tokens, and personal data; do not send an unredacted capture to
an untrusted service or commit it to source control.

1. Tell the LLM: **“Start a fresh capture for `example.com`; clear any existing exchanges first.”**
2. In your browser or app, load the site, sign in with a test account, open an authenticated page,
   then sign out.
3. Tell the LLM: **“I have completed the sign-in flow. Stop capturing and recreate the observed
   website auth flow as an API client. Use placeholders for credentials and secrets, do not reuse
   captured tokens, and do not invent endpoints.”**

ReqTree gives the LLM the captured exchanges. It can inspect their order, URLs, methods, request
and response JSON, headers, cookies, token transitions, and error responses, then generate a small
client, API test suite, test service, schema, fixtures, or mock from that evidence. Save the
evidence when you are done: **“Save this capture as `example-sign-in`.”**

One capture proves only the path you performed. Capture extra flows deliberately for other roles,
errors, device checks, or permissions before asking the LLM to broaden the implementation.

### Example modifications

While testing a site you are authorized to modify, ask directly. For example:

> **“For requests to `api.example.com`, add the request header `X-Test-Mode: true`. Keep capturing
> so I can see the result, and tell me how to undo the change.”**

The LLM can create a matching rule and later disable or remove it. Other useful prompts include:

- **Request body:** “For `POST` requests to `api.example.com/orders`, replace the JSON field
  `testMode` with `true`. Capture the original request and tell me which exchanges changed.”
- **URL redirect:** “Redirect requests from `https://api.example.com/v1/catalog` to
  `https://staging-api.example.com/v1/catalog` until I tell you to stop.”
- **Response body:** “For responses from `api.example.com/feature-flags`, return a version with
  `newCheckout` set to `true`, but keep the original upstream response in the capture.”
- **Mock response:** “Mock `GET https://api.example.com/account` with a `200` JSON response for a
  test user, without sending the request upstream.”
- **Block traffic:** “Block requests to `analytics.example.com` and log every match.”

For conditional or computed changes, the LLM can generate a custom C# script and pass it to ReqTree
for you. ReqTree runs that script before a request leaves or before a response reaches the client.
For example: **“Write and enable a script that adds `X-Preview: true` only when a request URL
contains `/preview/`; log each change and show me the script first.”**

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
| `--network-port=<n>` | `8889` | Internal transparent-listener port used by normal system-wide capture. |
| `--console-view` | off | Print one summary line per completed exchange. |
| `--paused` | off | Start the proxy with recording off. Traffic, rules, and scripts still run. |
| `--buffer=<n>` | `5000` | Maximum exchanges held in memory; drops the oldest when full. `0` is unlimited. |
| `--buffer-mb=<n>` | `512` | Approximate body-memory limit in MB; drops the oldest when full. `0` is unlimited. |
| `--stop-after=<n>` | unlimited | Stop recording after this many exchanges. Traffic continues to flow. |
| `--no-proxy` | off | Start MCP only. Start interception later with the `start_proxy` MCP tool. |
| `--no-system-proxy` | off | Manual-client mode: disable both the Windows proxy change and WFP redirect, then configure one client explicitly. |
| `--no-cert-trust` | off | Generate and export the root certificate without adding it to any Windows trust store. |
| `--user-cert-trust` | off | Trust the root only for the current account, avoiding elevation for certificate installation. Normal WFP capture still requires elevation. |
| `--machine-cert-trust` | on | Compatibility alias; machine-wide trust is already the default. |
| `-h` or `--help` | off | Show the built-in manual. `reqtree help` is the clearest form. |

## Common start modes

| Goal | Command |
|---|---|
| Capture system-wide HTTP/HTTPS traffic | `reqtree start` from an Administrator terminal |
| Limit certificate trust to this account | `reqtree start --user-cert-trust` |
| Capture one manually configured client | `reqtree start --no-system-proxy --no-cert-trust` |
| Connect an LLM before intercepting traffic | `reqtree start --no-proxy` |
| Start recording only when asked | `reqtree start --paused` |
| Read an earlier capture | `reqtree open C:\path\to\capture.reqtree` |

For manual-client mode, point the client at `http://localhost:8888`. The root certificate is still
exported to `%LOCALAPPDATA%\ReqTree\reqtree-root.cer` so that client can trust HTTPS traffic.

Machine-wide certificate trust is the default and persists after ReqTree exits. Normal capture also
uses WinDivert's signed WFP driver to redirect local IPv4 TCP ports 80 and 443 into ReqTree's
transparent listener, including applications that ignore Windows proxy settings. Windows requires
ReqTree to remain elevated while this mode runs. It does not redirect IPv6, UDP, or QUIC, and it
cannot decrypt certificate-pinned TLS clients. `--no-system-proxy` disables both machine-wide
routing mechanisms for explicit/manual client configuration.

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
