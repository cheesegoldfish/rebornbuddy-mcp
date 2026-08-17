# AGENTS.md

Guidance for working on rebornbuddy-mcp itself. For *using* it from another project, see
the README.

---

## What this is

A RebornBuddy plugin that exposes the running bot over MCP. It exists so questions about
FFXIV data and live game state can be answered from the client's own memory instead of from
datamining CSVs and guesswork.

It is dev tooling for people who write RebornBuddy plugins and routines. It adds no bot
behaviour, it is never shipped to end users of any routine, and it must never become a
dependency of one.

## Build

```powershell
dotnet build src\RbMcp\RbMcp.csproj
```

The build copies `RbMcp.dll` into `<RebornBuddy>\Plugins\RbMcp`, and the running plugin
hot-reloads itself. **Leave RebornBuddy open** — the loader reads the assembly from bytes
rather than mapping the file, so nothing holds a lock and you keep your game session. See
"Hot reload" below.

Full install (build + deploy + register the MCP server):

```powershell
.\scripts\deploy.ps1
```

- Target framework: `net8.0-windows8.0`, C# 10, matching RB itself.
- `RebornBuddy.ReferenceAssemblies` is referenced **compile-time only**
  (`ExcludeAssets=runtime`). Never let RB's own assemblies copy into the plugin folder.

### No hardcoded install paths, ever

Every developer puts RebornBuddy somewhere different, so the build resolves it:
`-p:RbPluginDir=...`, then `$env:REBORNBUDDY_PATH`, then the conventional location. Same
order in `deploy.ps1` via `-RebornBuddyPath`. `scripts/apidump` resolves the NuGet cache the
same way and takes the highest installed package version rather than a pin.

If resolution fails, the build emits a **warning** and says what to set. It must never skip
silently: a green build with no plugin in RB's list and nothing in any log is the single
most expensive failure mode this project has.

---

## Architecture

Two processes, split on purpose.

```
RebornBuddy.exe
└── Plugins/RbMcp/
    ├── RbMcpLoader.cs          compiled by RB; loads the DLL  <-- required
    ├── RbMcp.token             generated on first run; the shim reads it
    └── RbMcp.dll
        ├── Plugin.cs           BotPlugin lifecycle + the observations toggle
        ├── Threading/          marshals all RB access onto one logical thread
        ├── Http/               HttpListener; Guard + AuthToken; /health /tools /rpc
        ├── Eval/               RB's Roslyn
        ├── Tracking/           aura history + the observation ledger
        └── Tools/              the tool surface

mcp/rb-mcp.mjs                  MCP stdio shim -> the HTTP API
```

### The loader is not optional

**RebornBuddy discovers plugins by compiling the `.cs` files under `Plugins\`. It never
scans for DLLs.** A folder containing only a DLL is silently ignored — the plugin does not
appear in the list and *nothing is logged*. Every prebuilt plugin in the ecosystem ships a
source loader beside its assembly; see `Plugins\Panda Farmer\PandaFarmerLoader.cs`.

`loader/RbMcpLoader.cs` is ours. It loads `RbMcp.dll` **from bytes**, not `LoadFrom`, so RB
does not lock the file and you can rebuild without closing the client.

Because RB compiles that file rather than us, a mistake in it reproduces exactly the
symptom above — an invisible plugin, nothing logged. `loader/RbMcpLoader.csproj` exists
solely to compile it against the reference assemblies so errors surface as build failures;
`deploy.ps1` runs it. Its output is never shipped. Keep the loader dependent on nothing
beyond `ff14bot` and the BCL.

### Hot reload

The loader watches the DLL and swaps the plugin in on change, so the normal loop is
`dotnet build` and nothing else — no RB restart, no lost game session. Watch RB's log:

```
[RbMcp] Change detected, reloading...
[RbMcp] Reloaded v0.1.0.
```

Reload order matters and is easy to get wrong. The old instance's **statics** own a bound
`HttpListener` and the `GameThread` worker; the replacement lives in a freshly loaded
assembly with its own statics. So the old one is fully shut down and given a grace period
to release port 8787 *before* the new one is constructed — otherwise the rebind fails and
you get a dead bridge that looks alive.

A build writes the DLL in pieces, so reads retry briefly and events are debounced (one
save raises several).

**You still need an RB restart when you change `RbMcpLoader.cs` itself** — RB compiles that
at startup. Changing the loader is rare; changing the plugin is not.

Assemblies are never unloaded (no collectible `AssemblyLoadContext`), so each reload leaks
one. Irrelevant for a dev tool over a session; worth knowing before you reload 500 times.

### Dependency rule

**A plugin may depend on `RebornBuddy.dll` and the .NET 8 shared framework, and nothing
else.** Anything else fails to resolve at load time and RB swallows the error.

Beware that a reference appearing in `RebornBuddy.dll`'s own reference list does *not*
make it available — RB carries several assemblies embedded. The check that matters is
whether a DLL exists on disk in the shared framework or RB's folder.
`System.Configuration.ConfigurationManager` — the home of RB's `[Setting]` attribute —
fails that check, which is why `BridgeSettings` is a plain POCO rather than a
`JsonSettings` subclass.

The rule is stricter than "does it exist on my machine". `System.IO.FileSystem.AccessControl`
ships in `Microsoft.NETCore.App/8.0.6` and is **absent from 8.0.5** — so a plugin using it
loads for you and vanishes for the next developer, with nothing logged either time. That is
why `AuthToken` writes its file with default ACLs and documents the limitation instead of
tightening it. When a dependency would buy a secondary benefit at the cost of load-time
fragility, take the documentation.

**Why the split.** The in-process half is expensive to change: iterating on it means
closing RebornBuddy and losing your game session. The MCP protocol, meanwhile, moves. So
the plugin speaks three boring stable routes and the protocol lives in a shim you can edit
freely. It also keeps the HTTP API usable from `curl` or any script.

### Threading — read this before touching anything

`Threading/GameThread.cs` is the load-bearing piece.

RB game objects are lazy facades over the FFXIV process's memory, and RB's caches are
mutated by the botbase pulse thread. Reading them from an HTTP threadpool thread is a data
race that produces *wrong values*, not exceptions — the worst possible failure mode.

Every tool handler therefore runs through `GameThread.Invoke`, which drains from one of
two places depending on whether a botbase is running:

| Bot state | Drains from | Pulses? |
|---|---|---|
| `TreeRoot.IsRunning` | `Plugin.OnPulse` (already the pulse thread) | No — the bot is doing it |
| Stopped | the bridge's own worker thread | Yes, `Pulsator.Pulse` first |

A watchdog covers the middle case where the bot claims to be running but pulses have
stalled; after `PulseStallMs` the worker drains anyway rather than hanging every request.

**Rules:**
- Never touch an `ff14bot` type outside a `GameThread.Invoke` callback.
- Never call `GameThread.Invoke` from inside a handler already on the game thread — it
  deadlocks against its own queue.
- Keep handlers short. They run on the pulse thread while a bot is active; a slow handler
  is a stuttering bot.

### Serialization

`Http/Json.cs` refuses to reflect over `ff14bot` types, reducing them to `ToString()`
instead. This is not laziness — a reflection-based serializer on a `BattleCharacter` costs
a cross-process memory read per property and the graph is cyclic.

So: **tool handlers project into anonymous types by hand.** Verbose, but it is the only
thing keeping payload cost bounded and predictable. Depth and sequence length are capped
so a careless `eval_csharp` degrades into a truncated answer rather than a hung request.

### Eval

`Eval/ScriptHost.cs` uses RB's own `Clio.Utilities.Compiler.CodeCompiler`, not a bundled
Roslyn. RB has Roslyn ILMerged into `RebornBuddy.dll` as **public types**, so
`CSharpSyntaxWalker` and friends are available just by referencing RB — no NuGet, no
version conflict, and the same type identity as RB's compiler (which matters, because we
read `EmitResult.Diagnostics` off its `CompileResult`).

Compile happens off the game thread (Roslyn is slow enough to stall a pulse); only the
invocation is marshalled.

There is **no capability filter** on eval, and adding one back would be a regression. An
earlier version carried a syntax-level denylist over `DoAction`, `Navigator` and friends. It
was deleted because it defended against the wrong party: the caller is an agent working on
the developer's behalf, and letting it cast something, walk somewhere, or reflect over a
loaded routine is the entire point of the tool. A name-matching walker also cannot see
through reflection, so it cost real capability and bought approximately nothing.

If you find yourself wanting to block a call, the question to ask is "how did an untrusted
caller reach this port", and the answer belongs in `Http/Guard.cs`.

### Security model — the boundary is the caller, not the capability

`Http/Guard.cs` runs before every handler. Read its file comment before changing anything
in it; the checks look excessive for a loopback service and are not.

The load-bearing insight: **loopback keeps the bridge off the network, but not away from
attackers.** Any web page can POST to `127.0.0.1`, and with a simple content type it does so
without a preflight — the response is unreadable to the attacker but the request already
executed. RB usually runs elevated, so that is an unprivileged origin driving code in an
admin process.

Four checks, all cheap:

| Check | Defeats |
|---|---|
| Reject `Origin`, `Referer`, cross-site `Sec-Fetch-Site` | Browser-driven requests |
| `Host` must be `127.0.0.1`, `localhost` or `[::1]`, on our port | DNS rebinding |
| `/rpc` demands `application/json` | Simple-request CSRF — forces a preflight, and OPTIONS is answered 405 |
| Bearer token on `/tools` and `/rpc` | Other local processes |

Never emit CORS headers and never answer OPTIONS successfully. A preflight that cannot
succeed is doing the work.

`Http/AuthToken.cs` generates the secret on first run. Be honest about its limits in any
docs you write: it stops a browser dead, and it does not stop a process already running as
the user.

---

## Adding a tool

1. Add a `ToolDef` in the relevant `Tools/*.cs` `Register()`.
2. Write a **specific** description. It is the only thing a model sees when deciding
   whether to call it — say what it returns and when to prefer it.
3. Project into an anonymous type. Never return a raw `ff14bot` object.
4. Set `NeedsGameThread = false` only if the handler genuinely touches no game state.
5. Handle "not logged in" explicitly: return `available: false` with a reason rather than
   throwing. Callers hit this constantly and it is not an error.

No registry file to update — `ToolRegistry.RegisterAll()` calls each module's `Register()`.

## Testing

There is no automated test suite; the dependency is a live game client.

- `dotnet build` catches the majority of mistakes, since the RB API surface is what shifts
  between versions.
- Beyond that, exercise it against a running RB: `curl http://127.0.0.1:8787/health`, then
  the tool you changed, then the same tool **with a botbase running** — the threading
  paths differ and only the second one exercises `OnPulse`. `/tools` and `/rpc` need the
  token, so pass `-H "Authorization: Bearer $(cat <plugin dir>/RbMcp.token)"`.
- When you touch `Http/Guard.cs`, check the refusals too, not just the happy path. A
  request with `-H 'Origin: https://example.com'`, one with `-H 'Host: evil.com'`, and one
  with `--data 'x'` and no JSON content type should all be refused.
- `scripts/apidump` is the reference for what RB actually exposes. When unsure of a
  property name, dump it rather than guessing; the reference assemblies disagree with
  intuition often enough to matter (`WorldManager.EorzaTime` is spelled exactly like that).

## Eval cookbook

`docs/eval-cookbook.md` documents RB API idioms that are not discoverable from the
reference assemblies - `DynamicString()` for property discovery, `GetMaskedAction`,
the full attackable-target validity chain, `Clio.Common.MathEx` geometry helpers, and
the RB compiler's `//!CompilerOption:AddRef:` directive. Mined from a long-running
RebornConsole scratchpad; add to it whenever a snippet turns up something non-obvious.

## Known RB host quirks

Things about the plugin host, as opposed to the game data. Both of these were found only by
running against a live client; neither is visible from the reference assemblies.

| Behaviour | Consequence |
|---|---|
| **`Assembly.GetExecutingAssembly().Location` is an empty string.** The loader loads the DLL from a byte array so RB does not lock it, and a byte-loaded assembly has no on-disk location. | Any path derived from it collapses to `Path.Combine(".", ...)` — RB's *working directory*, not the plugin folder. This silently misplaced `RbMcp.json`, `RbMcp.token` and the observation ledger. Always go through `PluginPaths`, which asks `GlobalSettings.PluginsPath` the way the loader does. |
| **`BotPlugin.ButtonText` is only re-read on redraw.** | Toggling state from `OnButtonPress` leaves the label showing the old value until the plugin list repaints. The state is correct; the label lags. Don't debug this — log the new state instead of trusting the button text. |

## Known RB data quirks

Found by checking output against answers we already knew. Worth doing for any new field —
a plausible-looking wrong number is worse than a missing one, because nobody thinks to
question it.

| Field | Problem |
|---|---|
| `SpellData.BaseCastTime` | Offset-encoded: 187500ms for an instant, 190500ms for a 3s cast. Dropped from `lookup_action`; use `Adjusted*`. |
| `SpellData.BaseCooldown` | Inconsistent — 0 for Fast Blade, 1500 for Holy Spirit, both 2.5s GCDs. Dropped. |
| `LocalPlayer.CurrentGP/CP` | Stale non-zero value against a `Max` of 0 on combat jobs. Omitted unless the max is non-zero. |
| `EventObject.Name` | Often empty — but **not an RB bug**, so do not try to "fix" it. Pc, BattleNpc and Treasure are 100% named; EventObject frequently is not, because 7,538 of the 15,710 rows in RB's own `EventObjectResult` table (`db.s3db`) are blank in *every* language. RB reports the name whenever one exists. The unnamed ones are trigger volumes and collision objects that the game itself never named. Key mapping if you ever need the sheet row: `EObj row Id = NpcId - 2000000` (Knowledge Crystal = NpcId 2013856 = row 13856). |
| `WorldManager.AetheryteIdsForZone` | Returns empty in Occult Crescent / Field Operation zones — they do not register normal aetherytes. |
| `Aura.TimeLeft` | Negated full duration on the sample where an aura first appears (`-30` for a 30s buff), then counts down normally. `AuraTracker` reports the magnitude. |
| `ClassJobType` | Renders as `"255"` for actions with no job affinity. Cosmetic. |

## Conventions

- Match the surrounding style: file-scoped namespaces, `internal` by default, `Plugin` the
  only public type.
- Comments explain *why*, particularly around threading, serialization and the guard, where
  the obvious-looking simplification is wrong.
- Keep `Plugin.cs` thin. Lifecycle only.
- This repo is public. No absolute paths from your own machine, no real names, no account
  identifiers — in code, comments, docs, or commit messages. `cheesegoldfish` is the
  attribution used throughout; keep it that way.
