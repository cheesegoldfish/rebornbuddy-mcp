using System;
using System.Collections.Generic;
using System.Linq;
using ff14bot;
using ff14bot.Objects;

namespace RbMcp.Tracking;

/// <summary>
/// Records aura changes on the player over time.
///
/// This is the one thing the bridge cannot do on demand. "What debuff killed me", "did
/// that mechanic apply a vuln stack", "is my DoT actually being refreshed" are all
/// questions asked *after* the evidence has expired - by the time anyone types them, the
/// aura is gone and a point-in-time read shows nothing.
///
/// So a snapshot is diffed on a timer and the transitions are kept. Cost is one memory
/// read of the player's aura array four times a second.
/// </summary>
internal static class AuraTracker
{
    /// <summary>Ring capacity. At a few events per pull this is many minutes of history.</summary>
    private const int MaxEvents = 1000;

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A refresh only counts if remaining time jumped by more than this. Auras tick down
    /// continuously, so a small increase is sampling jitter rather than a reapplication.
    /// </summary>
    private const float RefreshThresholdSeconds = 1.0f;

    private static readonly object Lock = new();
    private static readonly Queue<AuraEvent> Events = new();
    private static Dictionary<uint, Snapshot> _previous = new();

    private static DateTime _startedUtc = DateTime.UtcNow;

    public static void Reset()
    {
        lock (Lock)
        {
            Events.Clear();
            _previous = new Dictionary<uint, Snapshot>();
            _startedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Called on the game thread by <c>GameThread.PeriodicWork</c>.</summary>
    public static void Sample()
    {
        var me = Core.Me;
        if (me == null || !me.IsValid)
            return;

        var auras = me.CharacterAuras;
        if (auras == null) return;

        var current = new Dictionary<uint, Snapshot>();
        foreach (var aura in auras)
        {
            if (aura == null) continue;
            // Keyed by id: FFXIV does not stack two instances of the same status on one
            // target, so the id is a stable identity across samples.
            current[aura.Id] = new Snapshot(aura.Id, aura.Name, aura.TimeLeft, aura.Value,
                aura.CasterId, aura.IsDebuff);
        }

        var now = DateTime.UtcNow;

        lock (Lock)
        {
            foreach (var kv in current)
            {
                if (!_previous.TryGetValue(kv.Key, out var before))
                {
                    Record(new AuraEvent(now, "gained", kv.Value));
                    continue;
                }

                if (kv.Value.TimeLeft > before.TimeLeft + RefreshThresholdSeconds)
                    Record(new AuraEvent(now, "refreshed", kv.Value));
                else if (kv.Value.Stacks != before.Stacks)
                    Record(new AuraEvent(now, "stacks", kv.Value));
            }

            foreach (var kv in _previous)
            {
                if (!current.ContainsKey(kv.Key))
                    Record(new AuraEvent(now, "lost", kv.Value));
            }

            _previous = current;
        }
    }

    private static void Record(AuraEvent e)
    {
        Events.Enqueue(e);

        while (Events.Count > MaxEvents) Events.Dequeue();

        // Age out from the front; the queue is naturally ordered oldest-first.
        var cutoff = DateTime.UtcNow - MaxAge;
        while (Events.Count > 0 && Events.Peek().At < cutoff) Events.Dequeue();
    }

    public static object GetSince(double seconds, bool debuffsOnly)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-seconds);
        var now = DateTime.UtcNow;

        List<AuraEvent> matching;
        lock (Lock)
        {
            matching = Events
                .Where(e => e.At >= cutoff)
                .Where(e => !debuffsOnly || e.Aura.IsDebuff)
                .ToList();
        }

        var trackedFor = (now - _startedUtc).TotalSeconds;

        return new
        {
            windowSeconds = seconds,
            // Without this a caller cannot distinguish "nothing happened" from "the
            // tracker had only been running two seconds when you asked".
            trackingForSeconds = Math.Round(trackedFor, 1),
            truncated = trackedFor < seconds,
            count = matching.Count,
            events = matching
                .OrderByDescending(e => e.At)
                .Select(e => new
                {
                    secondsAgo = Math.Round((now - e.At).TotalSeconds, 1),
                    change = e.Kind,
                    id = e.Aura.Id,
                    name = e.Aura.Name,
                    isDebuff = e.Aura.IsDebuff,
                    stacks = e.Aura.Stacks,
                    // RB reports TimeLeft as the negated full duration on the sample where
                    // an aura first appears (-30 for a 30s buff), then counts down normally.
                    // The magnitude is right in both cases, so report that.
                    timeLeftAtChange = Math.Round(Math.Abs(e.Aura.TimeLeft), 1),
                    casterId = e.Aura.CasterId
                })
                .ToArray()
        };
    }

    private readonly struct Snapshot
    {
        public readonly uint Id;
        public readonly string Name;
        public readonly float TimeLeft;
        public readonly uint Stacks;
        public readonly uint CasterId;
        public readonly bool IsDebuff;

        public Snapshot(uint id, string name, float timeLeft, uint stacks, uint casterId, bool isDebuff)
        {
            Id = id;
            Name = name;
            TimeLeft = timeLeft;
            Stacks = stacks;
            CasterId = casterId;
            IsDebuff = isDebuff;
        }
    }

    private readonly struct AuraEvent
    {
        public readonly DateTime At;
        public readonly string Kind;
        public readonly Snapshot Aura;

        public AuraEvent(DateTime at, string kind, Snapshot aura)
        {
            At = at;
            Kind = kind;
            Aura = aura;
        }
    }
}
