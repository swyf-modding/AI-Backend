<#
    Builds ScamWYF.AiBackend and installs it into the game's BepInEx\plugins.

    The shared library's build script does the work; this file only says what this mod is and
    which extra references it needs. Keeping it that way means the compiler flags and the
    reference list live in one place, in the library.

        .\build.ps1
        .\build.ps1 -CscDll C:\path\to\roslyn\csc.dll
        .\build.ps1 -GameDir "C:\...\steamapps\common\Scam With Your Friends"
        .\build.ps1 -Test

    -Test runs the library's tests as well: the behaviour tests and the API surface check. It is the
    flag a continuous build wants, since a mod that compiles against a library surface it does not
    actually use can still fail to run.
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

$lib = Join-Path $PSScriptRoot 'vendor\ScamWYF.Modding.Core\build.ps1'
if (-not (Test-Path $lib)) {
    throw "The shared library is missing. Run: git submodule update --init --recursive"
}

# The library finds the game install itself; find it here too so the extra references below can be
# pointed at the same place.
#
# Every Steam library is searched, not just the one Steam itself lives in: the release is installed
# to a library on another drive here. Only the full release counts - a leftover Playtest install is
# ignored - and the two live under different folder names, so the release is matched by name rather
# than by whichever library Steam happened to list first.
$managed = $null
if (-not $GameDir) { $GameDir = $env:SWYG_GAME_DIR }
if (-not $GameDir) {
    $roots = @()
    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "${env:ProgramFiles}\Steam",
                         "${env:LOCALAPPDATA}\Steam")) {
        if (-not $steam) { continue }
        $roots += (Join-Path $steam 'steamapps\common')

        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path $vdf)) { continue }
        try {
            $text = Get-Content $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $roots += (Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common')
            }
        } catch { }
    }
    $roots += @('C:\Program Files (x86)\Steam\steamapps\common', 'D:\SteamLibrary\steamapps\common')
    $roots = $roots | Where-Object { $_ } | Select-Object -Unique

    foreach ($name in @('Scam With Your Friends')) {
        foreach ($root in $roots) {
            $candidate = Join-Path $root $name
            if (Test-Path (Join-Path $candidate 'Scam With Your Friends_Data\Managed\mscorlib.dll')) {
                $GameDir = $candidate
                break
            }
        }
        if ($GameDir) { break }
    }
}
if ($GameDir) { $managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed' }

if (-not $managed) {
    throw "Could not find the game install. Pass -GameDir, or set SWYG_GAME_DIR."
}

# The version comes from this repository's tags, not the submodule's, which is why it is resolved here
# and handed down rather than left to the library to work out for itself.
. (Join-Path $PSScriptRoot 'vendor\ScamWYF.Modding.Core\Version.ps1')

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
    Write-Host "  Model / BaseUrl / ApiKey in BepInEx\config\com.community.scamwyf.aibackend.cfg" -ForegroundColor Green
}