# eval_csharp cookbook

RebornBuddy API idioms worth knowing before writing a snippet. Most of these are not
discoverable from the reference assemblies alone — they came out of a working
RebornConsole scratchpad built up over years, and several would take a long time to
rediscover.

Verified against RB 1.0.803 unless noted.

---

## Output

Return a value; don't log. `eval_csharp` serializes whatever the snippet returns.

**Always project into anonymous types.** RB objects are lazy facades over the game's
memory — one property access is one cross-process read — so the serializer refuses to walk
them and gives you `ToString()` instead.

```csharp
// good
GameObjectManager.GameObjects
    .Where(o => o.CanAttack)
    .Select(o => new { o.Name, o.NpcId, Dist = Core.Me.Location.Distance(o.Location) })
    .ToList()

// returns a bare type name
Core.Me.CurrentTarget
```

### DynamicString() — the discovery hammer

Every `GameObject` has `DynamicString()`, which dumps *every* property including ones not
exposed on the public type (`BaseId`, `RawDifficulty`, `DifficultyEstimate`, `VTable`,
`Pointer`).

```csharp
Core.Me.CurrentTarget.DynamicString()
```

~1.2KB per object, so call it on **one** object when you don't know what's available —
never across a collection.

---

## Finding things

```csharp
// by NPC id
GameObjectManager.GetObjectByNPCId(2013856)
GameObjectManager.GetObjectsByNPCId(2013180)
    .Where(i => i.Distance2D(Core.Me.Location) < 30 && i.IsTargetable)
    .OrderBy(i => i.Distance2D(Core.Me.Location))
    .FirstOrDefault()

// by type — second arg controls whether to include non-visible
GameObjectManager.GetObjectsOfType<BattleCharacter>()
GameObjectManager.GetObjectsOfType<EventObject>(true, false)
```

`GameObjectType`: `Pc=1, BattleNpc=2, EventNpc=3, Treasure=4, AetheryteObject=5,
GatheringPoint=6, EventObject=7, Mount=8, Minion=9, Retainer=10, HousingEventObject=12,
MJIObject=14`.

**Naming:** `Pc`, `BattleNpc` and `Treasure` are reliably named. `EventObject` often is
not — roughly half of the rows in RB's own `EventObjectResult` table are blank in every
language, because the game never named them. Match those on `NpcId`. The sheet row is
`NpcId - 2000000` if you ever need to look one up.

### Is this thing actually attackable

The full validity chain, from a real combat snippet:

```csharp
var c = (Character)x;
!c.IsMe && !c.IsDead && c.IsValid && c.IsTargetable
       && c.IsVisible && c.InLineOfSight() && c.CanAttack
```

`InLineOfSight()` is the one people forget.

---

## Spells

```csharp
DataManager.GetSpellData(41636)          // by id
DataManager.GetSpellData("Isle Sprint")  // by name — string overload exists
ActionManager.GetMaskedAction(s.Id)      // what the button ACTUALLY casts right now
ActionManager.CanCast(s, Core.Me)
ActionManager.HasSpell(id)               // respects level sync; LevelAcquired does not
```

`GetMaskedAction` matters for combo buttons, job-gauge upgrades, and PvP role actions —
the id you look up is often not the id that fires.

### Upgrades and replacements — RB already handles them

The single most common wrong assumption when writing rotation logic: that an upgraded action
makes the base action "unknown", so you have to select the new id by hand. It does not.

**Permanent upgrades** (the Lv96 tier and friends) keep the base action known and mask to the
upgrade. Verified live on a Lv96 WHM and a Lv100 SCH:

```
Medica II (133, Lv50)  -> HasSpell = true, GetMaskedAction -> Medica III  (37010)
Succor    (186, Lv35)  -> HasSpell = true, GetMaskedAction -> Concitation (37013)
```

**Conditional replacements** (stances, gauge states) mask only while the condition holds, and
`CanCast` on the *base* id stays true throughout. Same SCH, with Seraphism up vs down:

```
Seraphism down:  Adloquium (185) -> Adloquium      Succor (186) -> Concitation
Seraphism up:    Adloquium (185) -> Manifestation  Succor (186) -> Accession
                 CanCast(185, self) = true         CanCast(186, self) = true
```

So `Spells.Succor.Cast()` already fires Concitation, and Accession under Seraphism. Ternaries
that hand-pick the upgraded id are dead code, and they actively break anything comparing
`Casting.CastingSpell` / `Casting.LastSpellWas` against the base spell — Magitek records the
SpellData you *passed*, not the action that fired.

Check the claim before writing the workaround:

```csharp
var ids = new uint[]{185,186};
return ids.Select(i => new { Id=i, Name=DataManager.GetSpellData(i)?.LocalizedName,
    HasSpell=ActionManager.HasSpell(i),
    Masked=ActionManager.GetMaskedAction(i)?.LocalizedName,
    CanCast=ActionManager.CanCast(i, Core.Me) }).ToList();
```

If the state you need is transient (a stance on a long cooldown), do not cast it yourself —
ask the user to press it and poll `/rpc` for the aura, then read everything in one shot.

### What these calls cost

`GetMaskedAction` is not a sheet lookup. Measured on a live client, warmed, N=3000:

| call | µs/call |
|---|---:|
| `ActionManager.GetMaskedAction` | ~14.2 |
| `ActionManager.HasSpell` | 0.17 |
| `Core.Me.HasAura` | 0.07 |
| `DataManager.GetSpellData` | 0.04 |

`GetMaskedAction` runs ~350–400× a `GetSpellData`, and costs the same whether the spell
actually masks or not — there is no fast path. That makes it free once per cast (a ~2500 ms
GCD) and expensive inside a per-pulse LINQ predicate or a per-enemy loop. Budget it by call
*frequency*, not by call count.

Benchmark anything you are unsure about rather than guessing — warm it first, and report the
ratio against a known-cheap call, since the ratio survives a different machine and a loaded
raid where the absolute µs will not:

```csharp
var sw = new System.Diagnostics.Stopwatch(); int N = 3000;
for (int i=0;i<200;i++){ var _ = ActionManager.GetMaskedAction(186); }   // warm
sw.Restart(); for (int i=0;i<N;i++){ var x = ActionManager.GetMaskedAction(186); } sw.Stop();
return Math.Round(sw.Elapsed.TotalMilliseconds*1000/N, 3);              // µs/call
```

### Enumerating a job's oGCDs

Two categories, and the second is easy to miss — abilities like Nastrond that can't sit on
a hotbar because they replace another action:

```csharp
DataManager.SpellCache.Values.Where(x =>
       (x.IsPlayerAction && x.SpellType == SpellType.Ability && x.JobTypes.Contains(_job))
    || (x.SpellType == SpellType.System && x.Job == ClassJobType.Adventurer)
    || (!x.IsPlayerAction && x.SpellType == SpellType.Ability
        && (x.Job == _job || (x.JobTypes.Length == 1 && x.JobTypes.Contains(_job)))))
```

Use `AdjustedCooldown` / `AdjustedCastTime`. `BaseCastTime` is offset-encoded garbage
(187500ms for an instant) and `BaseCooldown` is inconsistent.

---

### Generating Spells.cs entries

Turn a list of names from a patch note or job guide straight into declarations. This is
the fastest way to add a job's actions after a patch, and it cannot get an id wrong:

```csharp
string[] spells = { "Fire in Red", "Aero in Green", "Creature Motif", "Pom Muse" };

return spells.Select(n =>
    $"public static readonly SpellData {n.Replace(" ", "")} = " +
    $"DataManager.GetSpellData({DataManager.GetSpellData(n).Id});").ToList();
```

Check for nulls before trusting output — a renamed or unreleased action returns null from
`GetSpellData(name)`.

### Combo and action state

```csharp
ActionManager.ComboTimeLeft
ActionManager.LastSpell.Id
ActionManager.CurrentActions          // what is currently on the bars
ActionManager.HasSpell(id)
ActionManager.CanCast("Swiftcast", Core.Me)   // string overload here too
ActionManager.CanCastOrQueue(spell, target)
```

### PvP combos

PvP combo buttons are driven through a dedicated API, not `DoAction`:

```csharp
var id = ActionManager.GetPvPComboCurrentActionId(65);  // what step 65 is on now
ActionManager.DoPvPCombo(65, Core.Me.CurrentTarget);
```

### Job gauges

`ActionResourceManager.<Job>` for every job:

```csharp
ActionResourceManager.Paladin.Oath
ActionResourceManager.Scholar.Aetherflow
ActionResourceManager.Samurai.Sen / .Kenki / .Meditation / .Kaeshi
ActionResourceManager.Summoner.AvailablePets / .PetTimer / .TranceTimer
ActionResourceManager.CostTypesStruct           // raw cost block
```

### Charges

`Charges` is fractional — integer part is full charges, decimal is progress toward the
next. Time until the next charge:

```csharp
var next = spell.Cooldown.TotalMilliseconds
         - spell.AdjustedCooldown.TotalMilliseconds
           * (spell.MaxCharges - (uint)Math.Floor(spell.Charges) - 1);
```

---

## Enemy cast bars and interrupts

```csharp
var t = (BattleCharacter)Core.Me.CurrentTarget;
t.SpellCastInfo.CastTime
t.SpellCastInfo.CurrentCastTime
t.SpellCastInfo.RemainingCastTime
t.SpellCastInfo.Interruptible     // gates Interject / Head Graze
t.IsBoss()
t.ValidAttackUnit()
```

## Mechanics: VFX, tethers, lock-ons

The visual layer of fight mechanics is readable, which is how you detect a mechanic that
applies no aura:

```csharp
Core.Me.VfxContainer.Omens        // ground AoE telegraphs
Core.Me.VfxContainer.Tethers
Core.Me.VfxContainer.LockOns      // stack / spread markers
```

## Claim and tap state

Who tagged a mob, and whether it is yours:

```csharp
x.Tapped
x.TappedByOther
x.TaggerObjectId
x.TaggerType
PartyManager.VisibleMembers.Any(p => p.GameObject == c.TargetGameObject)
```

## Inventory

```csharp
InventoryManager.FreeSlots
InventoryManager.FilledSlots
InventoryManager.FilledInventoryAndArmory
InventoryManager.EquippedItems

var slot = InventoryManager.FilledSlots.FirstOrDefault(r =>
    string.Equals(r.Name, "Grade 8 Tincture of Intelligence",
                  StringComparison.CurrentCultureIgnoreCase));

slot.CanUse(Core.Me)        // respects cooldown and context
slot.Item.BackingAction     // the SpellData the item invokes
slot.Item.MateriaSlots
slot.TrueItemId             // HQ-aware; RawItemId is not
DataManager.GetItem(id).CurrentLocaleName
```

**`BackingAction` is where an item's *range* lives** — `Item` itself has no range property.
`DataManager.GetItem(4570).BackingAction.Range` → 15 for Phoenix Down.

**It resolves lazily — a first read can return `null` and a second read populate it.** Do not
conclude "this item has no action" from one probe; read it twice, or check a field you expect
to exist. Getting this wrong makes items look structurally different from each other when they
are not, which is an easy way to talk yourself into a false finding.

**`EquipmentCatagory` is too coarse to reason about recast groups; `ItemRole` is finer.** All of
Phoenix Down, Potion, Tincture and Gemdraught report `EquipmentCatagory = Medicine`, but split
on `ItemRole`: Phoenix Down 16, potions 8, tinctures/gemdraughts 6. Wiki phrasing like "shares a
recast with other medicine items" tracks the coarse category and will over-state what actually
shares a timer.

**But the cast time lies.** For the same Phoenix Down action (43336), checked against the
wiki (8s cast, 360s recast, 15y range):

```
Range            = 15       correct
AdjustedCooldown = 360000   correct — the real recast is 6 minutes
Cooldown         = 0        correct — remaining, not duration; 0 just means ready
AdjustedCastTime = 0        WRONG — the real cast is 8 seconds
BaseCastTime     = 187500   the usual offset-encoded garbage
```

So `Range` and `AdjustedCooldown` are trustworthy off an item's `BackingAction`; the cast time
is not, and it fails *silently* as a plausible 0 rather than an obvious garbage value. Verify a
cast time against the wiki or a stopwatch before relying on it. This bites
in Magitek specifically: assigning `Casting.SpellCastTime = item.BackingAction.AdjustedCastTime`
puts a 0 where a duration belongs, which breaks the short-cast/interrupt detection in
`Casting.CheckForSuccessfulCast()` — and because `UseAdvancedSpellHistory2` defaults **true**,
the `AdjustedCastTime == 0 && Cooldown == 0` branch returns early and `CastingTime` never stops.

**`InventoryManager.FilledSlots` is expensive — never put it early in a hot path.** Measured on
a live client with 138 filled slots:

| operation | µs |
|---|---:|
| `FilledSlots.Where(...).Sum(...)` | ~317 |
| `FilledSlots.FirstOrDefault(r => r.RawItemId == id)` | ~290 |
| `FilledSlots.Count()` (walk only, no property read) | ~202 |

Roughly 1.5 µs per slot just to walk it, plus ~1 µs per property read — so `FirstOrDefault`
costs *more* than `Count()`. That is ~20x a `GetMaskedAction` and ~8000x a `GetSpellData`.
Scan the bags once, cache the result, and gate it behind a cheap check first: a `Count` on an
already-built Magitek collection like `Group.DeadAllies` is ~0.012 µs, about 25,000x cheaper.

Benchmark bag operations with a **small N (100–300)**. These run on the game thread, and a few
thousand iterations is enough to stall the bot and time the bridge out.

## Party and alliance

```csharp
PartyManager.RawMembers.Select(r => r?.BattleCharacter)
    .Where(i => i != null && i.IsValid && i.InLineOfSight())

// alliance members are not in PartyManager — find them as targetable PCs
GameObjectManager.GetObjectsOfType<BattleCharacter>()
    .Where(r => r.Type == GameObjectType.Pc && r.IsTargetable && r.InLineOfSight())
```

---

## Geometry

```csharp
Core.Me.Location.Distance(target.Location)
target.Location.Distance3D(Core.Me.Location)
Core.Me.Distance(target) - Core.Me.CombatReach - target.CombatReach  // true edge gap

MathHelper.CalculateHeading(from, to)
Clio.Common.MathEx.CalculateNeededFacing(from, to)
Clio.Common.MathEx.NormalizeRadian(rad)
Clio.Common.MathEx.CalculatePointAtSide(origin, heading, distance, leftSide)
Clio.Common.MathEx.CalculatePointFrom(origin, toward, distance)

Core.Me.Location.RayCast(heading, distance)   // LoS / wall probing
WorldManager.Raycast(start, end, out hit)
```

---

## Instance and duty state

```csharp
DutyManager.InInstance
DirectorManager.ActiveDirector.DirectorType
WorldManager.InSanctuary
PetManager.ActivePetType
```

---

## Game UI

RB can read and drive addons directly:

```csharp
var window = RaptureAtkUnitManager.GetWindowByName("MKDSupportJobList", true);
window.SendAction(1, 1, 0x0);
```

`AgentModule.GetAgentInterfaceById(468)` reaches agents by id.

---

## Fishing and gathering

```csharp
FishingManager.State              // fishing state machine
FishingManager.TugType            // light / medium / heavy
FishingManager.HasPatience
FishingManager.CanHook
FishingManager.CanMoochAny
FishingManager.SelectedBaitItemId

RemoteWindows.Catch.IsOpen
RemoteWindows.Catch.CaughtFish    // may throw - wrap it
RemoteWindows.Catch.FishName      // may throw - wrap it
RemoteWindows.Catch.QualityStars
RemoteWindows.Catch.Elements
```

`Catch.FishName` and `Catch.CaughtFish` throw rather than returning null when the window
is mid-transition. Guard each in its own try/catch; a single wrapper around both loses the
one that would have succeeded.

```csharp
GameObjectManager.GetObjectsOfType<GatheringPointObject>()
    .Where(gpo => gpo.CanGather && gpo.IsVisible)
```

**`GatheringPointObject.NpcId` is not consistently the GatheringPoint row id.** It carries
real row ids for some nodes but every spearfishing "Teeming Waters" node reports `NpcId=21`.
The real row can be read from the object's memory instead, at a patch-sensitive offset.
Verify which you are getting before filtering on it.

## Discovering RB's window classes

`ff14bot.RemoteWindows` has a lot in it and no index. Enumerate it:

```csharp
typeof(ff14bot.RemoteWindows.Catch).Assembly.GetTypes()
    .Where(t => t.Namespace == "ff14bot.RemoteWindows")
    .Select(t => t.Name)
    .ToList()
```

Same trick works for any RB namespace. Pairs well with `scripts/apidump` — that inspects
without loading, this inspects what is actually live.

## Aura history

Don't hand-roll it. `get_aura_history` already samples continuously — a snippet run after
the fact cannot see an aura that has expired.

---

## Reaching into a loaded routine

Whatever routines and libraries are loaded — Magitek, LlamaLibrary, Lisbeth — live in the
same process, so their internals, including private statics, are reachable. This is how you
inspect a routine's live state without adding logging and rebuilding it:

**You cannot write the type name directly.** `Magitek.Utilities.Group.DeadAllies` fails to
compile with `The name 'Magitek' does not exist in the current context` — the routine is
loaded from shadow-copied assemblies the eval compiler does not reference. There are usually
several loaded at once (`Magitek`, `Magitek_379041162.dll`, `ForceMagitek_1165824511.dll`),
so resolve the plain-named one and go through reflection:

```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Magitek");
var st  = asm.GetType("Magitek.Models.Account.BaseSettings");
var inst = st.GetProperty("Instance").GetValue(null);
var g   = asm.GetType("Magitek.Utilities.Globals");
return new {
    MagitekMovement = st.GetProperty("MagitekMovement")?.GetValue(inst),   // true
    AnimationLockMs = g.GetProperty("AnimationLockMs")?.GetValue(null),    // 625
};
```

A `null` back from `GetProperty(...)?` means the member does not exist — check the name
against the source before concluding the value is genuinely null. `typeof(...)` only works
for assemblies the eval compiler already references; for a routine, either use the lookup
above or add a reference with the `//!CompilerOption:AddRef:` directive.

Private statics come out the same way, with the binding flags spelled out. Field names below
are illustrative — read them off the source rather than guessing, since a wrong name returns
`null` and reads as "the value is null":

```csharp
var asm    = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Magitek");
var type   = asm.GetType("Magitek.Utilities.SomeCache");
var field  = type.GetField("_someStatic", BindingFlags.NonPublic | BindingFlags.Static);
var value  = field?.GetValue(null);

// Instance members off a singleton, and methods, work the same way:
var result = value?.GetType().GetMethod("SomeMethod")?.Invoke(value, new object[] { 27569 });
```

When the returned type is unknown, dump it generically rather than guessing:

```csharp
var t = obj.GetType();
return t.GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Select(f => f.Name + "=" + f.GetValue(obj))
        .Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                 .Select(p => p.Name + "=" + p.GetValue(obj)))
        .ToList();
```

Same technique reaches Magitek's settings singletons and cached collections live.

## Reading memory RB does not expose

When RB has no property for something, the UI addon usually holds it. Get the window
pointer and read offsets directly:

```csharp
var w = RaptureAtkUnitManager.GetWindowByName("SpearFishing");
bool  avail = Core.Memory.Read<bool>(w.Pointer + 0x29C);
sbyte size  = Core.Memory.Read<sbyte>(w.Pointer + 0x29C + 0x0A);
short speed = Core.Memory.Read<short>(w.Pointer + 0x29C + 0x0C);
```

To *find* an unknown offset, walk the pointer range and print anything that dereferences:

```csharp
for (int off = 0x40; off <= 0xC0; off += 8)
{
    try
    {
        long val = Core.Memory.Read<long>(w.Pointer + off);
        if (val < 0x10000 || val > 0x7FFFFFFFFFFF) continue;   // reject non-pointers
        Log($"+0x{off:X2} -> 0x{val:X}");
    }
    catch { }
}
```

**Offsets are patch-sensitive** — the `GatheringPointObject` row-id read above is the same
class of thing. Anything found this way needs re-verifying after every game update, so
record where it came from.

## Things to know but not do casually

**In-game echo.** Handy when you want output where you can see it while playing:

```csharp
ChatManager.SendChat($"/echo IsBehind {Core.Me.CurrentTarget.IsBehind}");
```

Real chat, real account. Fine for `/echo`; do not build on it.

**Direct client function calls.** RB exposes pattern scanning and injected calls:

```csharp
var pf = new GreyMagic.PatternFinder(Core.Memory);
var func = pf.Find("Search 40 53 48 83 EC ? 0F B6 DA E8 ? ? ? ? 48 85 C0 74 ? 38 58");
lock (Core.Memory.Executor.AssemblyLock)
    Core.Memory.CallInjected64<IntPtr>(func, agent.Pointer, (byte)2);
```

This calls arbitrary game code. Patterns break every patch, `AssemblyLock` is not optional,
and a wrong signature crashes the client. Only with an explicit request.

**Reloading a combat routine** via reflection into `RoutineManager`'s
`AssemblyLoader<CombatRoutine>`:

```csharp
((AssemblyLoader<CombatRoutine>)field.GetValue(null)).Reload("test");
```

---

## RB compiler directives

RB's script compiler honours comment directives, so a snippet can pull in an assembly RB
has not already loaded:

```csharp
//!CompilerOption:AddRef:PushbulletSharp.dll
```

Rarely needed — the compiler already references everything loaded in the process,
including Magitek and LlamaLibrary.
