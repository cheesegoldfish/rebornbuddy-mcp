using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace RbMcp.Http;

/// <summary>
/// Serialization boundary for everything leaving the bridge.
///
/// The important part is <see cref="Sanitize"/>, not the serializer settings. RB game
/// objects are lazy facades over another process's memory: every property getter is a
/// real cross-process read. Handing a BattleCharacter to any reflection-based serializer
/// turns one HTTP request into thousands of memory reads, and the object graph is cyclic
/// besides (target -> target.CurrentTarget -> ...).
///
/// So we never let the serializer walk an RB type. Tools project into anonymous types or
/// dictionaries, which serialize cleanly; anything else falls back to ToString(). Depth
/// and sequence length are capped so a careless eval snippet degrades into a truncated
/// answer rather than a hung request.
/// </summary>
internal static class Json
{
    private const int MaxDepth = 6;
    private const int MaxSequenceItems = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        MaxDepth = 32
    };

    public static string Serialize(object value)
        => JsonSerializer.Serialize(Sanitize(value, 0), Options);

    public static JsonDocument Parse(string text) => JsonDocument.Parse(text);

    /// <summary>
    /// Converts an arbitrary runtime value into something safe to hand to the serializer.
    /// Types we recognise pass through structurally; anything unknown - which in practice
    /// means anything from the ff14bot namespace - is reduced to its string form.
    /// </summary>
    public static object Sanitize(object value, int depth)
    {
        if (value == null) return null;
        if (depth > MaxDepth) return Describe(value);

        switch (value)
        {
            case string s: return s;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal:
                return value;
            case Enum e:
                return e.ToString();
            case DateTime dt:
                return dt.ToString("o");
            case TimeSpan ts:
                return ts.TotalMilliseconds;
            case Guid g:
                return g.ToString();
            case Vector3 v:
                return new Dictionary<string, object> { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
        }

        var type = value.GetType();

        if (value is IDictionary dict)
        {
            var result = new Dictionary<string, object>();
            var n = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (n++ >= MaxSequenceItems) { result["__truncated"] = true; break; }
                result[entry.Key?.ToString() ?? "null"] = Sanitize(entry.Value, depth + 1);
            }

            return result;
        }

        if (value is IEnumerable seq)
        {
            var list = new List<object>();
            foreach (var item in seq)
            {
                if (list.Count >= MaxSequenceItems) { list.Add("__truncated"); break; }
                list.Add(Sanitize(item, depth + 1));
            }

            return list;
        }

        // Anonymous types and our own DTOs are safe to walk - they are plain CLR objects
        // that tools built deliberately. Everything else is an RB memory facade.
        if (IsSafeToWalk(type))
        {
            var result = new Dictionary<string, object>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try { result[p.Name] = Sanitize(p.GetValue(value), depth + 1); }
                catch (Exception ex) { result[p.Name] = $"<error: {ex.GetBaseException().Message}>"; }
            }

            return result;
        }

        return Describe(value);
    }

    private static bool IsSafeToWalk(Type type)
    {
        if (IsAnonymous(type)) return true;

        var ns = type.Namespace ?? string.Empty;
        return ns.StartsWith("RbMcp", StringComparison.Ordinal)
               || ns.StartsWith("System.Tuple", StringComparison.Ordinal)
               || type.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true;
    }

    private static bool IsAnonymous(Type type)
        => type.IsGenericType
           && type.Name.Contains("AnonymousType")
           && type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Any();

    private static object Describe(object value)
    {
        try
        {
            return new Dictionary<string, object>
            {
                ["__type"] = value.GetType().FullName,
                ["value"] = value.ToString()
            };
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.GetBaseException().Message}>";
        }
    }
}
