using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;

namespace RbMcp.Tools;

/// <summary>
/// Tier 2 - live character and world state. This is the rotation-debugging tier: it turns
/// "why didn't it cast that" from a guess into a question with an answer.
///
/// Every projection here is a hand-written DTO on purpose. RB objects are lazy facades
/// over the game's memory, so reflecting over one costs a cross-process read per property
/// and can wander into a cyclic graph. Naming the fields keeps the cost bounded and
/// predictable.
/// </summary>
internal static class StateTools
{
    /// <summary>Caps <c>get_nearby</c> so a crowded hub city cannot produce a huge payload.</summary>
    private const int MaxNearbyResults = 60;

    public static void Register()
    {
        ToolRegistry.Register(new ToolDef
        {
            Name = "get_player",
            Description =
                "Live player state: name, job, level, HP/MP/GP/CP, position, combat and cast " +
                "state, level sync, and all active auras with time remaining. The first thing " +
                "to call when debugging why a rotation did or did not fire.",
            InputSchema = Args.Schema(
                ("includeAuras", "boolean", "Include the full aura list. Default true.")),
            Handler = GetPlayer
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_target",
            Description =
                "Current target: name, HP, distance, combat reach, whether we are behind or " +
                "flanking (positional checks), and its auras.",
            InputSchema = Args.Schema(
                ("includeAuras", "boolean", "Include the target's aura list. Default true.")),
            Handler = GetTarget
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_party",
            Description = "Party members with job, level, HP percent, distance and alive state.",
            InputSchema = Args.Schema(),
            Handler = GetParty
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_nearby",
            Description =
                "Nearby game objects within a radius, nearest first. Use to check AoE enemy " +
                "counts, find an NPC, or see what is around you.",
            InputSchema = Args.Schema(
                ("radius", "integer", "Search radius in yalms. Default 30."),
                ("type", "string", "Optional filter: BattleNpc, Pc, EventNpc, Treasure, GatheringPoint, Aetheryte."),
                ("attackableOnly", "boolean", "Only include units we can attack. Default false."),
                ("limit", "integer", "Max rows. Default 30.")),
            Handler = GetNearby
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_aura_history",
            Description =
                "Auras gained, lost, refreshed or restacked on the player over the last N " +
                "seconds, newest first. Answers questions the current state cannot: what " +
                "debuff hit me, what killed me, did that mechanic apply a vuln, is my DoT " +
                "actually being refreshed. Sampled continuously since the plugin was " +
                "enabled; check 'truncated' to see whether the window predates tracking.",
            InputSchema = Args.Schema(
                ("seconds", "integer", "How far back to look. Default 30, max 600."),
                ("debuffsOnly", "boolean", "Only report debuffs. Default false.")),
            // Reads a snapshot the tracker already captured, so no game access needed.
            NeedsGameThread = false,
            Handler = args => Tracking.AuraTracker.GetSince(
                Math.Clamp(Args.Int(args, "seconds", 30), 1, 600),
                Args.Bool(args, "debuffsOnly"))
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "find_observed",
            Description =
                "Search the observation ledger - every status and action seen on ANY nearby " +
                "unit, not just the party, catalogued across the whole play session and " +
                "persisted across restarts. This is how you identify a status you saw but " +
                "could not name: play, then search for it. Statuses carry the units that had " +
                "them and, when it could be correlated, the action that applied it. Requires " +
                "recording to have been on; see set_recording.",
            InputSchema = Args.Schema(
                ("search", "string", "Substring to match against status or action name. Omit to list everything."),
                ("kind", "string", "Limit results: 'statuses', 'actions', or omit for both."),
                ("limit", "integer", "Max rows per kind. Default 40.")),
            // Reads the ledger the tracker already built, so no game access needed.
            NeedsGameThread = false,
            Handler = args => Tracking.WorldTracker.Query(
                Args.String(args, "search"),
                Args.String(args, "kind"),
                Math.Clamp(Args.Int(args, "limit", 40), 1, 500))
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "set_recording",
            Description =
                "Turn observation recording on or off, and report what the ledger currently " +
                "holds. Recording samples nearby units continuously, so leave it off unless " +
                "you are trying to catalogue something. The setting persists across restarts.",
            InputSchema = Args.Schema(
                ("enabled", "boolean", "True to start recording, false to stop and flush to disk.")),
            NeedsGameThread = false,
            Handler = args =>
            {
                // Refuse rather than default. This is a setter, so a missing or misspelled
                // key would otherwise write `false` and report success - which is exactly
                // how a call meant to start recording once silently stopped it.
                if (!Args.Has(args, "enabled"))
                {
                    return new
                    {
                        ok = false,
                        error = "set_recording requires 'enabled'. Pass {\"enabled\": true} or " +
                                "{\"enabled\": false} - it is not defaulted, because guessing " +
                                "would silently change the setting."
                    };
                }

                Tracking.WorldTracker.SetRecording(Args.Bool(args, "enabled"));
                return Tracking.WorldTracker.Query(null, "none", 0);
            }
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_cooldowns",
            Description =
                "Cooldown state for the current job's actions: remaining cooldown, charges, " +
                "and whether each is ready. Only returns actions the character currently knows, " +
                "so it reflects level sync.",
            InputSchema = Args.Schema(
                ("readyOnly", "boolean", "Only list actions that are off cooldown. Default false.")),
            Handler = GetCooldowns
        });
    }

    private static object GetPlayer(JsonElement args)
    {
        var me = Core.Me;
        if (me == null || !me.IsValid)
            return new { available = false, message = "No valid local player. Is the character logged in?" };

        var includeAuras = Args.Bool(args, "includeAuras", true);

        return new
        {
            available = true,
            name = me.Name,
            job = me.CurrentJob.ToString(),
            level = me.ClassLevel,
            isLevelSynced = me.IsLevelSynced,
            health = new { current = me.CurrentHealth, max = me.MaxHealth, percent = me.CurrentHealthPercent },
            mana = new { current = me.CurrentMana, max = me.MaxMana, percent = me.CurrentManaPercent },
            // Only meaningful on gatherers/crafters. On a combat job RB reports a stale
            // CurrentGP against MaxGP of 0, which reads as a real value but is not one.
            gp = me.MaxGP > 0 ? new { current = me.CurrentGP, max = me.MaxGP } : null,
            cp = me.MaxCP > 0 ? new { current = me.CurrentCP, max = me.MaxCP } : null,
            inCombat = me.InCombat,
            isAlive = me.IsAlive,
            isCasting = me.IsCasting,
            castingSpellId = me.IsCasting ? me.CastingSpellId : 0,
            hasTarget = me.HasTarget,
            targetId = me.CurrentTargetObjId,
            position = new { x = me.Location.X, y = me.Location.Y, z = me.Location.Z, heading = me.Heading },
            auras = includeAuras ? ProjectAuras(me) : null
        };
    }

    private static object GetTarget(JsonElement args)
    {
        var me = Core.Me;
        if (me == null || !me.IsValid)
            return new { available = false, message = "No valid local player." };

        var target = me.CurrentTarget;
        if (target == null || !target.IsValid)
            return new { available = false, message = "No current target." };

        var includeAuras = Args.Bool(args, "includeAuras", true);
        var character = target as Character;

        return new
        {
            available = true,
            name = target.Name,
            englishName = target.EnglishName,
            objectId = target.ObjectId,
            npcId = target.NpcId,
            type = target.Type.ToString(),
            health = new
            {
                current = target.CurrentHealth,
                max = target.MaxHealth,
                percent = target.CurrentHealthPercent
            },
            distance = me.Location.Distance(target.Location),
            combatReach = target.CombatReach,
            isTargetable = target.IsTargetable,
            canAttack = target.CanAttack,
            // Positional state - the reason many melee GCDs pick one branch over another.
            isBehind = target.IsBehind,
            isFlanking = target.IsFlanking,
            inCombat = character?.InCombat,
            isCasting = character?.IsCasting,
            castingSpellId = character != null && character.IsCasting ? character.CastingSpellId : 0,
            auras = includeAuras && character != null ? ProjectAuras(character) : null
        };
    }

    private static object GetParty(JsonElement args)
    {
        if (!PartyManager.IsInParty)
            return new { inParty = false, count = 0, members = Array.Empty<object>() };

        var me = Core.Me;

        var members = PartyManager.VisibleMembers
            .Where(m => m != null)
            .Select(m =>
            {
                var bc = m.BattleCharacter;
                return new
                {
                    name = m.Name,
                    objectId = m.ObjectId,
                    job = bc?.CurrentJob.ToString(),
                    level = bc?.ClassLevel,
                    healthPercent = bc?.CurrentHealthPercent,
                    manaPercent = bc?.CurrentManaPercent,
                    isAlive = bc?.IsAlive,
                    inCombat = bc?.InCombat,
                    distance = bc != null && me != null ? me.Location.Distance(bc.Location) : (float?)null
                };
            })
            .ToArray();

        return new
        {
            inParty = true,
            count = PartyManager.NumMembers,
            isLeader = PartyManager.IsPartyLeader,
            crossRealm = PartyManager.CrossRealm,
            members
        };
    }

    private static object GetNearby(JsonElement args)
    {
        var me = Core.Me;
        if (me == null || !me.IsValid)
            return new { available = false, message = "No valid local player." };

        var radius = Math.Clamp(Args.Int(args, "radius", 30), 1, 200);
        var typeFilter = Args.String(args, "type");
        var attackableOnly = Args.Bool(args, "attackableOnly");
        var limit = Math.Clamp(Args.Int(args, "limit", 30), 1, MaxNearbyResults);

        var objects = GameObjectManager.GameObjects
            .Where(o => o != null && o.IsValid && !o.IsMe)
            .Where(o => typeFilter == null ||
                        o.Type.ToString().IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(o => !attackableOnly || o.CanAttack)
            .Select(o => new { obj = o, distance = me.Location.Distance(o.Location) })
            .Where(x => x.distance <= radius)
            .OrderBy(x => x.distance)
            .Take(limit)
            .Select(x => new
            {
                name = x.obj.Name,
                objectId = x.obj.ObjectId,
                npcId = x.obj.NpcId,
                type = x.obj.Type.ToString(),
                distance = x.distance,
                healthPercent = x.obj.CurrentHealthPercent,
                isTargetable = x.obj.IsTargetable,
                canAttack = x.obj.CanAttack,
                position = new { x = x.obj.Location.X, y = x.obj.Location.Y, z = x.obj.Location.Z }
            })
            .ToArray();

        return new { available = true, radius, count = objects.Length, objects };
    }

    private static object GetCooldowns(JsonElement args)
    {
        var me = Core.Me;
        if (me == null || !me.IsValid)
            return new { available = false, message = "No valid local player." };

        var readyOnly = Args.Bool(args, "readyOnly");
        var job = me.CurrentJob;
        var results = new List<object>();

        foreach (var kv in DataManager.SpellCache)
        {
            var spell = kv.Value;
            if (spell == null || !spell.IsPlayerAction) continue;
            if (spell.Job != job) continue;

            // ActionManager is the authority on what the character can actually use right
            // now - it accounts for level sync, which LevelAcquired alone does not.
            if (!ActionManager.HasSpell(spell.Id)) continue;

            var remaining = spell.Cooldown.TotalMilliseconds;
            var ready = remaining <= 0;
            if (readyOnly && !ready) continue;

            results.Add(new
            {
                id = spell.Id,
                name = spell.Name,
                ready,
                cooldownRemainingMs = remaining,
                charges = spell.Charges,
                maxCharges = spell.MaxCharges,
                levelAcquired = spell.LevelAcquired
            });
        }

        return new
        {
            available = true,
            job = job.ToString(),
            level = me.ClassLevel,
            isLevelSynced = me.IsLevelSynced,
            count = results.Count,
            actions = results
        };
    }

    private static object[] ProjectAuras(Character character)
    {
        var auras = character.CharacterAuras;
        if (auras == null) return Array.Empty<object>();

        return auras
            .Where(a => a != null)
            .Select(a => new
            {
                id = a.Id,
                name = a.Name,
                isDebuff = a.IsDebuff,
                stacks = a.Value,
                timeLeftSeconds = a.TimeLeft,
                casterId = a.CasterId,
                // Whether we applied it matters constantly: DoT refresh logic keys off
                // "my aura", not "any aura with this ID".
                isMine = a.CasterId == Core.Me.ObjectId
            })
            .ToArray();
    }
}
