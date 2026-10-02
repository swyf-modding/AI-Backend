# ScamWYF.AiBackend

Sends **Scam With Your Friends**' AI calls to your own LLM — Ollama, LM Studio, llama.cpp, OpenAI,
OpenRouter, vLLM, Groq, anything speaking the OpenAI chat-completions shape — instead of the hosted
backend.

Built against **Unity 6000.3.10f1**, Mono, managed stripping **on**.

Part of a four-repo setup:

| Repo | What it is |
|---|---|
| [scam-wyf-modding-lib](../scam-wyf-modding-lib) | Shared library: base class, menu, config, patch coordinator, hotkeys |
| **scam-wyf-aibackend** (this one) | This mod |
| [scam-wyf-modhandler](../scam-wyf-modhandler) | In-game list of installed plugin files |
| [scam-wyf-setup](../scam-wyf-setup) | BepInEx, Doorstop and the corlib patches the game needs |

Prerequisite: the game has to be loadable at all, which on this build means BepInEx plus the
unstripped corlib override. See [scam-wyf-setup](../scam-wyf-setup).

---

## How it hooks in

Everything the game asks an LLM — caller dialogue, the objective detector, the review writer — goes
through exactly one method:

```
KolkataApi.CompleteOpenRouterAsync(JObject body, CancellationToken, bool backgroundRequest)
    -> UniTask<JObject>
```

It takes an OpenAI-shaped chat-completion body and returns an OpenAI-shaped response. One Harmony
prefix replaces it, so the whole game is covered by one hook and nothing else has to be touched. The
hosted path is skipped entirely, Steam-lobby and backend-auth gates included.

The patch goes through the shared library's `PatchCoordinator`, which means: if another mod is
already patching that method you are told, and if the game updates and the method moves, the log
says what it expected and what this build actually has instead of throwing.

## Build and install

```powershell
.\build.ps1
.\build.ps1 -CscDll C:\path\to\roslyn\csc.dll      # if you have no compiler on PATH
.\build.ps1 -GameDir "C:\...\steamapps\common\Scam With Your Friends"
```

Roslyn runs directly; no .NET SDK needed. The game install is auto-detected, or set `SWYG_GAME_DIR`.
The build puts this mod's dll in `BepInEx\plugins` and the shared library in `BepInEx\core`.

```powershell
git submodule update --init --recursive    # first-time clone only
```

The library is a submodule built to its own dll rather than compiled into this one, because it owns the
singletons — the hotkey table, the menu, the panel. Embedded, every mod would have had a private copy,
and F1 would have opened two windows. Build both from one command; there is still nothing to keep in
step by hand.

## Configure

`BepInEx\config\com.community.scamwyf.aibackend.cfg`, created on first launch.

```ini
[1 - General]
Mode = OpenAiCompatible        # or OllamaNative, or Passthrough to disable

[2 - Endpoint]
BaseUrl = http://127.0.0.1:11434/v1
ApiKey =

[3 - Model]
Model = llama3.1:8b            # required - the game asks for an OpenRouter model id
```

### Or set it in game

Press **F1** and open the **AI Backend** tab. Every setting above is there as a form, with the same
comments as help text, and editing it writes straight back to the `.cfg`. The tab also shows what is
actually in effect right now — endpoint, mode, model, provider, request and failure counts, and the
last error — which is the thing worth having when a reply is not what you expected and you cannot
tell which of six settings you got wrong.

The file is also watched. Change `BaseUrl` or `Model` in a text editor while the game is running and
the next request uses it, within about half a second. Switching `Mode` to or from `Passthrough`
installs or removes the patch at that point, and the log says which happened.

| Server | BaseUrl | Mode |
|---|---|---|
| Ollama | `http://127.0.0.1:11434/v1` | OpenAiCompatible |
| Ollama (native) | `http://127.0.0.1:11434` | OllamaNative |
| LM Studio | `http://127.0.0.1:1234/v1` | OpenAiCompatible |
| llama.cpp | `http://127.0.0.1:8080/v1` | OpenAiCompatible |
| OpenAI | `https://api.openai.com/v1` | OpenAiCompatible |
| OpenRouter | `https://openrouter.ai/api/v1` | OpenAiCompatible + `KeepOpenRouterFields = true` |

## What it rewrites for you

* **`model`** — replaced with yours, since the game sends `google/gemini-2.5-flash-lite`.
* **`provider` and `session_id`** — dropped. OpenRouter extensions that OpenAI answers 400 for.
  Keep them with `KeepOpenRouterFields`.
* **`response_format`** — the game sends a strict `json_schema` and *discards the turn* if the reply
  does not parse. `StructuredOutputs` controls how that is expressed:
  `JsonSchema` (forward it) → `JsonObject` (ask for JSON, put the schema in the prompt) →
  `Prompt` (schema in the prompt only). Step down if replies keep failing validation.
* **thinking tags** — `<think>...</think>` is stripped, including an unclosed block left by a model
  that ran out of tokens.
* **response shape** — Ollama's native reply is converted back to OpenAI shape.

## What the main menu shows

The main menu asks the hosted backend how many AI credits are left and prints whatever it is told,
whether or not anything is answering. With a custom backend those credits mean nothing, so the mod
rewrites that readout:

* the **AI status** line reads `AI BACKEND: CUSTOM BACKEND`, in the healthy colour
* the **credits-depleted popup** is replaced with the endpoint, the model and the provider that are
  actually in use

Everything else the menu warns about — Steam offline, sign-in failed — is still true of the game, so
it is left alone.

Provider is worked out from `BaseUrl` (Ollama, LM Studio, OpenAI, OpenRouter, Groq, llama.cpp, vLLM
and so on are recognised; anything else falls back to the host name). Set it yourself if you like:

```ini
ProviderName = Ollama
```

### If you use a reasoning model

The game asks for **128 max_tokens** per caller turn. A thinking model spends all of it reasoning
and returns nothing usable — `"content": null`, `"finish_reason": "length"` — so the game throws the
turn away and the caller says *"Sorry, what were you saying"*.

```ini
MinMaxTokens = 2048
StripThinkTags = true
```

The log names this case explicitly rather than reporting a generic parse failure:

```
The model spent its entire token budget thinking and returned no reply (finish_reason "length").
Reasoning models need far more than the token limit the game asks for. Set MinMaxTokens in
BepInEx\config\com.community.scamwyf.aibackend.cfg to 2048 or more.
```

Models that reason in a `reasoning` or `reasoning_content` field rather than `<think>` tags need
nothing extra: the mod builds the reply from `content` only, so reasoning never reaches the game.
`MinMaxTokens` is a floor, not a replacement — it raises the game's 128 but leaves a higher
`MaxTokensOverride` alone.

Numeric settings are clamped to a workable range at load and again after every config reload, and the
corrected value is written back to the file — so a hand-edited `Timeout = -1` is a line in the log
and a fix, rather than a mod that stops working every launch.

## Letting another mod take over

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

Most recently registered backend is asked first; declining falls through to the built-in router and
then to the game's own backend. A routing bug never takes the game down — it logs and falls back to
the stock path. Registered backends are listed on the mod menu's **AI Backend** tab, so it is
visible when a reply came from somewhere other than this mod's router.

## The mod menu

This mod has a tab in the menu the shared library owns, so it is reachable whichever mods are
installed — the mod handler is not needed for it.

| Service | Used for |
|---|---|
| `ScamMod` | identity, load failures logged instead of fatal, unload handled |
| `ModMenu` | the **AI Backend** tab, and the default one if this mod registers nothing |
| `ConfigEditor` | the settings form, generated from the config file itself |
| `WatchConfig` | picking up an edit made in a text editor, without a restart |
| `PatchCoordinator` | the router patch, and the collision report if another mod has taken it |
| `GameBuild` | Unity version check on load, and resolving `MainMenuConnectionStatus` |

The shared menu is drawn with UI Toolkit, the same stack the base game uses for its own menus, so
the panel inherits the game's theme and fonts rather than looking like a debug overlay dropped on
top of a real interface. See
[the library README](../scam-wyf-modding-lib#the-ui--the-base-games-own-not-a-lookalike).

## The one build rule that matters

`build.ps1` compiles with `-nostdlib+` against **the game's own `mscorlib.dll`**, not a reference
assembly. The compiler then sees exactly the API surface Unity shipped, so code that uses a
stripped-away method **fails at compile time instead of at runtime**. Keep that flag. It is why
this plugin does not need an unstripped corlib of its own.

C# 7.3, no `.csproj`, no NuGet.

## Testing without the game taking over your screen

```powershell
& ".\Scam With Your Friends.exe" -batchmode -nographics
```

BepInEx still initialises and writes `BepInEx\LogOutput.log`, so plugin loading and Harmony patching
can be verified headlessly. A preloader crash lands in `preloader_*.log` in the game root.

`-batchmode` loads no scene, so UI Toolkit has no themed `PanelSettings` to clone and the in-game
menu reports itself unavailable. The router, the patch and hot config reload all still work
headlessly; only the interface needs a real window.