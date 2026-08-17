using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;

namespace RbMcp.Tracking;

/// <summary>
/// A persistent catalogue of every status and action observed on anything nearby.
///
/// <see cref="AuraTracker"/> answers "what just happened to me" over a ten minute ring.
/// This answers a different question: "have I ever seen a status called Doom, and what
/// applied it" - asked hours later, possibly after a restart, about a unit that was never
/// in the party and is long gone.
///
/// Two design decisions follow from that framing:
///
/// 1. It is a catalogue, not a log. Keyed by id, so a two hour session with seventy
///    players collapses to a few hundred rows instead of millions of events. That is what
///    makes it cheap to persist and searchable after the fact.
///
/// 2. Aura reads are budgeted. Dedup shrinks the output but not the input - you still have
///    to look at a unit to notice its auras, and CharacterAuras is an enumeration of lazy
///    memory-read facades. Scanning everyone every tick is thousands of cross-process
///    reads per second on the pulse thread. So a fixed number of units is scanned per
///    tick, round-robin, making cost independent of how crowded the zone is.
///
/// Casts are exempt from the budget because IsCasting plus ActionId is two reads rather
/// than thirty. Scanning them at full width is what lets a newly seen status be attributed
/// to the action that applied it.
/// </summary>
internal static class WorldTracker
{
    /// <summary>Hard cap on units considered per tick, so a hub city cannot blow the scan up.</summary>
    private const int MaxCandidates = 128;

    /// <summary>Only look at things close enough to matter; distance is one cheap vector read.</summary>
    private const float ScanRadius = 50f;

    /// <summary>
    /// How long a cast stays eligible to explain a newly seen status. Application lands
    /// well within a couple of seconds of the cast completing.
    /// </summary>
    private const double CastCorrelationSeconds = 4.0;

    /// <summary>Distinct sources kept per status. The first few tell you what you need.</summary>
    private const int MaxSourcesPerStatus = 4;

    private const int FlushIntervalSeconds = 30;

    private static readonly object Lock = new();
    private static readonly Dictionary<uint, StatusObservation> Statuses = new();
    private static readonly Dictionary<uint, CastObservation> Casts = new();

    /// <summary>Caster object id -> most recent cast, used to attribute new statuses.</summary>
    private static readonly Dictionary<uint, RecentCast> RecentCasts = new();

    /// <summary>Object id -> tick when its auras were last read. Drives the round-robin.</summary>
    private static readonly Dictionary<uint, long> LastAuraScanTick = new();

    private static long _tick;
    private static bool _dirty;
    private static DateTime _lastFlushUtc = DateTime.UtcNow;
    private static bool _loaded;
    private static bool _flushInFlight;

    public static bool Recording => BridgeSettings.Instance.RecordObservations;

    public static string FilePath => PluginPaths.Combine("RbMcp.observations.json");

    public static void SetRecording(bool on)
    {
        BridgeSettings.Instance.RecordObservations = on;
        BridgeSettings.Instance.Save();

        // Getting the ledger to disk at the moment recording stops is the whole point of
        // a stop button; waiting for the flush timer would risk losing the tail on unload.
        if (!on) Flush(force: true);
    }

    /// <summary>Called on the game thread by <c>GameThread.PeriodicWork</c>.</summary>
    public static void Sample()
    {
        if (!_loaded) Load();
        if (!Recording) return;

        var me = Core.Me;
        if (me == null || !me.IsValid) return;

        _tick++;

        var myLocation = me.Location;
        var candidates = GameObjectManager.GameObjects
            .OfType<Character>()
            .Where(c => c != null && c.IsValid)
            .Where(c => c.Type is GameObjectType.Pc or GameObjectType.BattleNpc)
            .Where(c => myLocation.Distance(c.Location) <= ScanRadius)
            .Take(MaxCandidates)
            .ToList();

        if (candidates.Count == 0) return;

        var now = DateTime.UtcNow;
        var zoneId = WorldManager.ZoneId;
        var zoneName = WorldManager.CurrentZoneName;

        lock (Lock)
        {
            ScanCasts(candidates, now, zoneId, zoneName);

            foreach (var unit in SelectAuraScanBatch(
                         candidates, BridgeSettings.Instance.ObservationScanBudget, LastAuraScanTick, _tick))
            {
                LastAuraScanTick[unit.ObjectId] = _tick;
                ScanAuras(unit, now, zoneId, zoneName);
            }

            PruneRecentCasts(now);
        }

        Flush(force: false);
    }

    /// <summary>
    /// Chooses which units get their auras read this tick.
    ///
    /// TODO(design): this is the perf/coverage knob and currently the simplest thing that
    /// works - strict round-robin by least-recently-scanned. See the notes in chat; swap in
    /// whichever policy you prefer.
    /// </summary>
    /// <param name="candidates">Valid nearby units this tick, in ObjectManager order.</param>
    /// <param name="budget">Maximum units to return.</param>
    /// <param name="lastScannedTick">Object id -> tick when last scanned. Absent means never.</param>
    /// <param name="currentTick">Monotonic tick counter.</param>
    private static IEnumerable<Character> SelectAuraScanBatch(
        IReadOnlyList<Character> candidates,
        int budget,
        IReadOnlyDictionary<uint, long> lastScannedTick,
        long currentTick)
    {
        return candidates
            .OrderBy(c => lastScannedTick.TryGetValue(c.ObjectId, out var t) ? t : -1)
            .Take(Math.Max(1, budget));
    }

    private static void ScanCasts(List<Character> candidates, DateTime now, ushort zoneId, string zoneName)
    {
        foreach (var unit in candidates)
        {
            if (!unit.IsCasting) continue;

            var info = unit.SpellCastInfo;
            if (info == null || !info.IsValid) continue;

            var actionId = info.ActionId;
            if (actionId == 0) continue;

            // Keyed by caster so a channelled cast observed across several ticks resolves
            // to one entry rather than re-attributing on every sample.
            RecentCasts[unit.ObjectId] = new RecentCast(actionId, info.Name, now);

            if (!Casts.TryGetValue(actionId, out var row))
            {
                Casts[actionId] = new CastObservation
                {
                    ActionId = actionId,
                    Name = info.Name,
                    CasterName = unit.Name,
                    CasterNpcId = unit.NpcId,
                    CasterKind = unit.Type.ToString(),
                    ZoneId = zoneId,
                    ZoneName = zoneName,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    TimesObserved = 1
                };
                _dirty = true;
                continue;
            }

            row.LastSeenUtc = now;
            row.TimesObserved++;
            _dirty = true;
        }
    }

    private static void ScanAuras(Character unit, DateTime now, ushort zoneId, string zoneName)
    {
        var auras = unit.CharacterAuras;
        if (auras == null) return;

        foreach (var aura in auras)
        {
            if (aura == null) continue;

            if (!Statuses.TryGetValue(aura.Id, out var row))
            {
                row = new StatusObservation
                {
                    Id = aura.Id,
                    Name = aura.Name,
                    IsDebuff = aura.IsDebuff,
                    ZoneId = zoneId,
                    ZoneName = zoneName,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    TimesObserved = 0,
                    Sources = new List<ObservedSource>()
                };
                Statuses[aura.Id] = row;
            }

            row.LastSeenUtc = now;
            row.TimesObserved++;
            _dirty = true;

            if (row.Sources.Count >= MaxSourcesPerStatus) continue;

            // A source is only interesting if it is a combination we have not recorded;
            // the same buff on forty passers-by is one fact, not forty.
            var casterName = ResolveCasterName(aura.CasterId);
            if (row.Sources.Any(s => s.UnitNpcId == unit.NpcId && s.CasterName == casterName))
                continue;

            RecentCasts.TryGetValue(aura.CasterId, out var cast);
            var correlated = cast != null && (now - cast.At).TotalSeconds <= CastCorrelationSeconds;

            row.Sources.Add(new ObservedSource
            {
                UnitName = unit.Name,
                UnitKind = unit.Type.ToString(),
                UnitNpcId = unit.NpcId,
                CasterName = casterName,
                LikelyActionId = correlated ? cast.ActionId : null,
                LikelyActionName = correlated ? cast.Name : null,
                SeenUtc = now
            });
        }
    }

    private static string ResolveCasterName(uint casterId)
    {
        if (casterId == 0) return null;
        var caster = GameObjectManager.GetObjectByObjectId(casterId);
        return caster is { IsValid: true } ? caster.Name : null;
    }

    private static void PruneRecentCasts(DateTime now)
    {
        if (RecentCasts.Count < 64) return;

        var stale = RecentCasts
            .Where(kv => (now - kv.Value.At).TotalSeconds > CastCorrelationSeconds)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in stale) RecentCasts.Remove(id);
    }

    // ---- Query ------------------------------------------------------------------------

    public static object Query(string search, string kind, int limit)
    {
        lock (Lock)
        {
            var statuses = Statuses.Values.AsEnumerable();
            var casts = Casts.Values.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                statuses = statuses.Where(s =>
                    s.Name != null && s.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
                casts = casts.Where(c =>
                    c.Name != null && c.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var wantStatuses = kind != "actions";
            var wantCasts = kind != "statuses";

            return new
            {
                recording = Recording,
                totalStatusesKnown = Statuses.Count,
                totalActionsKnown = Casts.Count,
                ledgerPath = FilePath,
                statuses = wantStatuses
                    ? statuses.OrderByDescending(s => s.LastSeenUtc).Take(limit).Select(s => new
                    {
                        s.Id,
                        s.Name,
                        s.IsDebuff,
                        s.ZoneName,
                        s.TimesObserved,
                        firstSeenUtc = s.FirstSeenUtc,
                        lastSeenUtc = s.LastSeenUtc,
                        sources = s.Sources.Select(src => new
                        {
                            src.UnitName,
                            src.UnitKind,
                            src.UnitNpcId,
                            src.CasterName,
                            src.LikelyActionId,
                            src.LikelyActionName
                        }).ToArray()
                    }).ToArray()
                    : null,
                actions = wantCasts
                    ? casts.OrderByDescending(c => c.LastSeenUtc).Take(limit).Select(c => new
                    {
                        c.ActionId,
                        c.Name,
                        c.CasterName,
                        c.CasterNpcId,
                        c.CasterKind,
                        c.ZoneName,
                        c.TimesObserved,
                        firstSeenUtc = c.FirstSeenUtc,
                        lastSeenUtc = c.LastSeenUtc
                    }).ToArray()
                    : null
            };
        }
    }

    // ---- Persistence ------------------------------------------------------------------

    private static void Load()
    {
        _loaded = true;

        try
        {
            if (!File.Exists(FilePath)) return;

            var ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(FilePath));
            if (ledger == null) return;

            lock (Lock)
            {
                foreach (var s in ledger.Statuses ?? new List<StatusObservation>())
                    Statuses[s.Id] = s;
                foreach (var c in ledger.Actions ?? new List<CastObservation>())
                    Casts[c.ActionId] = c;
            }

            Logging.Write(
                $"[RbMcp] Observation ledger loaded: {Statuses.Count} statuses, {Casts.Count} actions.");
        }
        catch (Exception ex)
        {
            Logging.Write($"[RbMcp] Could not read observation ledger ({ex.Message}); starting empty.");
        }
    }

    public static void Flush(bool force)
    {
        if (!force)
        {
            if (!_dirty) return;
            if ((DateTime.UtcNow - _lastFlushUtc).TotalSeconds < FlushIntervalSeconds) return;
        }

        if (_flushInFlight) return;

        string json;
        lock (Lock)
        {
            if (!_dirty && !force) return;

            json = JsonSerializer.Serialize(
                new Ledger
                {
                    SavedUtc = DateTime.UtcNow,
                    Statuses = Statuses.Values.ToList(),
                    Actions = Casts.Values.ToList()
                },
                new JsonSerializerOptions { WriteIndented = true });

            _dirty = false;
            _lastFlushUtc = DateTime.UtcNow;
        }

        // Serialize under the lock but write off the pulse thread. A synchronous file
        // write here would stall the botbase for however long the disk feels like taking.
        _flushInFlight = true;
        Task.Run(() =>
        {
            try
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logging.Write($"[RbMcp] Could not save observation ledger: {ex.Message}");
            }
            finally
            {
                _flushInFlight = false;
            }
        });
    }

    // ---- Shapes -----------------------------------------------------------------------

    private sealed class Ledger
    {
        public DateTime SavedUtc { get; set; }
        public List<StatusObservation> Statuses { get; set; }
        public List<CastObservation> Actions { get; set; }
    }

    internal sealed class StatusObservation
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public bool IsDebuff { get; set; }
        public ushort ZoneId { get; set; }
        public string ZoneName { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int TimesObserved { get; set; }
        public List<ObservedSource> Sources { get; set; } = new();
    }

    internal sealed class ObservedSource
    {
        public string UnitName { get; set; }
        public string UnitKind { get; set; }
        public uint UnitNpcId { get; set; }
        public string CasterName { get; set; }
        public uint? LikelyActionId { get; set; }
        public string LikelyActionName { get; set; }
        public DateTime SeenUtc { get; set; }
    }

    internal sealed class CastObservation
    {
        public uint ActionId { get; set; }
        public string Name { get; set; }
        public string CasterName { get; set; }
        public uint CasterNpcId { get; set; }
        public string CasterKind { get; set; }
        public ushort ZoneId { get; set; }
        public string ZoneName { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int TimesObserved { get; set; }
    }

    private sealed class RecentCast
    {
        public readonly uint ActionId;
        public readonly string Name;
        public readonly DateTime At;

        public RecentCast(uint actionId, string name, DateTime at)
        {
            ActionId = actionId;
            Name = name;
            At = at;
        }
    }
}
