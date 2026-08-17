using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

// Dumps the public API surface of the RebornBuddy reference assembly.
// Usage: apidump <regex-on-type-fullname> [--members]
internal static class Program
{
    /// <summary>
    /// Finds the RebornBuddy reference assemblies in the local NuGet cache.
    ///
    /// Resolved rather than hardcoded for two reasons: the cache is under the developer's
    /// profile, and the package version moves every time RB ships. Picking the highest
    /// installed version is right - this tool exists to answer "what does the current API
    /// look like", so an older pin would silently give stale answers.
    /// </summary>
    private static string ResolveReferenceAssemblies()
    {
        var explicitPath = Environment.GetEnvironmentVariable("REBORNBUDDY_REFS");
        if (!string.IsNullOrEmpty(explicitPath) && Directory.Exists(explicitPath))
            return explicitPath;

        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        }

        var packageRoot = Path.Combine(root, "rebornbuddy.referenceassemblies");
        if (!Directory.Exists(packageRoot))
        {
            throw new DirectoryNotFoundException(
                $"RebornBuddy.ReferenceAssemblies not found under {root}. " +
                "Build the solution once to restore it, or set REBORNBUDDY_REFS to a folder " +
                "containing RebornBuddy.dll.");
        }

        var best = Directory.GetDirectories(packageRoot)
            .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .Select(x => Path.Combine(x.Dir, "lib", "net8.0-windows"))
            .FirstOrDefault(Directory.Exists);

        if (best == null)
        {
            throw new DirectoryNotFoundException(
                $"No usable version of RebornBuddy.ReferenceAssemblies under {packageRoot}.");
        }

        return best;
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: apidump <type-regex> [--members]");
            return 1;
        }

        var pattern = new System.Text.RegularExpressions.Regex(
            args[0], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var showMembers = args.Contains("--members");

        string pkg;
        try
        {
            pkg = ResolveReferenceAssemblies();
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);

        var paths = new List<string>();
        paths.AddRange(Directory.GetFiles(pkg, "*.dll"));
        paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));

        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver, "System.Private.CoreLib");

        var asm = mlc.LoadFromAssemblyPath(Path.Combine(pkg, "RebornBuddy.dll"));

        if (args.Contains("--refs"))
        {
            foreach (var r in asm.GetReferencedAssemblies().OrderBy(r => r.Name))
                Console.WriteLine($"{r.Name} {r.Version}");
            return 0;
        }

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

        var matches = types
            .Where(t => t.IsPublic && t.FullName != null && pattern.IsMatch(t.FullName))
            .OrderBy(t => t.FullName)
            .ToArray();

        Console.WriteLine($"# {matches.Length} type(s) matching /{args[0]}/");

        foreach (var t in matches)
        {
            Console.WriteLine();
            Console.WriteLine($"== {t.FullName}{(t.IsEnum ? " (enum)" : t.IsInterface ? " (interface)" : t.IsAbstract && t.IsSealed ? " (static)" : "")}");

            if (!showMembers) continue;

            if (t.IsEnum)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    Console.WriteLine($"   {f.Name} = {f.GetRawConstantValue()}");
                continue;
            }

            Console.WriteLine($"   (base: {t.BaseType?.FullName})");

            var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
            if (!args.Contains("--inherited")) flags |= BindingFlags.DeclaredOnly;

            // A member whose signature touches an assembly outside our resolver set throws
            // on type resolution. Skip it rather than losing the whole dump.
            foreach (var p in t.GetProperties(flags).OrderBy(p => p.Name))
                Try(() => $"   P {(p.GetMethod?.IsStatic == true ? "static " : "")}{Short(p.PropertyType)} {p.Name}", p.Name);

            foreach (var f in t.GetFields(flags).Where(f => !f.IsSpecialName).OrderBy(f => f.Name))
                Try(() => $"   F {(f.IsStatic ? "static " : "")}{Short(f.FieldType)} {f.Name}", f.Name);

            foreach (var m in t.GetMethods(flags)
                         .Where(m => !m.IsSpecialName)
                         .OrderBy(m => m.Name))
            {
                Try(() =>
                {
                    var ps = string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
                    return $"   M {(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({ps})";
                }, m.Name);
            }
        }

        return 0;
    }

    private static void Try(Func<string> render, string memberName)
    {
        try { Console.WriteLine(render()); }
        catch (Exception ex) { Console.WriteLine($"   ? {memberName}  <unresolved: {ex.GetType().Name}>"); }
    }

    private static string Short(Type t)
    {
        if (t == null) return "?";
        if (!t.IsGenericType) return t.Name;
        var name = t.Name.Substring(0, t.Name.IndexOf('`'));
        return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Short))}>";
    }
}
