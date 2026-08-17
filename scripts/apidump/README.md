# apidump

Dumps the public API surface of RebornBuddy's reference assembly, so you can check what RB
actually exposes instead of guessing and waiting on a compile error.

Uses `MetadataLoadContext`, so it inspects the assembly without loading or running it.

```powershell
dotnet build scripts\apidump\apidump.csproj

# find types
dotnet scripts\apidump\bin\Debug\net8.0\apidump.dll 'WorldManager'

# with members
dotnet scripts\apidump\bin\Debug\net8.0\apidump.dll '^ff14bot\.Managers\.WorldManager$' --members

# include inherited members (many RB result types put Id/Name on a base class)
dotnet scripts\apidump\bin\Debug\net8.0\apidump.dll 'AuraResult$' --members --inherited

# what does RebornBuddy.dll reference?
dotnet scripts\apidump\bin\Debug\net8.0\apidump.dll x --refs
```

The argument is a regex matched against the full type name, case-insensitive.

## Why you want this

RB's naming does not always match intuition, and a wrong guess costs a build cycle:

- `WorldManager.EorzaTime` — spelled exactly like that
- `JsonSettings` lives in `ff14bot.Helpers`, but `[Setting]` is
  `System.Configuration.SettingAttribute`
- `Item` and `AuraResult` inherit their name fields from `LocalizeableResult`; `Item` has
  no `Id` at all (the `ItemCache` key is the ID)
- `LocalizedDictionary<K,V>` has `GetEnumerator()` but does **not** implement
  `IEnumerable`, so `foreach` binds and LINQ does not

It also confirmed the fact the eval tier is built on: Roslyn is ILMerged into
`RebornBuddy.dll` as public types, so `CSharpSyntaxWalker` is available with no NuGet
reference at all.

## Note

The assembly path in `Program.cs` is pinned to reference assemblies **1.0.803**, matching
the plugin's `PackageReference`. Update both together.

Members whose signatures touch assemblies outside the resolver set print as
`? Name <unresolved: ...>` rather than aborting the dump.
