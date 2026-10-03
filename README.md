<div align="center">

# ScamWYF.AiBackend

![license](https://img.shields.io/badge/license-MIT-blue)
![version](https://img.shields.io/github/v/release/swyf-modding/AI-Backend?label=version)
![game build](https://img.shields.io/badge/game-v82--playtest-blue)
![Unity](https://img.shields.io/badge/Unity-6000.3.10f1-blue)

*Built with:*

![C#](https://img.shields.io/badge/C%23-512BD4?style=for-the-badge&logo=csharp&logoColor=white)
![Harmony](https://img.shields.io/badge/Harmony-51796aa?style=for-the-badge)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-14172c?style=for-the-badge)
![Newtonsoft](https://img.shields.io/badge/Newtonsoft.Json-000000?style=for-the-badge)

</div>

---

## Table of Contents

- [Overview](#overview)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Installation](#installation)
  - [Developer Setup](#developer-setup)
  - [Configuration](#configuration)
  - [Testing](#testing)
- [Compatibility](#compatibility)
- [How It Hooks In](#how-it-hooks-in)
- [Configuration Reference](#configuration-reference)
  - [Supported Backends](#supported-backends)
  - [Request Rewrites](#request-rewrites)
  - [Reasoning Models](#reasoning-models)
- [Extending](#extending)
- [Project Structure](#project-structure)
- [Continuous Builds](#continuous-builds)
- [Security](#security)
- [License](#license)
- [Related Projects](#related-projects)

---

## Overview

**ScamWYF.AiBackend** is an unofficial, community-built BepInEx mod for **Scam With Your Friends**. It
routes the game's AI calls to your own LLM instead of the hosted backend — Ollama, LM Studio, llama.cpp,
OpenAI, OpenRouter, vLLM, Groq, or anything speaking the OpenAI chat-completions shape.

**Key Features:**

- **One hook covers everything.** Caller dialogue, the objective detector and the review writer all go
  through a single method, and the hosted path — Steam-lobby and backend-auth gates included — is
  skipped entirely
- **An in-game settings tab.** Every option is editable from the shared menu on **F1**, written straight
  to your `.cfg`, with the file's own comments as help text
- **Config hot reload.** Change `BaseUrl` or `Model` in a text editor while the game is running and the
  next request uses it, within about half a second
- **Diagnosable by design.** Live request and failure counts, the last error verbatim, and an explicit
  message for the one failure mode that otherwise looks like a generic parse error
- **Other mods can take over.** A registration API, with falling-back-on-decline, so a routing bug never
  takes the game down
- No redistributed game DLLs, decompiled game source, telemetry, or credential collection

> [!IMPORTANT]
> The game asks for **128 max_tokens** per caller turn. A reasoning model spends all of it thinking and
> returns nothing usable, so the game discards the turn and the caller says *"Sorry, what were you
> saying"*. Set `MinMaxTokens = 2048` if you use one — see [Reasoning Models](#reasoning-models).

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your Friends.

---

## Getting Started

### Prerequisites

- A legally installed copy of **Scam With Your Friends**, with the build listed under
  [Compatibility](#compatibility)
- BepInEx plus the unstripped corlib override. This game ships a stripped `mscorlib` that BepInEx cannot
  start without — [Setup](https://github.com/swyf-modding/Setup) installs both
- [ScamWYF.Modding.Core](https://github.com/swyf-modding/mod-lib), which owns the menu this mod's settings appear in
- An LLM endpoint reachable from this machine

### Installation

1. Install BepInEx and the corlib override with [Setup](https://github.com/swyf-modding/Setup).
2. Copy `ScamWYF.AiBackend.dll` into `BepInEx\plugins`.
3. Copy `ScamWYF.Modding.Core.dll` into `BepInEx\core`, if it is not already there.
4. Launch the game once so BepInEx writes its config, then set `Model` and `BaseUrl` in
   `BepInEx\config\com.community.scamwyf.aibackend.cfg` — or do both from the in-game tab.

### Developer Setup

```powershell
git clone --recurse-submodules https://github.com/swyf-modding/AI-Backend.git
cd AI-Backend
.\build.ps1                # build and install
.\build.ps1 -NoCopy -Test  # build, run the tests, do not touch the game
```

The library is a submodule, built to its own dll in `BepInEx\core` rather than compiled into this one.
That is deliberate: the library owns the singletons — the hotkey table, the menu, the panel — so
embedding it gave every mod a private copy, and F1 opened two windows. `build.ps1` builds the library
first and compiles this mod against the dll it just produced, so the reference is never stale.

Roslyn runs directly; no .NET SDK is needed to build. The game install is auto-detected, or set
`SWYG_GAME_DIR`.

#### Versioning

`build.ps1` reads the nearest git tag and stamps it into the dll. Nothing to edit by hand:

| | |
|---|---|
| `AssemblyVersion` | `1.2.3` — numeric, because the CLR rejects a prerelease here |
| `AssemblyInformationalVersion` | `1.2.3+g0a1b2c3` — what Explorer shows |
| `[BepInPlugin]` version | the same string, which is what the launcher's Mods tab reads |

That last row is why it is generated rather than typed. It used to be a `"1.0.0"` literal in the
source: correct on the first release, silently wrong on every release after it, and shown to players
by the launcher's Mods tab. It is a `const` because `[BepInPlugin]`'s arguments must be compile-time
constants. See [`Version.ps1`](https://github.com/swyf-modding/mod-lib/blob/main/Version.ps1), which
the mod's `build.ps1` and the library share.

A commit past the tag adds `+3.g0a1b2c3`, a prerelease tag keeps its name, and an uncommitted tree is
marked `.dirty` and warned about. `-Version` stamps one explicitly, for a build from a source archive.

### Configuration

Press **F1** and open the **AI Backend** tab. Every setting is there as a form, and editing it writes
straight back to the `.cfg`. The tab also shows what is actually in effect right now — endpoint, mode,
model, provider, request and failure counts, and the last error. That is the thing worth having when a
reply is not what you expected and you cannot tell which of six settings you got wrong.

It is reachable whichever mods are installed: the menu belongs to the shared library, so the mod handler
is not needed for it.

The file is watched as well. Change it in a text editor while the game is running and the next request
uses it. Switching `Mode` to or from `Passthrough` installs or removes the patch at that point, and the
log says which happened.

### Testing

```powershell
.\build.ps1 -NoCopy -Test
```

That compiles the mod against the library, then runs the library's API surface check and its behaviour
tests.

To verify a session headlessly, without the game taking over your screen:

```powershell
& "C:\...\Scam With Your Friends.exe" -batchmode -nographics
```

BepInEx still initialises and writes `BepInEx\LogOutput.log`, so plugin loading and Harmony patching can
be checked without a display. The router, the patch and hot config reload all work headlessly. `-batchmode`
loads no scene, so UI Toolkit has no themed `PanelSettings` to clone and the in-game menu reports itself
unavailable — only the interface needs a real window.

No game DLL belongs in this repository or in a GitHub release.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `v82-playtest` |
| Unity | `6000.3.10f1` |
| BepInEx | `5.4.23.5`, Mono preloader |
| ScamWYF.Modding.Core | built from the pinned submodule commit |
| C# language level | `7.3` |
| Platform | Windows x64 |

The mod compiles with `-nostdlib+` against the game's own `mscorlib.dll`, so a call that managed
stripping removed is a build error rather than a `MissingMethodException` in a session. Keep that flag:
it is why this plugin does not need an unstripped corlib of its own.

---

## How It Hooks In

Everything the game asks an LLM goes through exactly one method:

```text
KolkataApi.CompleteOpenRouterAsync(JObject body, CancellationToken, bool backgroundRequest)
    -> UniTask<JObject>
```

It takes an OpenAI-shaped chat-completion body and returns an OpenAI-shaped response. One Harmony prefix
replaces it, so the whole game is covered by one hook and nothing else has to be touched.

The patch goes through the shared library's `PatchCoordinator`, which means that if another mod is
already patching that method you are told, and if a game update moves it the log says what it expected
and what this build actually has — instead of throwing.

---

## Configuration Reference

`BepInEx\config\com.community.scamwyf.aibackend.cfg`, created on first launch:

```ini
[1 - General]
Mode = OpenAiCompatible        # or OllamaNative, or Passthrough to disable

[2 - Endpoint]
BaseUrl = http://127.0.0.1:11434/v1
ApiKey =

[3 - Model]
Model = llama3.1:8b            # required - the game asks for an OpenRouter model id
```

### Supported Backends

| Server | BaseUrl | Mode |
|---|---|---|
| Ollama | `http://127.0.0.1:11434/v1` | `OpenAiCompatible` |
| Ollama (native) | `http://127.0.0.1:11434` | `OllamaNative` |
| LM Studio | `http://127.0.0.1:1234/v1` | `OpenAiCompatible` |
| llama.cpp | `http://127.0.0.1:8080/v1` | `OpenAiCompatible` |
| OpenAI | `https://api.openai.com/v1` | `OpenAiCompatible` |
| OpenRouter | `https://openrouter.ai/api/v1` | `OpenAiCompatible` + `KeepOpenRouterFields = true` |

### Request Rewrites

- **`model`** — replaced with yours, since the game sends `google/gemini-2.5-flash-lite`.
- **`provider` and `session_id`** — dropped. OpenRouter extensions that OpenAI answers 400 for. Keep them
  with `KeepOpenRouterFields`.
- **`response_format`** — the game sends a strict `json_schema` and *discards the turn* if the reply does
  not parse. `StructuredOutputs` controls how that is expressed: `JsonSchema` (forward it) →
  `JsonObject` (ask for JSON, put the schema in the prompt) → `Prompt` (schema in the prompt only).
  Step down if replies keep failing validation.
- **Thinking tags** — `<think>...</think>` is stripped, including an unclosed block left by a model that
  ran out of tokens.
- **Response shape** — Ollama's native reply is converted back to OpenAI shape.

### Reasoning Models

The game asks for **128 max_tokens** per caller turn. A thinking model spends all of it reasoning and
returns nothing usable — `"content": null`, `"finish_reason": "length"` — so the game throws the turn
away:

```ini
MinMaxTokens = 2048
StripThinkTags = true
```

The log names this case explicitly rather than reporting a generic parse failure:

```text
The model spent its entire token budget thinking and returned no reply (finish_reason "length").
Reasoning models need far more than the token limit the game asks for. Set MinMaxTokens in
BepInEx\config\com.community.scamwyf.aibackend.cfg to 2048 or more.
```

Models that reason in a `reasoning` or `reasoning_content` field rather than `<think>` tags need nothing
extra: the reply is built from `content` only, so reasoning never reaches the game. `MinMaxTokens` is a
floor, not a replacement — it raises the game's 128 but leaves a higher `MaxTokensOverride` alone.

### Validation and the Main Menu

Numeric settings are clamped to a workable range at load and again after every config reload, and the
corrected value is written back to the file — so a hand-edited `Timeout = -1` is a line in the log and a
fix, rather than a mod that stops working every launch.

The main menu asks the hosted backend how many AI credits are left and prints whatever it is told,
whether or not anything is answering. With a custom backend those credits mean nothing, so the mod
rewrites that readout: the **AI status** line reads `AI BACKEND: CUSTOM BACKEND` in the healthy colour,
and the **credits-depleted popup** is replaced with the endpoint, model and provider actually in use.
Everything else the menu warns about — Steam offline, sign-in failed — is still true of the game, so it
is left alone.

---

## Extending

Another mod can provide the backend instead, without patching anything:

```csharp
public sealed class MyBackend : ScamWYF.AiBackend.IChatBackend
{
    public string Name => "my backend";

    public bool TryComplete(JObject request, CancellationToken ct, out UniTask<JObject> response)
    {
        // return false to decline and fall through to the next backend
    }
}

AiBackendApi.Register(new MyBackend());
```

Most recently registered backend is asked first; declining falls through to the built-in router and then
to the game's own backend. A routing bug never takes the game down — it logs and falls back to the stock
path. Registered backends are listed on the menu's **AI Backend** tab, so it is visible when a reply came
from somewhere other than this mod's router.

---

## Project Structure

```text
src/
|-- Plugin.cs          Load, patch installation, menu tab, reload handling.
|                      Also IChatBackend and AiBackendApi, the surface other mods use
|-- Config.cs          Bound settings, ranges, clamping
|-- AiRouter.cs        The HTTP call itself: timeout, retry on 408/429/5xx, backoff
|-- Translator.cs      Request and response rewriting: schema handling, thinking tags, Ollama's native shape
|-- MenuStatus.cs      The main menu's AI status line and popup
build.ps1              Wraps the shared library's build script
vendor/                The shared library, as a submodule
```

| Service | Used for |
|---|---|
| `ScamMod` | identity, load failures logged instead of fatal, unload handled |
| `ModMenu` | the **AI Backend** tab, and the default one if this mod registers nothing |
| `ConfigEditor` | the settings form, generated from the config file itself |
| `WatchConfig` | picking up an edit made in a text editor, without a restart |
| `PatchCoordinator` | the router patch, and the collision report if another mod has taken it |
| `GameBuild` | Unity version check on load, and resolving `MainMenuConnectionStatus` |

The shared menu is drawn with UI Toolkit, the same stack the base game uses for its own menus, so the
panel inherits the game's theme and fonts rather than looking like a debug overlay dropped on top of a
real interface.

---

## Continuous Builds

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request, in two
tiers, because the build compiles against the game's assemblies and those are not ours to redistribute:

| Job | Runner | What |
|---|---|---|
| `library-tests` | hosted, any OS | the library's behaviour tests — pure BCL, no game |
| `build` | self-hosted with the game, or by hand | the real compile, the API check, both dlls as an artefact |

`build` skips itself with a notice when there is no game rather than failing, so a green run never
quietly means "nothing was compiled". To use a labelled runner, set a repository variable:

```text
SWYM_RUNNER = self-hosted, windows, scamwyf
```

---

## Security

Please do not publish suspected vulnerabilities or private game data in a public issue, and do not paste
`BepInEx\LogOutput.log` contents in public: **this mod logs your configured endpoint and model, and the
`ApiKey` is in your `.cfg`.** Share a redacted log if you need to report a problem.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

The shared library this mod builds against is a separate work under its own licence, included here as a
submodule.

---

## Related Projects

| Project | What it is |
|---|---|
| [mod-lib](https://github.com/swyf-modding/mod-lib) | Shared library: base class, menu, config editor, hot reload, patch coordinator |
| [Setup](https://github.com/swyf-modding/Setup) | Installs BepInEx, the corlib override, and the vtable patches this game needs |
| [Launcher](https://github.com/swyf-modding/Launcher) | Installs, launches, and manages mods from outside the game |
| [Mod-Handler](https://github.com/swyf-modding/Mod-Handler) | The in-game **Plugins** tab — turn mods off without leaving a session |
