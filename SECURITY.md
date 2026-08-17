# Security

This runs arbitrary C# inside your RebornBuddy process, over an HTTP endpoint on loopback,
and RebornBuddy is usually elevated. That is the entire point of the tool. The README says
so before it says anything else.

## Known and intentional

Not bugs. Not changing.

- **`eval_csharp` runs unrestricted code.** It can cast abilities, move your character, send
  chat, and reflect over loaded routines. There is no denylist and there will not be one.
- **Any process running as you can read the token file.** It carries whatever ACL it
  inherits from the plugin folder. `AuthToken.cs` explains why tightening that costs more
  than it buys.
- **`/health` answers without a token**, reporting the version, the tool count, and whether
  a character is logged in.
- **`RequireAuthToken: false`** turns the token check off completely, if you ask it to.

The one genuinely load-bearing piece is `Http/Guard.cs`, which decides who may reach the port
at all. Read it and decide for yourself whether you want this running.

## As-is

MIT licensed, no warranty of any kind — see `LICENSE`.

No support, no disclosure process, and I don't accept pull requests. Fork it if you want it
to work differently; that's what the licence is for.

Nobody is making you install this.
