<#
.SYNOPSIS
    Builds RbMcp, deploys it into RebornBuddy, and registers the MCP server
    with Claude Code.

.DESCRIPTION
    Three steps, each independently skippable:

      1. dotnet build  - the csproj already copies RbMcp.dll into
                         RebornBuddy\Plugins\RbMcp on a successful build.
      2. Verify        - confirms the DLL landed and reports its timestamp.
      3. Register MCP  - `claude mcp add` for the stdio shim, at user scope so the
                         bridge is reachable from every project (Magitek, or any other,
                         anywhere) without committing anything to those repos.

    Re-running is safe. The MCP registration is removed and re-added so the command
    stays correct if paths or ports change.

.PARAMETER SkipBuild
    Deploy and register using whatever is already built.

.PARAMETER SkipMcp
    Build and deploy only. Use when Claude Code is not installed on this machine.

.PARAMETER Port
    Port the plugin listens on. Must match BridgeSettings (default 8787). Written
    into the MCP registration as an environment variable.

.PARAMETER Scope
    Claude Code MCP scope: user (default), project, or local.

.EXAMPLE
    .\scripts\deploy.ps1
    Build, deploy, register.

.EXAMPLE
    .\scripts\deploy.ps1 -SkipBuild -Port 9000
    Re-register the MCP server on a different port.
#>
param(
    [switch]$SkipBuild,
    [switch]$SkipMcp,
    [int]$Port = 8787,
    [ValidateSet('user', 'project', 'local')]
    [string]$Scope = 'user',
    [string]$RebornBuddyPath,
    [string]$ServerName = 'rb'
)

$ErrorActionPreference = 'Stop'

# Where RebornBuddy lives differs per developer, so nothing here may assume a path.
# Order: explicit flag, environment, a sibling of the repo, then the usual suspects.
function Resolve-RebornBuddyPath {
    param([string]$Explicit, [string]$RepoRoot)

    $candidates = @()
    if ($Explicit)                 { $candidates += $Explicit }
    if ($env:REBORNBUDDY_PATH)     { $candidates += $env:REBORNBUDDY_PATH }

    # Most installs put the repo next to the client, or one level above it.
    $parent = Split-Path -Parent $RepoRoot
    if ($parent) {
        $candidates += (Join-Path $parent 'RebornBuddy')
        $grandparent = Split-Path -Parent $parent
        if ($grandparent) { $candidates += (Join-Path $grandparent 'RebornBuddy') }
    }

    $candidates += 'C:\RebornBuddy\RebornBuddy'
    $candidates += 'C:\RebornBuddy'
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'RebornBuddy') }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'RebornBuddy.exe'))) {
            return (Resolve-Path $c).Path
        }
    }

    throw @"
Could not locate a RebornBuddy install (looked for RebornBuddy.exe in $($candidates.Count) places).
Pass it explicitly:      .\scripts\deploy.ps1 -RebornBuddyPath 'D:\Games\RebornBuddy'
Or set it once:          [Environment]::SetEnvironmentVariable('REBORNBUDDY_PATH','D:\Games\RebornBuddy','User')
"@
}

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Project    = Join-Path $RepoRoot 'src\RbMcp\RbMcp.csproj'
$ShimPath   = Join-Path $RepoRoot 'mcp\rb-mcp.mjs'
$RbRoot     = Resolve-RebornBuddyPath -Explicit $RebornBuddyPath -RepoRoot $RepoRoot
$PluginDir  = Join-Path $RbRoot 'Plugins\RbMcp'
$PluginDll  = Join-Path $PluginDir 'RbMcp.dll'
$PluginCs   = Join-Path $PluginDir 'RbMcpLoader.cs'
$TokenPath  = Join-Path $PluginDir 'RbMcp.token'

function Write-Step   ($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok     ($m) { Write-Host "   $m" -ForegroundColor Green }
function Write-Warn   ($m) { Write-Host "   $m" -ForegroundColor Yellow }
function Write-Detail ($m) { Write-Host "   $m" -ForegroundColor DarkGray }

# --- 1. Build ---------------------------------------------------------------

if ($SkipBuild) {
    Write-Step 'Build (skipped)'
} else {
    Write-Step 'Building RbMcp'
    Write-Detail "RebornBuddy: $RbRoot"

    if (-not (Test-Path $Project)) {
        throw "Project not found at $Project"
    }

    # RebornBuddy holds a lock on the DLL while it is running, so a build that cannot
    # copy is almost always "RB is open", not a real compile failure.
    $rb = Get-Process -Name 'RebornBuddy' -ErrorAction SilentlyContinue
    if ($rb) {
        Write-Warn 'RebornBuddy is running - it holds RbMcp.dll open.'
        Write-Warn 'Close it before building, or the copy step will fail.'
    }

    # Hand the resolved path to MSBuild so the csproj's copy step and this script cannot
    # disagree about where the plugin goes.
    dotnet build $Project --nologo "-p:RbPluginDir=$PluginDir"
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }

    # RebornBuddy compiles RbMcpLoader.cs itself, so an error in it would not show
    # up above - it would show up as the plugin silently missing from RB's list. Build it
    # here against the reference assemblies to catch that as a normal compile error.
    $LoaderCheck = Join-Path $RepoRoot 'loader\RbMcpLoader.csproj'
    if (Test-Path $LoaderCheck) {
        dotnet build $LoaderCheck --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Loader failed to compile. RebornBuddy would silently skip the plugin."
        }
        Write-Ok 'Loader compile check passed.'
    }

    Write-Ok 'Build succeeded.'
}

# --- 2. Verify deployment ---------------------------------------------------

Write-Step 'Verifying deployment'

if (-not (Test-Path $PluginDll)) {
    throw "RbMcp.dll not found at $PluginDll. Did the build's DeployToRebornBuddy target run?"
}

$dll = Get-Item $PluginDll
Write-Ok "$PluginDll"
Write-Detail ("{0:N1} KB, built {1:yyyy-MM-dd HH:mm:ss}" -f ($dll.Length / 1KB), $dll.LastWriteTime)

# Without the loader source, RB never looks at the DLL and the plugin is invisible.
if (-not (Test-Path $PluginCs)) {
    throw "RbMcpLoader.cs not found at $PluginCs. RebornBuddy discovers plugins by compiling .cs files, so the DLL alone will not be loaded."
}

Write-Ok "$PluginCs"

# --- 3. Register the MCP server --------------------------------------------

if ($SkipMcp) {
    Write-Step 'MCP registration (skipped)'
} else {
    Write-Step "Registering MCP server '$ServerName' (scope: $Scope)"

    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if (-not $claude) {
        Write-Warn 'Claude Code CLI not found on PATH; skipping registration.'
        Write-Detail 'Register manually with:'
        Write-Detail "  claude mcp add $ServerName --scope $Scope --env RBMCP_PORT=$Port --env RBMCP_TOKEN_FILE=`"$TokenPath`" -- node `"$ShimPath`""
    }
    elseif (-not (Test-Path $ShimPath)) {
        Write-Warn "MCP shim not found at $ShimPath; skipping registration."
    }
    else {
        $node = Get-Command node -ErrorAction SilentlyContinue
        if (-not $node) {
            Write-Warn 'node not found on PATH. The shim needs Node 18+ to run.'
        }

        # Remove first so re-running picks up a changed path or port instead of erroring
        # on a duplicate name. On a first install there is nothing to remove and the CLI
        # says so on stderr - which PowerShell would otherwise promote to a terminating
        # NativeCommandError and abort the install.
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & claude mcp remove $ServerName --scope $Scope | Out-Null } catch { }
        $ErrorActionPreference = $previousEap

        # The token file will not exist until the plugin has run once. Registering the path
        # anyway is correct - the shim reads it lazily, so first use picks it up.
        & claude mcp add $ServerName --scope $Scope --env "RBMCP_PORT=$Port" --env "RBMCP_TOKEN_FILE=$TokenPath" -- node $ShimPath
        if ($LASTEXITCODE -ne 0) {
            Write-Warn "claude mcp add returned $LASTEXITCODE. Register manually:"
            Write-Detail "  claude mcp add $ServerName --scope $Scope --env RBMCP_PORT=$Port --env RBMCP_TOKEN_FILE=`"$TokenPath`" -- node `"$ShimPath`""
        } else {
            Write-Ok "Registered as '$ServerName' on port $Port."
            Write-Detail "Auth token: $TokenPath"
        }
    }
}

# --- Done -------------------------------------------------------------------

Write-Step 'Next steps'
Write-Detail '1. Start RebornBuddy and enable the RbMcp plugin.'
Write-Detail "2. Confirm it is up:  curl http://127.0.0.1:$Port/health"
Write-Detail "3. In your MCP client, confirm the '$ServerName' server connected."
Write-Host ''
