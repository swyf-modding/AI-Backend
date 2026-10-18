<#
    Builds ScamWYF.AiBackend and installs it into the game's BepInEx/plugins.

    The shared library's build script does the work; this file only says what this mod is and
    which extra references it needs. Keeping it that way means the compiler flags and the
    reference list live in one place, in the library.

        ./build.ps1
        ./build.ps1 -CscDll /path/to/roslyn/csc.dll
        ./build.ps1 -GameDir "/home/you/.steam/steam/steamapps/common/Scam With Your Friends"
        ./build.ps1 -Test

    -Test runs the library's tests as well: the behaviour tests and the API surface check. It is the
    flag a continuous build wants, since a mod that compiles against a library surface it does not
    actually use can still fail to run.

    Powershell 7 (pwsh) on Linux and macOS; either that or Windows PowerShell 5.1 on Windows.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$CscDll,
    # Stamp this version instead of reading the nearest tag. For a build from a source archive with
    # no .git, and for reproducing a release from a commit that has since moved.
    [string]$Version,
    [switch]$NoCopy,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

$lib = Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/build.ps1'
if (-not (Test-Path $lib)) {
    throw "The shared library is missing. Run: git submodule update --init --recursive"
}

# The library finds the game install itself; find it here too so the extra references below can be
# pointed at the same place. It is the library's own resolver, from the submodule, so the two cannot
# end up disagreeing about where the game is.
#
# -Require Managed, not the default Both: this mod reads only the game's own assemblies, and the
# library it calls will insist on BepInEx/core when its own turn comes.
. (Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/GameDir.ps1')
$GameDir = Resolve-GameDir -Explicit $GameDir -Require Managed
$managed = Join-Path $GameDir 'Scam With Your Friends_Data/Managed'

# The version comes from this repository's tags, not the submodule's, which is why it is resolved here
# and handed down rather than left to the library to work out for itself.
. (Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/Version.ps1')

$buildArgs = @{
    Project = 'ScamWYF.AiBackend'
    Sources = @((Join-Path $PSScriptRoot 'src'))
    OutDir  = (Join-Path $PSScriptRoot 'bin')
    BuildVersion = (Resolve-BuildVersion -Path $PSScriptRoot -Explicit $Version)
    Refs    = @(
        (Join-Path $managed 'UnityEngine.UnityWebRequestModule.dll')
        (Join-Path $managed 'UnityEngine.UIElementsModule.dll')
        (Join-Path $managed 'Unity.InputSystem.dll')
        (Join-Path $managed 'Newtonsoft.Json.dll')
        (Join-Path $managed 'UniTask.dll')
        (Join-Path $managed 'Assembly-CSharp.dll')
    )
}

if ($GameDir)   { $buildArgs.GameDir = $GameDir }
if ($CscDll)    { $buildArgs.CscDll = $CscDll }
if ($NoCopy)    { $buildArgs.NoCopy = $true }
if ($Test)      { $buildArgs.Test = $true }

& $lib @buildArgs

if (-not $NoCopy) {
    Write-Host ""
    Write-Host "Launch the game once so BepInEx writes its config, then set" -ForegroundColor Green
    Write-Host "  Model / BaseUrl / ApiKey in BepInEx/config/com.community.scamwyf.aibackend.cfg" -ForegroundColor Green
}