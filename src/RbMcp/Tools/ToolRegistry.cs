using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RbMcp.Tools;

/// <summary>
/// The bridge's tool surface. Kept as data rather than as an interface hierarchy so the
/// MCP shim can render /tools straight into an MCP tools/list response without the two
/// sides having to agree on anything beyond JSON.
/// </summary>
internal sealed class ToolDef
{
    public string Name { get; init; }
    public string Description { get; init; }

    /// <summary>JSON Schema for the arguments object, surfaced verbatim to MCP clients.</summary>
    public object InputSchema { get; init; }

    /// <summary>
    /// Runs on the game thread unless <see cref="NeedsGameThread"/> is false. Receives the
    /// raw arguments object; use the <see cref="Args"/> helpers to read fields.
    /// </summary>
    public Func<JsonElement, object> Handler { get; init; }

    public bool NeedsGameThread { get; init; } = true;
}

internal static class ToolRegistry
{
    private static readonly Dictionary<string, ToolDef> Tools = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(ToolDef tool) => Tools[tool.Name] = tool;

    public static void RegisterAll()
    {
        Tools.Clear();
        DataTools.Register();
        ZoneTools.Register();
        StateTools.Register();
        EvalTools.Register();
    }

    public static bool TryGet(string name, out ToolDef tool) => Tools.TryGetValue(name ?? string.Empty, out tool);

    public static int Count => Tools.Count;

    /// <summary>Shape matches MCP's tools/list entries so the shim is a pass-through.</summary>
    public static object Describe() => new
    {
        tools = Tools.Values
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema
            })
            .ToArray()
    };
}

/// <summary>Small readers over the incoming arguments object.</summary>
internal static class Args
{
    public static string String(JsonElement args, string name, string fallback = null)
        => args.ValueKind == JsonValueKind.Object &&
           args.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : fallback;

    public static int Int(JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object &&
           args.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.Number &&
           v.TryGetInt32(out var i)
            ? i
            : fallback;

    public static uint? UInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
            return null;

        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && uint.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static bool Bool(JsonElement args, string name, bool fallback = false)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
            return fallback;

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    /// <summary>
    /// Whether the caller actually supplied a key, as opposed to it defaulting.
    ///
    /// Matters for tools that *set* something rather than read it. A misspelled argument to
    /// a getter costs you a default-shaped answer you will notice; a misspelled argument to
    /// a setter silently writes the default and reports success. That happened for real:
    /// `set_recording {"on": true}` parsed as `enabled = false` and turned recording off
    /// while returning ok.
    /// </summary>
    public static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out _);

    /// <summary>Schema helper - keeps tool definitions readable.</summary>
    public static object Schema(params (string Name, string Type, string Description)[] properties)
        => new
        {
            type = "object",
            properties = properties.ToDictionary(
                p => p.Name,
                p => (object)new { type = p.Type, description = p.Description })
        };
}
