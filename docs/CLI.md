# Snaply CLI Reference

`snaply` is the command-line interface to Snaply — a Windows screenshot and
auto-beautify tool. It captures the full screen, a pixel region, or a specific
window, optionally wraps the result in a beautified frame (background, padding,
rounded corners, drop shadow, aspect fit), and writes the PNG to a file, the
clipboard, or standard output.

The CLI is built for automation: docs pipelines, CI jobs, and AI agents. Every
command supports a machine-readable `--json` envelope and a `--stdout` raw-PNG
mode, so it composes cleanly with other tools without touching the GUI.

> **Platform:** Windows only. Capture uses Windows Graphics Capture (WGC) and
> Win32 under the hood. The CLI itself is headless-leaning — it needs no visible
> window and runs fine in a background/CI session — but capture requires a
> desktop session it is allowed to record, and the clipboard/STA paths need a
> message pump (Snaply hosts one internally).

---

## Contents

- [Install / build](#install--build)
- [Command tree](#command-tree)
- [Beautify options](#beautify-options)
- [Output options](#output-options)
- [Global options](#global-options)
- [JSON envelope](#json-envelope)
- [Exit codes](#exit-codes)
- [Examples](#examples)
- [See also](#see-also)

---

## Install / build

Snaply uses [mise](https://mise.jdx.dev/) to supply the .NET 10 SDK
(`10.0.300`) and [`just`](https://just.systems/) as the task runner. From a
clone of the repo:

```bash
# Build the self-contained CLI (assembly Snaply.Cli -> snaply.exe)
just cli-build
```

Once built, you can run the CLI two ways.

**Through `just`** — arguments after `--` are forwarded verbatim:

```bash
just cli -- capture full --out shot.png
```

**Directly** via the produced executable. `snaply.exe` is an unpackaged,
self-contained console app (no runtime install required):

```powershell
# Adjust the path to your build output
.\artifacts\snaply\snaply.exe capture full --out shot.png
```

The rest of this document writes commands as `snaply ...` for brevity. Substitute
`just cli -- ...` or the full `snaply.exe` path as appropriate for your setup.

Convenience `just` recipes wrap the common flows:

| Recipe | Does |
| --- | --- |
| `just cli-build` | Build the CLI executable |
| `just cli -- <args>` | Run the CLI, forwarding `<args>` |
| `just doctor` | Run `snaply doctor` |
| `just completions <shell>` | Print a shell completion script |
| `just mcp` | Start the MCP server (`snaply mcp serve`) |

---

## Command tree

```
snaply
├── capture                                     (every verb also takes --delay <ms>)
│   ├── full [--monitor N]                      Capture a whole monitor (default 0 = primary)
│   ├── region <x,y,w,h>                         Capture a pixel rectangle (physical px; w,h > 0)
│   └── window [--hwnd H | --title <substr> | --process <name> | --active | --pick] [--with-popups]
│                                                Capture one window (or the active one)
├── beautify --in <file>                         Beautify an existing image file
├── list
│   ├── windows                                  List capturable windows, front-to-back
│   └── monitors                                 List monitors (index matches capture full --monitor)
├── doctor                                       Toolchain + capture-runtime health check
├── completions <bash|zsh|pwsh|fish>             Print a shell completion script
└── mcp
    └── serve [--allow-capture] [--consent-mode deny|prompt-once|allow]
                                                 Run the MCP server over stdio

snaply --version        Print the version
snaply --help           Show help (also works on any subcommand: snaply capture region --help)
```

### `snaply capture full [--monitor N]`

Captures an entire monitor. `--monitor` takes a zero-based index; `0` (the
default) is the primary display. Indices match the order from
`snaply list monitors`.

### `snaply capture region <x,y,w,h>`

Captures a rectangle given in **physical pixels** as a single comma-separated
argument: `x,y,width,height`. Both `width` and `height` must be greater than 0.

```bash
snaply capture region 100,100,1280,720 --out region.png
```

### `snaply capture window [--hwnd H | --title <substr> | --process <name> | --active | --pick] [--with-popups]`

Captures a single window. Selectors:

- `--hwnd H` — target a window by its handle, hex (`0x402C4`) or decimal (see
  `snaply list windows`). The **exact** target.
- `--title <substr>` — match windows whose title contains `<substr>`.
- `--process <name>` — match windows owned by a process (name, `.exe` optional).
- `--active` — capture the current foreground window. This is also the **default
  when no selector is given**, so bare `snaply capture window` grabs what's in front.
- `--pick` — open an interactive [Spectre](https://spectreconsole.net/)
  selection list and choose a window. Because it is interactive, `--pick` is
  **not allowed together with `--json` or `--quiet`**.

`--hwnd`, `--active`, and `--pick` are alternatives; `--title` and `--process` are
filters that can be combined with each other. If a `--title`/`--process` filter
matches **more than one** window, the command does not silently pick the first —
it lists the candidates and fails with `capture.window.ambiguous` (exit `15`), so
you can re-run with the exact `--hwnd`.

`--with-popups` captures the window **together with its owned dialogs/popups** —
a file picker, modal dialog, or open menu — as one image. Without it a window
capture shows only the app's own surface, even if a picker is sitting in front of
it. Combine with `--delay` to open the popup first:

```bash
# Open a menu/dialog during the delay, then capture the window with it
snaply capture window --process myapp --with-popups --delay 1500 --out withdialog.png
```

### `snaply beautify --in <file>`

Runs the beautify pipeline on an existing image (no capture). Takes the same
beautify and output options as the capture verbs.

```bash
snaply beautify --in raw.png --background gradient:#0f172a,#334155@135 --out pretty.png
```

### `snaply list windows` / `snaply list monitors`

Enumerate capture targets. `list windows` reports windows front-to-back;
`list monitors` reports monitors in the same index order used by
`capture full --monitor`. Both support `--json`.

### `snaply doctor`

Runs a health check over the toolchain and capture runtime and reports whether
the environment can capture. Supports `--json` for scripted preflight checks in
CI.

### `snaply completions <shell>`

Prints a shell completion script to stdout for `bash`, `zsh`, `pwsh`, or
`fish`. See [Shell completions](#shell-completions).

### `snaply mcp serve`

Starts the Model Context Protocol server over stdio so an AI client can drive
Snaply. See [MCP.md](./MCP.md) for the full tool surface and consent model.

---

## Beautify options

These options are shared by every capture verb (`capture full`,
`capture region`, `capture window`) and by `beautify`. They map 1:1 to the Core
`BeautifySpec`. When you do not override them, padding, corner radius, and the
`auto` background are derived automatically from the capture.

| Option | Alias | Value grammar | Meaning |
| --- | --- | --- | --- |
| `--no-beautify` | | (flag) | Keep the raw screenshot; skip beautify entirely |
| `--background` | `-b` | `auto` \| `solid:#RRGGBB[AA]` \| `gradient:#RRGGBB,#RRGGBB@135` \| `image:<path>` | Backdrop behind the shot. Gradient angle after `@` is in degrees |
| `--padding` | `-p` | `N` or `L,T,R,B` | Space between the shot and the frame edge, in physical px. One value = uniform; four = left,top,right,bottom |
| `--corner-radius` | `-r` | `<n>` | Corner rounding of the shot, in physical px |
| `--shadow` | `-s` | `none` \| `default` \| `offX,offY,blur,opacity[,#RRGGBB]` | Drop shadow. Custom form: X/Y offset, blur radius, opacity, optional color |
| `--aspect` | `-a` | `auto` \| `square` \| `standard` \| `wide` | Aspect-ratio fitting of the final frame |

Defaults come from `BeautifySpec.Default`. To turn beautify off completely, use
`--no-beautify`.

```bash
# Solid background, 48px uniform padding, 16px corners, custom shadow
snaply capture full \
  --background solid:#1e1e2e \
  --padding 48 \
  --corner-radius 16 \
  --shadow 0,24,48,0.35,#000000 \
  --out framed.png
```

---

## Output options

Output options are combinable — you can, for example, save a file **and** copy
to the clipboard in one run.

| Option | Alias | Meaning |
| --- | --- | --- |
| `--out <path>` | `-o` | Save a PNG to `<path>` |
| `--clipboard` | `-c` | Copy the PNG to the clipboard (via an STA message-pump host) |
| `--stdout` | | Write raw PNG bytes to stdout; all human text goes to stderr |

Every `capture` verb (`full`, `region`, `window`) also accepts **`--delay <ms>`** —
wait that many milliseconds before capturing. Use it to let a UI animation settle,
or to open a menu/dialog by hand before the shot is taken (`beautify` does not take
it, as it captures nothing).

**Defaults and rules:**

- If you specify no output option and are **not** in `--json` mode, Snaply saves
  to `./snaply-YYYYMMDD-HHmmss.png` in the current directory.
- In `--json` mode with **no** output option specified, the command fails with
  error code `output.missing` (exit code `30`). Be explicit about output when
  scripting with `--json`.
- `--stdout` sends the binary PNG to stdout and redirects **all** human-readable
  text to stderr, so the stream stays a clean pipe for downstream tools.

---

## Global options

These are recursive — available on every command and subcommand.

| Option | Alias | Meaning |
| --- | --- | --- |
| `--json` | | Emit a machine-readable JSON envelope instead of the human summary |
| `--quiet` | `-q` | Suppress non-essential output |
| `--verbose` | | Show error causes and extra detail |
| `--no-color` | | Disable ANSI color (also honors the `NO_COLOR` environment variable) |

---

## JSON envelope

With `--json`, the command prints a single JSON object to stdout. The envelope
has a stable shape: an `ok` boolean, the `command` name, and either `data`
(success) or `error` (failure).

### Success

```json
{
  "ok": true,
  "command": "capture.region",
  "data": {
    "width": 2400,
    "height": 1500,
    "dpi": 144,
    "beautified": true,
    "output": {
      "path": "C:\\shots\\a.png",
      "clipboard": false,
      "stdout": false,
      "bytes": 481203
    }
  }
}
```

### Failure

```json
{
  "ok": false,
  "command": "capture.window",
  "error": {
    "code": "capture.window",
    "message": "No window matched title substring \"invoice\".",
    "exitCode": 10
  }
}
```

The `error.exitCode` field mirrors the process exit code (see below), so scripts
can branch on either.

### Command-specific `data` shapes

- **`list.windows`** — `data` is an array of
  `{ "handle": "0x00A2", "title": "...", "processName", "processId", "className", "owner", "foreground", "bounds": { "x", "y", "width", "height" } }`
  (`owner` is the hex root-owner handle or `null`; `foreground` marks the active window).
- **`list.monitors`** — `data` is an array of
  `{ "index", "primary", "dpi", "bounds" }`.
- **`doctor`** — `data` is
  `{ "healthy": true, "checks": [ { "name", "status", "detail" } ] }`.

Example `list monitors --json`:

```json
{
  "ok": true,
  "command": "list.monitors",
  "data": [
    { "index": 0, "primary": true,  "dpi": 144, "bounds": { "x": 0,    "y": 0, "width": 2560, "height": 1440 } },
    { "index": 1, "primary": false, "dpi": 96,  "bounds": { "x": 2560, "y": 0, "width": 1920, "height": 1080 } }
  ]
}
```

---

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Unexpected error |
| `2` | Usage / parse error (`System.CommandLine`) or `input.invalid` |
| `10` | Capture failed (`capture.*`) |
| `15` | Window selector matched several windows (`capture.window.ambiguous`) |
| `11` | Beautify render failed (`beautify.render`) |
| `12` | Export to file failed (`export.save`) |
| `13` | Export to clipboard failed (`export.clipboard`) |
| `14` | Pipeline produced no capture (`pipeline.nocapture`) |
| `20` | Consent denied (`consent.denied`) |
| `30` | No output specified in `--json` mode (`output.missing`) |

---

## Examples

### Full screen to a file

```bash
snaply capture full --out screenshot.png
```

Capture a specific monitor (index from `snaply list monitors`):

```bash
snaply capture full --monitor 1 --out second-display.png
```

### Region with a gradient background and wide aspect

```bash
snaply capture region 0,0,1600,1000 \
  --background gradient:#0f172a,#6366f1@135 \
  --aspect wide \
  --padding 64 \
  --corner-radius 20 \
  --out hero.png
```

### Window by title, straight to the clipboard

```bash
snaply capture window --title "Visual Studio Code" --clipboard
```

Pick a window interactively (not compatible with `--json`/`--quiet`):

```bash
snaply capture window --pick --out chosen.png
```

Save **and** copy in one run (output options combine):

```bash
snaply capture full --out shot.png --clipboard
```

### Pipe raw PNG via `--stdout` to another tool

`--stdout` emits binary PNG on stdout with all human text on stderr, so it pipes
cleanly. For example, re-encode to JPEG or resize with ImageMagick:

```bash
snaply capture full --no-beautify --stdout | magick png:- -resize 50% out.jpg
```

Or on PowerShell, write the piped bytes to a file:

```powershell
.\snaply.exe capture region 100,100,800,600 --stdout > region.png
```

### `--json` for scripting

Capture and parse the result with `jq`:

```bash
result=$(snaply capture full --out shot.png --json)
echo "$result" | jq -r '.data.output.path'   # -> shot.png's absolute path
echo "$result" | jq -r '.data.bytes'          # -> PNG size in bytes
```

Branch on success/failure in a shell script:

```bash
if snaply capture window --title "Terminal" --out term.png --json > result.json; then
  echo "captured $(jq -r '.data.width'x'.data.height' result.json)"
else
  code=$(jq -r '.error.code' result.json)
  echo "capture failed: $code" >&2
  exit "$(jq -r '.error.exitCode' result.json)"
fi
```

Preflight a CI job with `doctor`:

```bash
snaply doctor --json | jq -e '.data.healthy' >/dev/null || {
  echo "Snaply capture environment is unhealthy" >&2
  exit 1
}
```

Remember: in `--json` mode you must specify an output option, or the command
fails with `output.missing` (exit `30`):

```bash
snaply capture full --json                 # -> fails, exit 30 (no --out/--clipboard/--stdout)
snaply capture full --out shot.png --json  # -> ok
```

### Shell completions

Print a completion script for your shell and load it. Supported shells:
`bash`, `zsh`, `pwsh`, `fish`.

```bash
# Bash — load for the current session
source <(snaply completions bash)

# Bash — install persistently
snaply completions bash > ~/.local/share/bash-completion/completions/snaply
```

```zsh
# Zsh
snaply completions zsh > "${fpath[1]}/_snaply"
```

```powershell
# PowerShell — add to your profile
snaply completions pwsh | Out-String | Invoke-Expression
```

```fish
# Fish
snaply completions fish > ~/.config/fish/completions/snaply.fish
```

---

## See also

- [MCP.md](./MCP.md) — driving Snaply from an AI client via the Model Context
  Protocol server (`snaply mcp serve`), tool surface, and consent model.
