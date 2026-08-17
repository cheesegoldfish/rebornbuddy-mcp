using System;
using System.Text.Json;
using RbMcp.Eval;
using RbMcp.Threading;
using ff14bot.Helpers;

namespace RbMcp.Tools;

/// <summary>
/// Tier 3 - arbitrary C# against the live RebornBuddy API.
///
/// This subsumes every other tool, which is exactly why the others exist: a named tool with
/// a schema is cheaper and more predictable than a snippet. Reach for eval when no tool
/// covers the question, then consider promoting the snippet into a real tool if you find
/// yourself sending it twice.
///
/// There is no capability filter, by design. An earlier version kept a syntax-level denylist
/// over things like DoAction and Navigator; it was removed because it defended against the
/// wrong party. The caller is an agent working on your behalf under your supervision, and
/// the reason to run a bot-development tool at all is to let it drive the client - cast
/// something, walk somewhere, reflect over Lisbeth or Magitek internals - and report what
/// happened. Restricting that gutted the tool while stopping nobody, since a denylist
/// matching names as written cannot see through reflection anyway.
///
/// The boundary that replaced it is <see cref="Http.Guard"/>: authenticate the caller, then
/// trust them completely.
/// </summary>
internal static class EvalTools
{
    public static void Register()
    {
        ToolRegistry.Register(new ToolDef
        {
            Name = "eval_csharp",
            Description =
                "Execute a C# snippet inside the running RebornBuddy process and return the " +
                "result as JSON. The ff14bot namespaces (Core, GameObjectManager, DataManager, " +
                "WorldManager, ActionManager, PartyManager, Chocobo...) plus System and LINQ are " +
                "in scope, along with any routine loaded in the process - Magitek, Lisbeth, " +
                "LlamaLibrary - reachable by reflection over AppDomain assemblies. " +
                "A bare expression is fine ('Core.Me.Name'); so is a statement body ending in " +
                "'return x;'. Project into anonymous types - returning a raw ff14bot object " +
                "yields only its ToString(), because walking one costs a memory read per " +
                "property. Nothing is filtered: snippets can issue actions and move the " +
                "character on a live account, so keep them read-only unless the user asked " +
                "for something to happen.",
            InputSchema = Args.Schema(
                ("code", "string", "C# expression or statement body to execute.")),
            // Compilation happens off the game thread; only the invocation is marshalled.
            NeedsGameThread = false,
            Handler = Eval
        });
    }

    private static object Eval(JsonElement args)
    {
        var code = Args.String(args, "code");
        if (string.IsNullOrWhiteSpace(code))
            return new { ok = false, error = "No 'code' provided." };

        Audit(code);

        System.Reflection.MethodInfo compiled;
        try
        {
            compiled = ScriptHost.Compile(code);
        }
        catch (ScriptCompileException ex)
        {
            return new { ok = false, error = ex.Message, compileError = true };
        }

        try
        {
            var result = GameThread
                .Invoke(() => ScriptHost.Invoke(compiled), BridgeSettings.Instance.EvalTimeoutMs)
                .GetAwaiter()
                .GetResult();

            return new { ok = true, result = Http.Json.Sanitize(result, 0) };
        }
        catch (Exception ex)
        {
            var root = ex.GetBaseException();
            return new { ok = false, error = $"{root.GetType().Name}: {root.Message}" };
        }
    }

    /// <summary>
    /// Names worth seeing in the log in full, every time, whatever the verbosity setting.
    ///
    /// This is a mirror, not a gate - the snippet runs either way. The list is short on
    /// purpose: it is for calls whose effects land outside your own client and cannot be
    /// taken back. Chat is the whole category, because it speaks as you to real people who
    /// have no idea an agent is driving.
    /// </summary>
    private static readonly string[] Loud = { "SendChat", "SendMessage" };

    private static void Audit(string code)
    {
        if (BridgeSettings.Instance.VerboseLogging)
            Logging.Write($"[RbMcp] eval: {code}");

        foreach (var name in Loud)
        {
            if (code.IndexOf(name, StringComparison.Ordinal) < 0) continue;

            Logging.Write($"[RbMcp] NOTICE - eval snippet references {name}. Full snippet:");
            Logging.Write($"[RbMcp]   {code}");
            return;
        }
    }
}
