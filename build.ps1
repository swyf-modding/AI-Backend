<#
    Builds ScamWYF.AiBackend and installs it into the game's BepInEx\plugins.

    The shared library's build script does the work; this file only says what this mod is and
    which extra references it needs. Keeping it that way means the compiler flags and the
    reference list live in one place, in the library.

        .\build.ps1
        .\build.ps1 -CscDll C:\path\to\roslyn\csc.dll
        .\build.ps1 -GameDir "C:\...\steamapps\common\Scam With Your Friends"
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$CscDll,
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'

$lib = Join-Path $PSScriptRoot 'vendor\ScamWYF.Modding.Core\build.ps1'
if (-not (Test-Path $lib)) {
    throw "The shared library is missing. Run: git submodule update --init --recursive"
}

# The library finds the game install itself; ask it where that is so the extra references
# below can be pointed at the same place.
$managed = $null
if ($GameDir) {
    $managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
} else {
    foreach ($root in @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common",
        "${env:ProgramFiles}\Steam\steamapps\common",
        "${env:HOME}\.local\share\Steam\steamapps\common",
        "${env:HOME}\.steam\steam\steamapps\common")) {
        foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest')) {
            $candidate = Join-Path $root $name
            if (Test-Path (Join-Path $candidate 'Scam With Your Friends_Data\Managed\mscorlib.dll')) {
                $GameDir = $candidate
                $managed = Join-Path $candidate 'Scam With Your Friends_Data\Managed'
                break
            }
        }
        if ($managed) { break }
    }
}

if (-not $managed) {
    throw "Could not find the game install. Pass -GameDir, or set SWYG_GAME_DIR."
}

$buildArgs = @{
    Project = 'ScamWYF.AiBackend'
    Sources = @((Join-Path $PSScriptRoot 'src'))
    OutDir  = (Join-Path $PSScriptRoot 'bin')
    Refs    = @(
        (Join-Path $managed 'UnityEngine.UnityWebRequestModule.dll')
        (Join-Path $managed 'Newtonsoft.Json.dll')
        (Join-Path $managed 'UniTask.dll')
        (Join-Path $managed 'Assembly-CSharp.dll')
    )
}

if ($GameDir)   { $buildArgs.GameDir = $GameDir }
if ($CscDll)    { $buildArgs.CscDll = $CscDll }
if ($NoCopy)    { $buildArgs.NoCopy = $true }

& $lib @buildArgs

if (-not $NoCopy) {
    Write-Host ""
    Write-Host "Launch the game once so BepInEx writes its config, then set" -ForegroundColor Green
    Write-Host "  Model / BaseUrl / ApiKey in BepInEx\config\com.community.scamwyf.aibackend.cfg" -ForegroundColor Green
}