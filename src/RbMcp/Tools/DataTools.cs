using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;

namespace RbMcp.Tools;

/// <summary>
/// Tier 1 - static game data. Answers "what is the ID of X" from the same
/// <see cref="DataManager"/> caches a routine reads at runtime.
///
/// This is the tier that replaces scraping xivapi/ffxiv-datamining CSVs after a patch.
/// Those lag the patch by 1-3 days and describe the game's static sheets; DataManager
/// describes what your client actually loaded. When the two disagree, DataManager is the
/// one that matters, because it is what Spells.cs will resolve against.
/// </summary>
internal static class DataTools
{
    public static void Register()
    {
        ToolRegistry.Register(new ToolDef
        {
            Name = "lookup_action",
            Description =
                "Look up a single FFXIV action/spell by ID or exact name. Returns ID, name, " +
                "job, level acquired, range, radius, cast time, cooldown, charges and whether " +
                "it is a PvP action. Prefer this over datamining CSVs - it reads the client's " +
                "own data, so it is correct the moment a patch lands.",
            InputSchema = Args.Schema(
                ("id", "integer", "Action ID. Takes precedence over name."),
                ("name", "string", "Exact action name, e.g. 'Mountain Buster'."),
                ("pvp", "boolean", "When matching by name, prefer the PvP variant.")),
            Handler = LookupAction
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "search_actions",
            Description =
                "Fuzzy-search actions by substring. Use when you know roughly what an ability " +
                "is called, or to enumerate a job's actions. Returns compact rows.",
            InputSchema = Args.Schema(
                ("query", "string", "Substring to match against the action name (case-insensitive)."),
                ("job", "string", "Optional job filter, e.g. 'Summoner' or 'SMN'."),
                ("pvp", "boolean", "Only return PvP actions."),
                ("limit", "integer", "Max rows. Default 40.")),
            Handler = SearchActions
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "lookup_status",
            Description =
                "Look up a status effect (buff/debuff, called an 'aura' in RebornBuddy) by ID " +
                "or exact name. Returns ID, name, max stacks and whether it is dispellable.",
            InputSchema = Args.Schema(
                ("id", "integer", "Status ID. Takes precedence over name."),
                ("name", "string", "Exact status name, e.g. 'Radiant Aegis'.")),
            Handler = LookupStatus
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "search_statuses",
            Description = "Fuzzy-search status effects by substring. Returns compact rows.",
            InputSchema = Args.Schema(
                ("query", "string", "Substring to match against the status name."),
                ("limit", "integer", "Max rows. Default 40.")),
            Handler = SearchStatuses
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "search_items",
            Description =
                "Search items by substring or look one up by exact name. Returns ID, name, " +
                "item level, rarity and stack size.",
            InputSchema = Args.Schema(
                ("query", "string", "Substring to match against the item name."),
                ("limit", "integer", "Max rows. Default 40.")),
            Handler = SearchItems
        });
    }

    private static object LookupAction(JsonElement args)
    {
        var id = Args.UInt(args, "id");
        var name = Args.String(args, "name");
        var wantPvp = Args.Bool(args, "pvp");

        if (id.HasValue)
        {
            return DataManager.SpellCache.TryGetValue(id.Value, out var byId)
                ? (object)Project(byId)
                : new { found = false, message = $"No action with ID {id.Value}." };
        }

        if (string.IsNullOrWhiteSpace(name))
            return new { found = false, message = "Provide either 'id' or 'name'." };

        // PvE and PvP actions frequently share a display name, so an exact-name hit is
        // ambiguous by construction. Collect every match and let 'pvp' disambiguate.
        var matches = Enumerate(DataManager.SpellCache)
            .Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return new
            {
                found = false,
                message = $"No action named '{name}'. Try search_actions for a substring match."
            };
        }

        var chosen = matches.FirstOrDefault(s => s.IsPvP == wantPvp) ?? matches[0];

        return new
        {
            found = true,
            action = Project(chosen),
            // Surfaced so the caller notices when a name is overloaded rather than
            // silently taking the wrong variant.
            otherMatches = matches.Count > 1
                ? matches.Where(m => m.Id != chosen.Id)
                    .Select(m => new { id = m.Id, isPvp = m.IsPvP, job = m.Job.ToString() })
                    .ToArray()
                : null
        };
    }

    private static object SearchActions(JsonElement args)
    {
        var query = Args.String(args, "query", string.Empty) ?? string.Empty;
        var jobFilter = Args.String(args, "job");
        var pvpOnly = Args.Bool(args, "pvp");
        var limit = Math.Clamp(Args.Int(args, "limit", 40), 1, 300);

        var results = Enumerate(DataManager.SpellCache)
            .Where(s => query.Length == 0 ||
                        (s.Name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            .Where(s => !pvpOnly || s.IsPvP)
            .Where(s => jobFilter == null || MatchesJob(s, jobFilter))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(s => new
            {
                id = s.Id,
                name = s.Name,
                job = s.Job.ToString(),
                level = s.LevelAcquired,
                isPvp = s.IsPvP
            })
            .ToArray();

        return new { count = results.Length, limit, results };
    }

    private static object LookupStatus(JsonElement args)
    {
        var id = Args.UInt(args, "id");
        var name = Args.String(args, "name");

        if (id.HasValue)
        {
            var result = DataManager.GetAuraResultById(id.Value);
            return result != null
                ? (object)new { found = true, status = Project(result) }
                : new { found = false, message = $"No status with ID {id.Value}." };
        }

        if (string.IsNullOrWhiteSpace(name))
            return new { found = false, message = "Provide either 'id' or 'name'." };

        var matches = Enumerate(DataManager.AuraCache)
            .Where(a => string.Equals(a.CurrentLocaleName, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.EnglishName, name, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .Select(Project)
            .ToArray();

        return matches.Length > 0
            ? (object)new { found = true, matches }
            : new { found = false, message = $"No status named '{name}'. Try search_statuses." };
    }

    private static object SearchStatuses(JsonElement args)
    {
        var query = Args.String(args, "query", string.Empty) ?? string.Empty;
        var limit = Math.Clamp(Args.Int(args, "limit", 40), 1, 300);

        var results = Enumerate(DataManager.AuraCache)
            .Where(a => query.Length == 0 ||
                        (a.CurrentLocaleName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (a.EnglishName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(a => a.CurrentLocaleName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(Project)
            .ToArray();

        return new { count = results.Length, limit, results };
    }

    private static object SearchItems(JsonElement args)
    {
        var query = Args.String(args, "query", string.Empty) ?? string.Empty;
        var limit = Math.Clamp(Args.Int(args, "limit", 40), 1, 300);

        var results = new List<object>();
        foreach (var kv in DataManager.ItemCache)
        {
            if (results.Count >= limit) break;

            var item = kv.Value;
            if (item == null) continue;

            var itemName = item.CurrentLocaleName ?? item.EnglishName;
            if (query.Length > 0 &&
                (itemName?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                continue;

            results.Add(new
            {
                id = kv.Key,
                name = itemName,
                englishName = item.EnglishName,
                itemLevel = item.ItemLevel,
                rarity = item.Rarity,
                stackSize = item.StackSize
            });
        }

        return new { count = results.Count, limit, results };
    }

    private static bool MatchesJob(SpellData spell, string filter)
    {
        if (spell.Job.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return spell.JobTypes != null &&
               spell.JobTypes.Any(j => j.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static object Project(SpellData s) => new
    {
        id = s.Id,
        name = s.Name,
        localizedName = s.LocalizedName,
        description = s.Description,
        job = s.Job.ToString(),
        jobs = s.JobTypes?.Select(j => j.ToString()).ToArray(),
        levelAcquired = s.LevelAcquired,
        isPvp = s.IsPvP,
        isPlayerAction = s.IsPlayerAction,
        spellType = s.SpellType.ToString(),
        range = s.Range,
        radius = s.Radius,
        effectRange = s.EffectRange,
        groundTarget = s.GroundTarget,
        // Only the Adjusted* values are reported. SpellData.BaseCastTime is offset-encoded
        // internally - it reads 187500ms for an instant and 190500ms for a 3s cast - and
        // BaseCooldown is inconsistent across spells (0 for Fast Blade, 1500 for Holy
        // Spirit, both of which are 2.5s GCDs). Adjusted* is correct and is what a
        // rotation should reason about anyway, since it folds in Skill/Spell Speed.
        adjustedCastTimeMs = s.AdjustedCastTime.TotalMilliseconds,
        adjustedCooldownMs = s.AdjustedCooldown.TotalMilliseconds,
        maxCharges = s.MaxCharges,
        comboSpellId = s.ComboSpellId,
        cost = s.Cost,
        costType = s.CostType.ToString()
    };

    private static object Project(AuraResult a) => new
    {
        id = a.Id,
        name = a.CurrentLocaleName,
        englishName = a.EnglishName,
        maxStacks = a.MaxStacks,
        isDispellable = a.IsDispellable,
        iconId = a.IconId,
        flags = a.Flags
    };

    /// <summary>
    /// LocalizedDictionary exposes GetEnumerator() but does not implement IEnumerable,
    /// so foreach binds to it while LINQ does not. Bridge it once here.
    /// </summary>
    private static IEnumerable<TValue> Enumerate<TKey, TValue>(
        ff14bot.Helpers.LocalizedDictionary<TKey, TValue> dict)
    {
        foreach (var kv in dict)
            yield return kv.Value;
    }
}
