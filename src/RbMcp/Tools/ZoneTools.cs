using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ff14bot;
using ff14bot.Managers;

namespace RbMcp.Tools;

/// <summary>
/// Zone context - "where am I and what is true here right now".
///
/// Serves two callers with the same data. For a rotation, zone plus PvP flag plus
/// sanctuary state explains why a routine is or is not acting. For gathering work, the
/// weather forecast plus Eorzea time is the entire window calculation, and RB exposes it
/// via <see cref="WorldManager.GetWeatherForZone"/> with an hour offset - no need to
/// reimplement the weather RNG against a seed table.
/// </summary>
internal static class ZoneTools
{
    public static void Register()
    {
        ToolRegistry.Register(new ToolDef
        {
            Name = "get_zone",
            Description =
                "Current zone context: territory ID and name, sub-zone, current weather, " +
                "Eorzea time, PvP/sanctuary flags, flight capability, and the player's " +
                "position. Start here when asked 'what zone am I in' or 'what's happening here'.",
            InputSchema = Args.Schema(),
            Handler = GetZone
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_weather_forecast",
            Description =
                "Weather forecast for a zone across upcoming Eorzea hours. Weather changes " +
                "on 8-hour Eorzea blocks (about 23m20s real time each). Use for gathering, " +
                "fishing and weather-gated content windows.",
            InputSchema = Args.Schema(
                ("zoneId", "integer", "Territory ID. Defaults to the current zone."),
                ("hours", "integer", "How many Eorzea hours ahead to report. Default 24, max 240.")),
            Handler = GetWeatherForecast
        });

        ToolRegistry.Register(new ToolDef
        {
            Name = "get_fates",
            Description = "Active FATEs in the current zone with name, level, progress and location.",
            InputSchema = Args.Schema(),
            Handler = GetFates
        });
    }

    private static object GetZone(JsonElement args)
    {
        var me = Core.Me;

        return new
        {
            zoneId = WorldManager.ZoneId,
            rawZoneId = WorldManager.RawZoneId,
            subZoneId = WorldManager.SubZoneId,
            zoneName = WorldManager.CurrentZoneName,
            localizedZoneName = WorldManager.CurrentLocalizedZoneName,
            currentWeather = WorldManager.CurrentWeather,
            currentWeatherId = WorldManager.CurrentWeatherId,
            eorzeaTime = WorldManager.EorzaTime.ToString("HH:mm"),
            inPvp = WorldManager.InPvP,
            inSanctuary = WorldManager.InSanctuary,
            canFly = WorldManager.CanFly,
            position = me != null && me.IsValid
                ? new { x = me.Location.X, y = me.Location.Y, z = me.Location.Z, heading = me.Heading }
                : null
        };
    }

    private static object GetWeatherForecast(JsonElement args)
    {
        var zoneId = Args.UInt(args, "zoneId") ?? WorldManager.ZoneId;
        var hours = Math.Clamp(Args.Int(args, "hours", 24), 1, 240);

        var forecast = new List<object>();
        string previous = null;

        for (var offset = 0; offset < hours; offset++)
        {
            string weather;
            try
            {
                weather = WorldManager.GetWeatherForZone(zoneId, offset);
            }
            catch (Exception ex)
            {
                return new { error = $"Weather lookup failed for zone {zoneId}: {ex.Message}" };
            }

            // Only emit transitions. A flat hour-by-hour list is mostly repetition,
            // since weather holds for 8-hour Eorzea blocks.
            if (weather != previous)
            {
                forecast.Add(new
                {
                    hourOffset = offset,
                    eorzeaHour = (WorldManager.EorzaTime.Hour + offset) % 24,
                    weather
                });
                previous = weather;
            }
        }

        return new
        {
            zoneId,
            zoneName = zoneId == WorldManager.ZoneId ? WorldManager.CurrentZoneName : null,
            eorzeaTimeNow = WorldManager.EorzaTime.ToString("HH:mm"),
            hoursRequested = hours,
            note = "Entries mark weather transitions, not every hour.",
            forecast
        };
    }

    private static object GetFates(JsonElement args)
    {
        var fates = FateManager.ActiveFates;
        if (fates == null) return new { count = 0, fates = Array.Empty<object>() };

        var results = fates
            .Where(f => f != null)
            .Select(f => new
            {
                id = f.Id,
                name = f.Name,
                level = f.Level,
                progress = f.Progress,
                status = f.Status.ToString(),
                location = new { x = f.Location.X, y = f.Location.Y, z = f.Location.Z },
                radius = f.Radius,
                timeLeftSeconds = f.TimeLeft.TotalSeconds
            })
            .ToArray();

        return new { count = results.Length, fates = results };
    }
}
