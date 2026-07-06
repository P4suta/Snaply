# Snaply MCP Server

The Snaply MCP server exposes Snaply's Windows screen-capture and auto-beautify engine to AI clients and agents over the [Model Context Protocol](https://modelcontextprotocol.io). It lets an AI **capture a screenshot, auto-beautify it, and see the result directly** — capture tools can return the finished PNG inline as base64 image content, so the model looks at the actual pixels instead of a file path it cannot open.

It is built on the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and ships inside the `snaply` CLI (assembly `Snaply.Cli`, an unpackaged self-contained console executable).

---

## Starting the server

```pwsh
snaply mcp serve [--allow-capture] [--consent-mode deny|prompt-once|allow]
              [--transport stdio|http] [--http-url <url>] [--stateless]
```

Or, from the repo with the `just` recipes:

```pwsh
just mcp                              # stdio (default)
just mcp -- --transport http          # Streamable HTTP on http://localhost:3001
```

### Transports

Snaply ships **two** transports that share the exact same tool set — pick per client:

| Transport | Flag | Use it for |
| --- | --- | --- |
| **stdio** (default) | `--transport stdio` | Local desktop clients (Claude Desktop / Claude Code). The client launches the `snaply` process and talks over its stdin/stdout pipes; no network socket. This is the standard for local integrations. |
| **Streamable HTTP** | `--transport http` | The modern, recommended networked transport (it supersedes the deprecated HTTP+SSE transport; it uses Server-Sent Events under the hood for streaming). Good for remote/shared agents and web clients. |

For HTTP, `--http-url` sets the listen address (default `http://localhost:3001`) — **bind to loopback** unless you deliberately want remote access. `--stateless` runs without per-session state, which is simpler to scale; omit it for stateful sessions. The MCP endpoint is mapped at the root path (`/`).

```pwsh
# Streamable HTTP, capture enabled, stateless, loopback only
snaply mcp serve --transport http --http-url http://localhost:3001 --stateless --allow-capture
```

> **Security:** the HTTP transport is a real web endpoint. Keep it on `localhost`, and if you expose it, put it behind auth/TLS and validate `Origin`/`Host` (DNS-rebinding protection) per the MCP guidance.

### Logging is file-only

For **stdio**, stdout is the protocol channel, so **nothing** human-readable is written there — all diagnostics go to log files (the HTTP transport logs there too for consistency), under:

```
%LOCALAPPDATA%\Snaply\logs
```

Keep this in mind when debugging: if the server misbehaves, the logs are on disk, not on the console.

---

## Tool catalogue

Snaply exposes five tools. The two `list_*` tools are **read-only and always available**. The three `capture_*` tools are gated by the [consent model](#consent-model) and are **off by default**.

### `list_monitors`

Read-only. No arguments.

Returns a JSON array of monitors. Each entry carries an `index`, a `primary` flag, `dpi`, and `bounds`. The `index` matches the `monitor` argument of `capture_fullscreen`.

### `list_windows`

Read-only. No arguments.

Returns a JSON array of top-level windows, ordered **front-to-back**. Each entry is
`{ handle, title, processName, processId, className, owner, foreground, bounds }`, where `handle`
is a hex window handle (e.g. `"0x00A2"`), `owner` is the hex root-owner handle (or omitted when the
window owns itself), `foreground: true` marks the active window, and `bounds` is
`{ x, y, width, height }`. Pass the `handle` to `capture_window` for an **exact** target; a
`title`/`process` selector is convenient but may match several windows.

### `capture_fullscreen`

Captures an entire monitor.

| Argument | Type | Default | Notes |
|---|---|---|---|
| `monitor` | int | `0` | Monitor index (`0` = primary). Matches `list_monitors` `index`. |
| `beautify` | bool | `false` | **Off by default** — returns the raw screenshot (best for reading UI). Set `true` for the styled frame. |
| `background` | string | *(auto)* | See [beautify grammar](#beautify-argument-grammar). |
| `padding` | string | *(auto)* | |
| `cornerRadius` | string | *(auto)* | |
| `shadow` | string | *(auto)* | |
| `aspect` | string | *(auto)* | |
| `output` | `"image"` \| `"file"` | `"image"` | See [return shapes](#return-shapes). |
| `path` | string | — | Required semantics only when `output="file"`. |
| `delayMs` | int | `0` | Wait this many ms before capturing (let the UI settle first). |
| `confirmed` | bool | `false` | Required in `prompt-once` mode. See [consent](#consent-model). |

### `capture_region`

Captures a rectangle in physical pixels.

| Argument | Type | Default | Notes |
|---|---|---|---|
| `x` | int | — | Left, physical px. |
| `y` | int | — | Top, physical px. |
| `width` | int | — | Must be `> 0`. |
| `height` | int | — | Must be `> 0`. |
| `beautify` (default `false`) / `background` / `padding` / `cornerRadius` / `shadow` / `aspect` | | | Same as above. |
| `output` / `path` / `delayMs` / `confirmed` | | | Same as above. |

### `capture_window`

Captures a top-level window — the one you name, or the active one.

| Argument | Type | Default | Notes |
|---|---|---|---|
| `handle` | string | — | Hex (or decimal) window handle from `list_windows`. **Exact** target. |
| `title` | string | — | Match windows whose title contains this text. |
| `process` | string | — | Match windows owned by this process (name, `.exe` optional). |
| `active` | bool | `false` | Capture the foreground window (also the default when no target is given). |
| `includePopups` | bool | `false` | Also capture the window's owned dialogs/popups (file picker, menus) as **one** image. |
| `beautify` (default `false`) / `background` / `padding` / `cornerRadius` / `shadow` / `aspect` | | | Same as above. |
| `output` / `path` / `delayMs` / `confirmed` | | | Same as above. |

**Targeting precedence:** `handle` (exact) → `active` → `title`/`process` filters (combinable). With
none given, the **foreground** window is captured. A `title`/`process` selector that matches more than
one window does **not** silently pick the first — the call fails with code `capture.window.ambiguous`
and a `candidates` array (`{ handle, title, processName, bounds }`); retry with a specific `handle`.

**`includePopups`** composes the window with its owned popups into a single region capture, so a file
picker or dialog sitting *in front of* the app is included — a plain window capture would only show the
app's own surface. Pair it with `delayMs` to open a menu/dialog first, e.g. `{ "active": true,
"includePopups": true, "delayMs": 800 }`.

### Return shapes

Both capture output modes also carry machine-readable `StructuredContent` on the result.

- **`output="image"` (default)** — the tool returns two content blocks:
  1. a **TextContentBlock** holding a JSON summary `{ width, height, dpi, beautified, bytes }`, and
  2. an **ImageContentBlock** — the base64-encoded PNG (`image/png`).

  This is the mode that lets the AI *see* the screenshot directly.

- **`output="file"`** — the tool saves the PNG to `path` and returns the JSON summary, extended with `savedPath`. No image bytes are returned inline.

### Errors

A failed tool call returns a `CallToolResult` with `IsError: true` and a payload of `{ code, message }`. Denied captures use the code `consent.denied` (see below). An ambiguous `capture_window` selector uses `capture.window.ambiguous` and additionally carries a `candidates` array so the AI can retry with an exact `handle`.

---

## Beautify argument grammar

The capture tools accept the **same string grammar as the Snaply CLI**. Each maps 1:1 to the Core `BeautifySpec`. Beautify is **off by default on the MCP surface** (the AI usually wants faithful pixels); pass `beautify: true` to enable it, and the other arguments then auto-derive sensible values from the capture when omitted (padding, corner radius, and `background:auto` are all inferred).

| Argument | Grammar | Examples |
|---|---|---|
| `beautify` | boolean | `true` enables the styled frame; omit/`false` keeps the raw screenshot |
| `background` | `auto` \| `solid:#RRGGBB[AA]` \| `gradient:#RRGGBB,#RRGGBB@135` \| `image:<path>` | `solid:#1E1E1E`, `gradient:#FF0080,#7928CA@135` |
| `padding` | `N` or `L,T,R,B` (physical px) | `48`, `40,60,40,60` |
| `cornerRadius` | `n` (physical px) | `24` |
| `shadow` | `none` \| `default` \| `offX,offY,blur,opacity[,#RRGGBB]` | `default`, `0,20,60,0.35,#000000` |
| `aspect` | `auto` \| `square` \| `standard` \| `wide` | `wide` |

---

## Consent model

Screen capture is sensitive, so Snaply gates it deliberately. **Capture tools are off by default**; a freshly launched server exposes only `list_monitors` and `list_windows`.

### Flags

- **`--allow-capture`** — enables the `capture_*` tools at all. Without it, capture is unavailable.
- **`--consent-mode <mode>`** — how each capture call is authorized:

| Mode | Behavior |
|---|---|
| `deny` | Capture is always refused. |
| `prompt-once` | **(default)** Every capture call must pass `confirmed: true`. |
| `allow` | No per-call confirmation required. |

### How `prompt-once` works

In the default `prompt-once` mode, the AI must set `confirmed: true` on each `capture_*` call. This gives the client a natural checkpoint to surface a confirmation to the human before any pixels are read. A capture that is not confirmed (or is refused by mode) returns an error with code **`consent.denied`**.

### Recommendation

**Run with `--consent-mode prompt-once`** (the default). It keeps capture available to the agent while forcing an explicit, per-call `confirmed: true` — the best balance of usefulness and safety. Reserve `allow` for trusted, unattended automation, and use `deny` when you want the read-only `list_*` tools exposed but no capture at all.

---

## Register with a client

Add Snaply to your MCP client's server list. The example below is in the Claude Desktop / Claude Code style. Point `command` at your `snaply.exe`, and pass the serve arguments — here enabling capture with the recommended `prompt-once` consent.

```json
{
  "mcpServers": {
    "snaply": {
      "command": "C:\\path\\to\\snaply.exe",
      "args": ["mcp", "serve", "--allow-capture", "--consent-mode", "prompt-once"]
    }
  }
}
```

Restart the client (or reload its MCP servers) after editing the config. The `list_*` tools appear immediately; the `capture_*` tools appear because `--allow-capture` is present.

---

## Example: a `tools/call` for `capture_region`

An annotated JSON-RPC request capturing a 1200×800 region at (100, 200), beautified with a dark gradient background and a shadow, returned as an inline image:

```jsonc
{
  "jsonrpc": "2.0",
  "id": 7,
  "method": "tools/call",
  "params": {
    "name": "capture_region",
    "arguments": {
      "x": 100,                 // left edge, physical pixels
      "y": 200,                 // top edge, physical pixels
      "width": 1200,            // must be > 0
      "height": 800,            // must be > 0
      "beautify": true,
      "background": "gradient:#FF0080,#7928CA@135",
      "padding": "48",
      "cornerRadius": "24",
      "shadow": "default",
      "aspect": "auto",
      "output": "image",        // return base64 PNG inline (the default)
      "confirmed": true          // REQUIRED in prompt-once mode
    }
  }
}
```

The response carries a TextContentBlock with the JSON summary `{ width, height, dpi, beautified, bytes }` and an ImageContentBlock holding the base64 PNG, plus the same data as `StructuredContent`. To save to disk instead, set `"output": "file"` and provide `"path"` — the result then returns the summary with `savedPath` and no inline image.
