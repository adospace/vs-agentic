# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

VsAgentic is a Visual Studio 2026 extension that embeds a Claude chat panel in the IDE. It does **not** call the Anthropic API — it drives the `claude` CLI as a long-lived child process in bidirectional stream-json mode and renders the resulting event stream as chat UI. Authentication is entirely the CLI's (subscription-based); `ANTHROPIC_API_KEY` is deliberately cleared before spawning.

## Build & run

```powershell
dotnet restore src/VsAgentic.slnx
dotnet build src/VsAgentic.slnx -c Release -v minimal   # produces the VSIX under VsAgentic.VSExtension/bin/<config>/
```

- **F5 in Visual Studio** launches the VS Experimental Instance with the VSIX deployed. Deployment hinges on `<DeployExtension>` in `VsAgentic.VSExtension.csproj`, *not* on the `<Deploy Solution="Debug|*" />` entry in `VsAgentic.slnx`: `Microsoft.VisualStudio.Extensibility.Build.targets` defaults `DeployExtension` to `false` and is imported after the VSSDK targets that default it to `true`, so this hybrid VSSDK + VisualStudio.Extensibility project must set it explicitly. It is conditioned on `'$(MSBuildRuntimeType)' != 'Core'` because the VSSDK targets hard-error on `dotnet build`. Don't remove that condition — it would break CI.
- **`VsAgentic.Desktop`** (WPF) and **`VsAgentic.Console`** are standalone apps over `AddVsAgenticServices`, intended for iterating on the service/UI layers without an Experimental Instance. **Both are currently not contolled by `VsAgentic.slnx`**, so nothing builds them. Both fail restore with `NU1107`: `Microsoft.Extensions.Hosting 10.0.5` wants `System.Text.Json >= 10.0.5` but `VsAgentic.Services`/`VsAgentic.UI` strict-pin it to `[10.0.0]` (fix: a direct `System.Text.Json` reference in the app — the pin only matters inside devenv). `Desktop` is additionally stale since v3.0.15, referencing `BannerTheme` and `ChatWebView.ShowPermissionBanner`/`ShowQuestionCard`/`ShowLoginBanner`, none of which exist any more. Don't recommend either as a verification path without spending time shaping them up for this.
- There are **no test projects**. Verification is manual, via the Experimental Instance (F5).
- `ForceRefreshIntermediateVsixManifest` in `VsAgentic.VSExtension.csproj` deletes `obj\<config>\<tfm>\extension.vsixmanifest` when `source.extension.vsixmanifest` is newer. `DetokenizeVsixManifestSource` only generates that intermediate file when it's absent — without the target, every manifest edit (version, `DisplayName`, `Assets`, …) is silently dropped on incremental builds while the build still reports success. Don't remove it.

Everything targets `net472` (VS extension constraint) with `LangVersion latest`; `PolySharp` supplies the missing modern BCL attributes.

### Release

Pushing a `v*.*.*` tag triggers `.github/workflows/publish-vsix.yml`, which rewrites `source.extension.vsixmanifest`'s version from the tag, builds, publishes to the VS Marketplace, and cuts a GitHub release. The repo convention (see `git log`) is to bump the manifest version in the same commit as the change.

### Logs

- `%AppData%\VsAgentic\logs\vsagentic-<date>.log` — main Serilog sink (and the MCP helper's own log).
- `%LocalAppData%\VsAgentic\resolver.log` — assembly-resolve bootstrap only (runs before Serilog exists).
- `%AppData%\VsAgentic\workspaces\<hash>\` — persisted sessions, keyed by SHA-256 of the normalized solution path.

## Architecture

Four layers, each project referencing only the one below it:

```
VsAgentic.VSExtension  →  VsAgentic.UI  →  VsAgentic.Services
(VSIX, tool windows,      (WPF controls,    (CLI process host,
 options, package)         MVVM, WebView2)   brokers, session store)

VsAgentic.Services.McpPermissionServer — separate exe, embedded as a zip resource
```

`AddVsAgenticServices` (`Services/DependencyInjection/ServiceCollectionExtensions.cs`) is the single composition entry point used by all three hosts. In the VS extension, `VsAgenticPackage.CreateChatViewModel` builds a **fresh `ServiceProvider` per chat window** — so each chat tool window owns its own CLI subprocess, brokers, and pipe server.

### The CLI conversation loop

1. `ClaudeCliProcessHost` (singleton per chat window) spawns `claude -p --input-format stream-json --output-format stream-json --verbose --permission-mode <mode> --permission-prompt-tool mcp__vsagentic__approval_prompt --strict-mcp-config --mcp-config <tempfile>`. `-p` (print mode) is mandatory — the stream-json flags and `--permission-prompt-tool` only apply there.
2. All stdin writes funnel through one `Channel<string>` serviced by a single writer task; stdout lines are parsed to `JsonElement` and pushed onto a second channel.
3. `ClaudeCliChatService` runs one dispatcher task over that channel and routes events to the single `TurnState` that's active (turns are serialized by the UI's `IsBusy` gate). `SendMessageAsync` writes one user line and yields text deltas until the matching `result` event.
4. The process is reused across turns; session state lives inside the CLI. On restart, `SetResumeSessionId` + `--resume` restores context.

Argument-order caveat documented in `BuildArguments`: `--resume` must precede `--append-system-prompt`, because a `.cmd`/`.bat` CLI target routes through `cmd.exe`, which can truncate the argument string at embedded newlines.

### Permission & question flow

This is the least obvious part of the system. The CLI can't call back into the extension directly, so:

```
claude CLI  --(stdio MCP)-->  vsagentic-mcp-permissions.exe  --(named pipe)-->  extension process
                                                                                      |
                                              PermissionPipeServer → IPermissionBroker / IUserQuestionBroker
                                                                                      |
                                                              ChatSessionViewModel → banner UI → Allow/Deny
```

- The helper exe's whole `bin` folder is zipped into `VsAgentic.Services` as an embedded resource (`EmbedMcpHelper` target) and extracted at runtime to `%LocalAppData%\VsAgentic\helpers\<content-hash>\`. The hash directory exists so two IDE instances on different extension versions don't fight over locked DLLs.
- Pipe name and shared secret are per-process GUIDs passed to the helper via env vars in the generated `--mcp-config`.
- Wire format is newline-delimited JSON, two shapes only — see the doc comment on `PermissionPipeServer`.
- `AskUserQuestion` rides the same channel: the answers come back as the *allow* decision's `updatedInput`, not as a side-channel `tool_result`. That's the documented Anthropic flow and keeps the model's tool call resolving naturally.
- "Allow for this session" is **extension-side only** (`IPermissionBroker.RememberAllow`) — the CLI always sees a plain `allow`, and nothing is written to user settings. Remembered allows are cleared whenever the subprocess restarts, since a new process is a new session.
- The helper exe hand-rolls its JSON escaping (`JsonEscape` in its `Program.cs`) because `JsonSerializer.Serialize(string)` drags in `DefaultJsonTypeInfoResolver`, which fails to load on net472 inside the CLI's spawn environment.

### Assembly loading — read before touching package versions

Visual Studio ships its own `Microsoft.Extensions.*`, `System.Text.Json`, `Microsoft.Bcl.AsyncInterfaces`, etc. in `Common7\IDE\PublicAssemblies` / `PrivateAssemblies`, and .NET Framework's strong-name binder rejects any patch mismatch. The scheme:

- Every VS-bundled package is **strict-pinned to `[10.0.0]`** (the .NET 10 GA floor) with `ExcludeAssets="runtime"` in `VsAgentic.VSExtension.csproj`, so we compile against the floor and ship nothing.
- `AssemblyResolveBootstrap` (a `[ModuleInitializer]` in the extension) hooks `AppDomain.AssemblyResolve` and probes VS's assembly folders for whatever version is actually on disk, for an explicit allow-list only.
- **Adding a VS-bundled dependency requires two edits:** an explicit pinned `PackageReference` in `VsAgentic.VSExtension.csproj` *and* the simple name in `AssemblyResolveBootstrap.AllowList`. The host-level `PackageReference` is required even if the dependency is only used by `VsAgentic.Services` — transitive packages don't inherit `ExcludeAssets` and get re-deposited into the output folder otherwise.
- `Serilog` is pinned to exactly `4.2.0` (not VS-bundled, so it ships) because `Serilog.Extensions.Logging` 10.0.0 and `Serilog.Sinks.File` 7.0.0 were compiled against that exact assembly version.

### UI rendering

Chat content is **not** WPF-rendered. `ChatWebView` hosts a WebView2 that loads an embedded HTML template with showdown.js inlined (`VsAgentic.UI/Assets/`), and the view model pushes messages/updates into it via events (`MessageAdded`, `MessageContentUpdated`, …). Operations issued before `NavigationCompleted` are queued in `_pendingOps` and replayed. Clicks on file links post back through `WebMessageReceived` → `ChatWebView.FileOpenRequested` → `VsAgenticPackage.OnFileOpenRequested`, which normalizes MSYS-style `/c/foo` paths and `:line` suffixes before opening the document.

Banners (permission, login, question card) are real WPF: view models in `VsAgentic.UI/ViewModels/Banners/`, controls in `VsAgentic.VSExtension/ToolWindows/Banners/`. `IOutputListener` carries tool-step start/update/complete events from the service layer up to the chat item list.

Concurrency notes worth respecting: `OpenOrActivateSessionAsync` serializes window creation behind `_openSessionGate` because concurrent WebView2 initialization deadlocks the UI thread; `ChildProcessTracker` assigns the CLI to a Win32 Job Object so it dies with a crashed IDE.
