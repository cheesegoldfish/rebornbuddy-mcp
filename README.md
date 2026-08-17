# rebornbuddy-mcp

An MCP server for RebornBuddy plugin and routine development.

Ask a coding agent about FFXIV data or live game state and get the answer out of the
client's own memory instead of out of datamining CSVs and guesswork — action IDs, status
IDs, what the weather will do, why the rotation didn't fire, or the result of arbitrary C#
evaluated inside the bot process.

```
> what's the action ID for Mountain Buster in PvP, and does it apply a self-buff?

  lookup_action { name: "Mountain Buster", pvp: true }
  → id 29671, Summoner, PvP
  → otherMatches: [{ id: 25833, isPvp: false }]
```

## Who this is for

**RebornBuddy plugin and routine developers.** If you maintain a combat routine, a botbase,
or a plugin, this turns "I think the API does X" into a question you can answer in one call
against a running client.

It is not for general RebornBuddy users, and there is nothing here that makes a bot work
better. It adds no automation, no farming, no combat behaviour. What it adds is an
introspection endpoint that only helps if you are writing code against the RB API — and it
executes arbitrary C# in your game process, which is a sharp edge that only pays for itself
if you're the one holding it.

## Install

Requires RebornBuddy (net8), the .NET 8 SDK, and Node 18+ for the MCP shim.

```powershell
git clone https://github.com/cheesegoldfish/rebornbuddy-mcp
cd rebornbuddy-mcp
.\scripts\deploy.ps1
```

If PowerShell refuses to run the script, that's the default execution policy rather than
anything about this repo:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy.ps1
```

That builds the plugin, drops it into `<RebornBuddy>\Plugins\RbMcp`, and registers the `rb`
MCP server with Claude Code at user scope.

The script finds RebornBuddy by checking `-RebornBuddyPath`, then `$env:REBORNBUDDY_PATH`,
then folders next to the repo, then the conventional locations. If yours is somewhere
unusual:

```powershell
.\scripts\deploy.ps1 -RebornBuddyPath 'D:\Games\RebornBuddy'
```

Then start RebornBuddy, enable **RbMcp** in the plugin list, and check:

```powershell
curl http://127.0.0.1:8787/health
```

### If RbMcp never appears in the plugin list

RebornBuddy discovers plugins by compiling the `.cs` files under `Plugins\`, and a plugin
that fails to load is dropped **without logging anything**. So the symptom for every cause
below is identical: no entry in the list, nothing anywhere to read. Check in this order.

1. **`RbMcpLoader.cs` is missing from `<RebornBuddy>\Plugins\RbMcp`.** The DLL alone is
   invisible — RB never scans for bare assemblies. `deploy.ps1` fails loudly if it didn't
   land, so this only happens if you copied the DLL by hand.
2. **Your RebornBuddy is newer than the reference assemblies this pins.** The version is in
   `src/RbMcp/RbMcp.csproj`. The plugin compiles against it but loads into whatever RB you
   actually run, so if RB moved an API the assembly resolves against nothing at load time
   and vanishes. Bump the `RebornBuddy.ReferenceAssemblies` version, rebuild, and fix
   whatever stops compiling — that error is the diagnostic RB declined to give you.
3. **A dependency outside `RebornBuddy.dll` and the .NET 8 shared framework.** Same silent
   drop. See `AGENTS.md` if you're adding one.

### Other MCP clients

The shim is a plain stdio MCP server with no Claude-specific behaviour. Point anything that
speaks MCP at it:

```
node mcp/rb-mcp.mjs
```

with `RBMCP_TOKEN_FILE` set to `<RebornBuddy>\Plugins\RbMcp\RbMcp.token`. Use
`.\scripts\deploy.ps1 -SkipMcp` to build and deploy without touching any client config.

## How it works

```
FFXIV ──memory──> RebornBuddy.exe
                   └── RbMcp plugin
                       HttpListener on 127.0.0.1:8787
                              │
              ┌───────────────┴───────────────┐
        mcp/rb-mcp.mjs                  curl / scripts / anything
        (MCP stdio shim)
        your MCP client connects here
```

The plugin speaks plain JSON over three routes (`/health`, `/tools`, `/rpc`). MCP protocol
handling lives in a separate shim process, so protocol changes never require restarting the
game client — and the HTTP API stays usable from anything else.

## Tools

**Static data** — works whenever RB is open, no character needed:
`lookup_action`, `search_actions`, `lookup_status`, `search_statuses`, `search_items`

**Zone context** — `get_zone`, `get_weather_forecast`, `get_fates`

**Live state** — `get_player`, `get_target`, `get_party`, `get_nearby`, `get_cooldowns`,
`get_aura_history`

**Observation ledger** — `set_recording`, `find_observed`. Off by default, and `set_recording`
turns it on without touching the plugin button, so read the note under Configuration before
enabling it — the ledger records other players' character names.

**Escape hatch** — `eval_csharp`

## eval_csharp

Runs arbitrary C# inside the RebornBuddy process, compiled fresh per call by RB's own
Roslyn. The full `ff14bot` API is in scope, plus any routine loaded in the process.

```csharp
Combat.Enemies.Where(e => e.WithinSpellRange(5))
    .Select(e => new { e.Name, e.CurrentHealthPercent }).ToList()
```

Project into anonymous types — a raw `ff14bot` object serializes to just its `ToString()`,
because walking one costs a cross-process memory read per property.

**Nothing is filtered.** A snippet can cast, move your character, and reflect over Magitek,
Lisbeth or LlamaLibrary internals, on a live account in real content. That is deliberate:
the tool exists so an agent can drive the client to answer a question, and a denylist over
`DoAction` or `Navigator` would remove most of the value while stopping nobody — matching
identifiers as written can't see through reflection anyway.

So the boundary is on **who may call**, not on what they may call.

## Security

This is an arbitrary-code-execution endpoint attached to your game client, usually running
elevated. Read this section before deciding to run it.

**Binding to loopback is not enough, and the reason is not obvious.** Every web page your
browser loads can issue requests to `127.0.0.1`. With a "simple" content type there is no
CORS preflight, so the request executes even though the page can't read the response — and
for code execution, the request *is* the payload. DNS rebinding removes the cross-origin
limitation entirely.

The bridge therefore requires, on every request:

| Check | Stops |
|---|---|
| No `Origin` / `Referer` / cross-site `Sec-Fetch-Site` | Browser-driven requests, which always carry these |
| `Host` must be `127.0.0.1`, `localhost` or `[::1]`, on our port | DNS rebinding |
| `/rpc` must be `Content-Type: application/json` | Simple-request CSRF — that content type forces a preflight, and preflights are never answered |
| `Authorization: Bearer <token>` on `/tools` and `/rpc` | Everything else on the machine |

The token is random per install, written to `RbMcp.token` beside the plugin DLL. `/health`
answers without it so setup can be debugged with curl.

**What this does not protect against:** any process already running under your account can
read the token file. Locking the file down would mean depending on
`System.IO.FileSystem.AccessControl`, which ships in some .NET 8 patch installs and not
others, and a missing assembly makes an RB plugin fail to load with nothing in the log. That
trade wasn't worth it. If malware is already running as you, this endpoint is not your
biggest problem.

Set `RequireAuthToken: false` in the settings file only on a machine where you have decided
none of the above matters.

`eval_csharp` snippets referencing `SendChat` are logged in full to RB's log, always. That is
a mirror, not a gate — the snippet still runs. Chat is the one call whose effects land on
other people who don't know an agent is involved.

## Configuration

`<RebornBuddy>\Plugins\RbMcp\RbMcp.json`, written on first run:

| Setting | Default | Notes |
|---|---|---|
| `Port` | 8787 | Must match the MCP registration |
| `AutoStart` | true | Start the listener when the plugin is enabled |
| `RequireAuthToken` | true | See Security. Leave it on |
| `EvalTimeoutMs` | 5000 | Per-snippet execution budget |
| `ToolTimeoutMs` | 5000 | Per-tool budget |
| `VerboseLogging` | false | Log every call to RB's log |
| `RecordObservations` | false | Catalogue nearby statuses and casts — see below |
| `ObservationScanBudget` | 8 | Units whose auras are read per sample tick |

If binding fails with access denied, either run RebornBuddy as administrator (it usually
already is, to read game memory) or reserve the URL once:

```powershell
netsh http add urlacl url=http://127.0.0.1:8787/ user=%USERNAME%
```

### A note on observation recording

`RecordObservations` builds a persistent catalogue of statuses and casts seen nearby, which
is genuinely useful for "what was that debuff" archaeology. It also records the **character
names of other players** around you, durably, to disk in
`<RebornBuddy>\Plugins\RbMcp\RbMcp.observations.json`.

That's other people's data, so it's off by default and the plugin button toggles it. Delete
the file when you're done with it.

## Development

`dotnet build` and you're done — the loader watches the DLL and hot-swaps the plugin without
restarting RebornBuddy. Only changes to `RbMcpLoader.cs` itself need a restart, since RB
compiles that file at startup.

If the build can't find your RebornBuddy install it warns loudly rather than skipping
silently, because "green build, plugin missing from the list, nothing in the log" is the
worst hour this project can cost you.

See `AGENTS.md` for the architecture and the reasoning behind it, and
`docs/eval-cookbook.md` for RB API idioms that are not discoverable from the assemblies.

## License

MIT — see `LICENSE`.

RebornBuddy itself is separate, closed-source, commercial software. This repo contains no
part of it and does not redistribute it; the build resolves the public
`RebornBuddy.ReferenceAssemblies` package from nuget.org at compile time only.
