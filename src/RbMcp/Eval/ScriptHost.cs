using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Clio.Utilities.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RbMcp.Eval;

/// <summary>
/// Compiles and runs a C# snippet against the live RebornBuddy API.
///
/// Uses RB's own <see cref="CodeCompiler"/> rather than bringing our own Roslyn. RB has
/// Roslyn ILMerged into RebornBuddy.dll, so its compiler already references every
/// assembly loaded in the host - including Magitek, LlamaLibrary and any other routine
/// once loaded. Bringing a second Roslyn would mean a second set of type identities and
/// we would not be able to read diagnostics off RB's own CompileResult.
///
/// This is also the hot-reload story: a snippet is compiled fresh on every call, so
/// iterating on logic costs a round trip rather than an RB restart.
/// </summary>
internal static class ScriptHost
{
    private const string Prefix = @"
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.BotBases;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.RemoteAgents;
using ff14bot.RemoteWindows;

public static class RbMcpScript
{
    public static async Task<object> Run()
    {
";

    private const string Postfix = @"
        return null;
    }
}
";

    private static readonly int PrefixLines = Prefix.Count(c => c == '\n');

    /// <summary>
    /// Compiles the snippet. Deliberately separate from <see cref="Invoke"/> because
    /// Roslyn takes long enough that doing it on the game thread would stall the botbase
    /// pulse. Compilation touches no game state, so it is safe off-thread.
    /// </summary>
    public static MethodInfo Compile(string code)
    {
        var source = new StringBuilder()
            .Append(Prefix)
            .Append(Normalize(code))
            .Append(Postfix)
            .ToString();

        var compiled = CodeCompiler.CompileScript($"RbMcpScript_{Guid.NewGuid():N}", source);

        if (!compiled.Result.Success)
            throw new ScriptCompileException(Describe(compiled.Result.Diagnostics));

        var type = compiled.Assembly.GetType("RbMcpScript")
                   ?? throw new ScriptCompileException("Compiled assembly did not contain RbMcpScript.");

        return type.GetMethod("Run", BindingFlags.Static | BindingFlags.Public)
               ?? throw new ScriptCompileException("Compiled script did not expose Run().");
    }

    /// <summary>
    /// Runs a compiled snippet. Must be called on the game thread.
    ///
    /// Blocks on the returned Task. Read-only introspection completes synchronously, so
    /// this is normally free - but a snippet that awaits a real RB coroutine will stall
    /// the pulse for as long as it runs, because the thread it needs is the one it is
    /// blocking. Long-running snippets are out of scope for the bridge by design.
    /// </summary>
    public static object Invoke(MethodInfo run)
    {
        try
        {
            var task = (Task<object>)run.Invoke(null, null);
            return task.GetAwaiter().GetResult();
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Surface the snippet's real failure, not the reflection wrapper.
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// Lets a caller send a bare expression ("Core.Me.Name") instead of a full statement
    /// body, which is what you want the majority of the time when poking at game state.
    ///
    /// Decided by actually parsing rather than by looking for punctuation. The obvious
    /// heuristics all fail on the single most common input: an anonymous-type projection
    /// like <c>new { Core.Me.Name }</c> contains braces but is an expression, and treating
    /// it as a statement body yields a baffling "; expected".
    /// </summary>
    private static string Normalize(string code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "return null;";

        return IsCompleteExpression(trimmed)
            ? $"return (object)({trimmed});"
            : trimmed;
    }

    private static bool IsCompleteExpression(string code)
    {
        try
        {
            var expression = SyntaxFactory.ParseExpression(code);

            // Must parse cleanly AND consume the whole input - otherwise something like
            // "var x = 1; x" parses as the expression "var" with trailing junk.
            return !expression.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)
                   && expression.FullSpan.Length == code.Length;
        }
        catch
        {
            return false;
        }
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            // Report the line the caller actually wrote, not the line in our wrapper.
            var line = d.Location.GetLineSpan().StartLinePosition.Line + 1 - PrefixLines;
            sb.AppendLine($"line {line}: {d.GetMessage()}");
        }

        return sb.Length == 0 ? "Compilation failed with no error diagnostics." : sb.ToString().TrimEnd();
    }
}

internal sealed class ScriptCompileException : Exception
{
    public ScriptCompileException(string message) : base(message) { }
}
