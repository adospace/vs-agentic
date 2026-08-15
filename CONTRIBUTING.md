# Contributing to VsAgentic

## Prerequisites

| Requirement                             | Notes                                                                                                                                                                                                                                                                                                         |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Visual Studio 2026** (18.x)           | Install the **Visual Studio extension development** workload — it supplies the VSSDK build tools, `CreateExpInstance.exe`, and the Experimental Instance machinery.                                                                                                                                           |
| **.NET Framework 4.7.2 targeting pack** | Every project targets `net472`. Included with the extension development workload.                                                                                                                                                                                                                             |
| **Node.js 18+**                         | Only needed to install the Claude CLI.                                                                                                                                                                                                                                                                        |
| **Claude Code CLI, logged in**          | In the future the API won't be supported, and only subscrptions.<br>`npm install -g @anthropic-ai/claude-code` then `claude login`. The extension shells out to this binary and uses **subscription auth** — an `ANTHROPIC_API_KEY` env var is explicitly cleared before spawning, so API keys will not work. |
| **A Claude Pro or Max subscription**    | Required by the CLI.                                                                                                                                                                                                                                                                                          |

Verify the CLI works standalone before debugging the extension — most "nothing happens" reports trace back to an unauthenticated CLI:

```powershell
claude -p "hello"
```

## Build

```powershell
dotnet restore src/VsAgentic.slnx
dotnet build src/VsAgentic.slnx -c Release -v minimal
```

The VSIX lands in `src/VsAgentic.VSExtension/bin/<config>/`. A `Debug` build is what you want for local iteration.

`VsAgentic.Services` has a build-time dependency on the MCP helper: the `EmbedMcpHelper` target zips `VsAgentic.Services.McpPermissionServer/bin/<config>/net472/` into an embedded resource. If you see

```
MCP helper exe not found at ... Build VsAgentic.Services.McpPermissionServer first.
```

build the solution (not just one project) — the helper is wired as a `ProjectReference` with `ReferenceOutputAssembly=false` purely to force that ordering.

## Running it in VS 2026

**F5 is the normal way to debug on this solution.** It runs the whole solution and runs all the components end to end.

The repo also contains two smaller applications — `VsAgentic.Desktop` and `VsAgentic.Console` — that reuse the lower layers without Visual Studio. They exist to give a faster loop than a full Experimental Instance launch, but **both are not ready for separate running** (see below).

### Debugging in the VS Experimental Instance

With **VsAgentic.VSExtension** as the startup project and press **F5**. VS builds the VSIX, deploys it into a sandboxed instance (`devenv.exe /rootsuffix Exp`), and attaches the debugger.

> Deployment depends on the `DeployExtension` property set in `VsAgentic.VSExtension.csproj` — **not** on the `<Deploy Solution="Debug|*" />` entry in `VsAgentic.slnx`, which only controls the Configuration Manager checkbox. `Microsoft.VisualStudio.Extensibility.Build.targets` defaults `DeployExtension` to `false` and is imported after the VSSDK targets that default it to `true`, so a hybrid VSSDK + VisualStudio.Extensibility extension gets no deployment unless the project overrides it. The property is deliberately scoped to full-framework MSBuild, because the VSSDK targets hard-error on `dotnet build`. If you ever need to confirm what it evaluates to:
> 
> ```powershell
> & "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" `
>     src\VsAgentic.VSExtension\VsAgentic.VSExtension.csproj -getProperty:DeployExtension -nologo
> ```

VS terms the sandboxed version as the "Experimental Instance", and creates a full replica VS install that is isolated from the standard install. When using note:

1. Open any solution — VsAgentic scopes the CLI's working directory to the solution directory, and with no solution open it falls back to your user profile.
2. **View → Other Windows → VsAgentic** opens a chat window. The **VsAgentic Sessions** panel docks next to Solution Explorer.
3. Settings live under **Tools → Options → VsAgentic → General** (CLI path, permission mode, session retention days).

This is the only way to exercise tool windows, the VS options page, solution-switch handling, file-link navigation into the editor, and the assembly-resolve bootstrap — nothing below `VsAgentic.VSExtension` can stand in for it.

**Resetting the Experimental Instance** — do this when the sandbox gets into a bad state (duplicate extensions, a stale build that won't go away):

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VSSDK\VisualStudioIntegration\Tools\Bin\CreateExpInstance.exe" /Reset /VSInstance=18.0 /RootSuffix=Exp
```

Adjust the edition in the path to match your install. If an old build keeps loading, also delete stale VsAgentic folders under the Experimental Instance's `Extensions\` directory — check each folder's `extension.vsixmanifest` to identify the version.

**Where the Experimental Instance lives:** `%LocalAppData%\Microsoft\VisualStudio\18.0_<id>Exp\`, with the extension under `Extensions\<Publisher>\<DisplayName>\<Version>\`. A near-empty `...\VisualStudio\exp\` folder also exists and holds only settings — it is not the instance root, so don't be misled by it.

#### Setting breakpoints not working

In VS 2026 version 18.7.4 - setting breakpoints basically confuses the IDE and most of the time both the launching instance and experimental instance just hang.  

#### Problems with versioning in Experimental Instance

Sometimes it mighe be unclear if the changes have deployed to the experimental instance, so to prove whether the extension deployed at all:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Recurse -Filter "extension.vsixmanifest" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\Designer\\Cache\\' } |
    ForEach-Object { $x=[xml](Get-Content $_.FullName)
        if ($x.PackageManifest.Metadata.Identity.Id -like '*VsAgentic*') {
            "v{0}  {1}" -f $x.PackageManifest.Metadata.Identity.Version, $_.DirectoryName } }
```

No hits means it was never installed — a deployment problem, not a code problem. The `Designer\Cache\` exclusion matters: the XAML designer caches copies of the extension assemblies there, and those paths are **not** deployments.

A correct deployment contains `VsAgentic.VSExtension.pkgdef` *and* `.vsextension\extension.json`. These feed two independent registration systems, which is worth knowing because they fail independently:

| Deployed file                  | Registers                                                                                  | Symptom if only this one works                                                     |
| ------------------------------ | ------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------- |
| `VsAgentic.VSExtension.pkgdef` | The VSSDK package, Tools → Options page, tool window types                                 | Settings appear under Tools → Options, but **no menu entry** to open a chat window |
| `.vsextension\extension.json`  | The VisualStudio.Extensibility command `OpenChatSessionCommand` under View → Other Windows | The menu entry appears but the package never loads                                 |

There is no `.vsct` in this repo — the View → Other Windows entry comes *only* from `extension.json`. So "Options page present, no window" points squarely at the VisualStudio.Extensibility side, not at the package.

**Note:** the absence of `%LocalAppData%\VsAgentic\resolver.log` does *not* prove the extension failed to load. `AssemblyResolveBootstrap` only writes that file when the `AssemblyResolve` fallback actually fires; if the default binder resolves everything, the file is never created. Use the deployment check above, or `%AppData%\VsAgentic\logs\vsagentic-<date>.log`, to tell whether the package initialized.

## The standalone apps

`VsAgentic.Desktop` and `VsAgentic.Console` are not part of the extension, and aren't configured to run as part of this solution. They are small applications that call the same composition root, `AddVsAgenticServices`, so they can drive the CLI without Visual Studio in the picture:

|                         | Layers it exercises                   | What it is for                                                                                                                                                                                                                                                                                             |
| ----------------------- | ------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **`VsAgentic.Console`** | `VsAgentic.Services` only             | The CLI conversation loop with no UI at all — stream-json protocol, process host, the MCP → pipe → broker permission chain. Permission prompts become a `y/N` on stdin and `AskUserQuestion` a numbered list, so you can watch the plumbing directly. Tool steps print inline via `ConsoleOutputListener`. |
| **`VsAgentic.Desktop`** | `VsAgentic.Services` + `VsAgentic.UI` | The same `ChatSessionViewModel` and `ChatWebView` the extension uses, in a plain WPF window — WebView2 rendering, markdown output, banner behaviour.                                                                                                                                                       |

Both take the working directory as `args[0]`. The appeal is the loop time: seconds to restart versus a full Experimental Instance launch, and no Exp settings hive to configure. Neither can tell you anything about tool windows, the options page, solution switching, editor navigation, or the assembly-resolve bootstrap — those only exist above this layer, in `VsAgentic.VSExtension`.

**Neither builds today.** Both are absent from `VsAgentic.slnx`, so CI and every normal build skip them, and they have drifted since v3.0.15 (`git log -- src/VsAgentic.Desktop src/VsAgentic.Console`) while the extension is on 3.4.x.

*Both* fail at restore, before any compilation:

```
error NU1107: Version conflict detected for System.Text.Json.
  VsAgentic.Console -> Microsoft.Extensions.Hosting 10.0.5 -> ... -> System.Text.Json (>= 10.0.5)
  VsAgentic.Console -> VsAgentic.Services -> System.Text.Json (= 10.0.0)
```

*Desktop additionally* references four APIs that no longer exist anywhere in `VsAgentic.UI` — `BannerTheme`, and `ChatWebView.ShowPermissionBanner` / `ShowQuestionCard` / `ShowLoginBanner`, plus the matching view-model events. Banner rendering was reworked after v3.0.15 and Desktop was never updated, so it needs its banner wiring rewritten against the current `ChatWebView`, not just a package fix.



## Install the updates so that a local VS2026 can use the extension before publishing

Using it for a few days locally, before creating a PR to contribute, can be done with the VSIX installer using this script:

```powershell
.\scripts\install-local.ps1            # build Release, replace the installed copy
.\scripts\install-local.ps1 -BumpPatch # same, but bump 3.4.4 -> 3.4.5 first (not needed to pick up a rebuild)
.\scripts\install-local.ps1 -SkipBuild # reinstall the VSIX already in bin\Release
```

The script locates VSIXInstaller via `vswhere`, uninstalls the previous copy by Identity Id, then installs the fresh one. It **refuses to run while any `devenv.exe` is alive** — VSIXInstaller cannot replace files that a running IDE has loaded, and a partial install is worse than no install. Close every VS window, including the Experimental Instance.

You will get an unsigned-extension prompt; that is expected for a local build and the script deliberately does not suppress it.

Settings and history survive a reinstall: **Tools → Options → VsAgentic** lives in the VS settings hive, and chat history in `%AppData%\VsAgentic\workspaces\`.

### Why the uninstall step is not optional

**VSIXInstaller silently refuses a package whose Identity Id *and* Version both match an already-installed extension.** It exits non-zero without touching anything. Since every local build carries the same Identity Id, that means an unbumped rebuild installed on its own does *nothing* — you get your old bits and no obvious error.

Uninstalling first is what makes a same-version reinstall work, and the uninstall is immediate rather than deferred to the next VS start: the installer log shows it deleting the extension's files and then its parent directory, after which the install lands in a fresh randomly-named folder. So **you do not need `-BumpPatch` to pick up a rebuild** — the script's uninstall already handles it. `-BumpPatch` exists for the Marketplace-overwrite problem described below, which is a different concern.

Because a failed uninstall degrades into exactly that silent no-op, the script checks the extension directory on disk before and after each step rather than trusting VSIXInstaller's exit code. It will fail loudly if a previous copy survives the uninstall, or if the expected version is not present afterwards.

### Which version actually gets installed

Two versions are in play and they can disagree:

|                                                   | Where it comes from                                   |
| ------------------------------------------------- | ----------------------------------------------------- |
| `source.extension.vsixmanifest`                   | What you edit, and what `-BumpPatch` increments       |
| `extension.vsixmanifest` inside the built `.vsix` | What VSIXInstaller reads, and what VS ends up running |

They drift whenever the manifest is bumped without a completed build — a `-BumpPatch` run that failed at the build step, or any `-SkipBuild` run against a stale `bin\Release`. The trap is that the manifest says one thing while the artifact on disk contains the previous build, so the install "succeeds" and nothing changes.

The script reads the version out of the built `.vsix` and treats *that* as authoritative, erroring out if it disagrees with the manifest. If you hit that error after a `-BumpPatch` that did not finish, just re-run without `-SkipBuild`.

#### Why a *successful* build used to ship the old version

Worth knowing, because the symptom was a green build that changed nothing. The VSSDK packs `obj\<config>\<tfm>\extension.vsixmanifest`, not `source.extension.vsixmanifest` directly. `DetokenizeVsixManifestSource` (Microsoft.VSSDK.BuildTools 17.14.2120) generates that intermediate file **only when it does not already exist** — and it reports the write in `FileWrites` either way, so even a `-v:diag` build log shows the target running normally.

The effect is that on any incremental build, every edit to `source.extension.vsixmanifest` is silently discarded: version, `DisplayName`, `Description`, `Prerequisites`, `Assets`. The packed `.vsix` keeps whatever the manifest said the last time `obj` was clean. Version is the one that bites, because VSIXInstaller keys on it — you bump, the build succeeds, and the `.vsix` is still stamped with the old version, which then refuses to install over itself.

`VsAgentic.VSExtension.csproj` has a `ForceRefreshIntermediateVsixManifest` target that deletes the intermediate when the source manifest is newer, so the detokenizer regenerates it. Its `Inputs`/`Outputs` make it a no-op otherwise. If you ever see the version mismatch error again, delete `obj\<config>\net472\extension.vsixmanifest` by hand and rebuild — that is the whole fix, and a full `Clean` is not necessary.

### Keep your local version ahead of the Marketplace

This is the part that will silently undo your work if you skip it. A local build and the published extension share an Identity Id, so Visual Studio considers them the same extension. If the Marketplace version ever exceeds the one you installed, **VS's automatic extension update replaces your local build with the published one** — usually without you noticing.

Two defences, worth applying together:

1. **Bump the manifest version past the published one.** `-BumpPatch` does this. Because `UpdateChecker` only raises its InfoBar when Marketplace > running, staying ahead also silences the update nag. A local bump is safe: the publish workflow rewrites the version from the git tag, so it cannot corrupt a release.
2. **Disable automatic extension updates** — Tools → Options → Environment → Extensions.

To check what the Marketplace currently advertises, look at the update checker's own log:

```powershell
Select-String "marketplace latest" "$env:APPDATA\VsAgentic\logs\updatechecker-*.log" | Select-Object -Last 3
```

### It said it installed but nothing changed

Work through these in order:

1. **Confirm what is on disk.** The deployment check in the Experimental Instance section above works just as well for a normal install — it lists every VsAgentic folder with its version. Two hits means an old copy survived an uninstall; zero means the install never landed.
2. **Check the version in that folder.** If it is not the version you just built, you are looking at a stale install, not a broken change — see the two sections above for the two ways that happens.
3. **Read the installer's own log.** VSIXInstaller writes to `%TEMP%\dd_VSIXInstaller_<timestamp>_<id>.log`. Each invocation produces two files: a small one echoing the command line, and a large one with the actual work. Sort by timestamp and open the large one.
4. **Rule out VS having updated over you** — if the installed version matches the Marketplace rather than your build, automatic extension updates replaced it. See the section above.

### Doing it by hand

If you would rather not use the script, it is three commands (VS closed):

```powershell
dotnet build src/VsAgentic.slnx -c Release -v minimal

& "<VS>\Common7\IDE\VSIXInstaller.exe" /q /u:VsAgentic.VSExtension.c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f
& "<VS>\Common7\IDE\VSIXInstaller.exe" src\VsAgentic.VSExtension\bin\Release\net472\VsAgentic.VSExtension.vsix
```

Resolve `<VS>` with `& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`.

**Do not skip the uninstall.** If you did not bump the version, the install is a no-op for the reason above — you will see a non-zero exit code and no change. If you did bump it, skipping the uninstall tends to leave two extension directories behind, which is the root of the "update banner keeps appearing" symptom in the README. Doing it by hand also means neither step is verified, so check the extension folder on disk afterwards.

## Testing

**There are no test projects and no test runner.** Verification is manual. If you add automated tests, note that the `net472` target rules out some modern tooling, and the solution will need the new project added to `VsAgentic.slnx`.

Until then, the checklist below is the de-facto regression suite. Run the sections that your change touches; run all of it before tagging a release.

### Core conversation

- [ ] Send a message and get a streamed response — text should appear incrementally, not all at once.
- [ ] Ask a follow-up that depends on the previous turn, to confirm the subprocess is being reused and context survives.
- [ ] Send a prompt containing **non-ASCII text** (e.g. Cyrillic, CJK, emoji) and confirm it is not corrupted — stdin encoding here has regressed before.
- [ ] Press **Stop** mid-response and confirm the UI returns to idle and remains usable.
- [ ] Trigger a tool-heavy request (e.g. "find all TODO comments") and confirm tool steps render with correct pending → success status transitions.

### Permissions and questions

These paths only work when **CLI Permission Mode** is `Default` in Tools → Options — the other modes bypass the prompt entirely.

- [ ] Ask for something that requires a gated tool (a file edit or a shell command) and confirm the permission banner appears.
- [ ] **Allow** — the tool runs.
- [ ] **Deny** — the model is told and adapts rather than hanging.
- [ ] **"Do this instead"** — the custom instruction reaches the model.
- [ ] **"Allow for this session"** — a second matching request is auto-approved with no banner. Then restart the chat window and confirm the grant is *gone* (it is deliberately per-subprocess and never persisted).
- [ ] Provoke an `AskUserQuestion` (ask something genuinely ambiguous) and confirm the question card renders, accepts an answer, and the model continues with that answer.
- [ ] Press **Stop** while a permission banner is open — pending requests should be denied so the dispatcher unblocks.

### Sessions and windows

- [ ] Create a session, exchange messages, close VS, reopen — history is restored and the auto-generated title persists.
- [ ] Open several chat windows at once; each keeps an independent conversation and its own CLI subprocess.
- [ ] Rename and delete sessions from the Sessions panel.
- [ ] Switch solutions while a chat is open — idle windows close, busy ones stay open, and the session list reloads for the new workspace.
- [ ] Click a file path in a rendered response and confirm it opens at the right line in the editor. Test a `path:42` suffix and an MSYS-style `/c/foo/bar` path.

### Environment failure modes

> **First-run gotcha: set the CLI path in the Experimental Instance.** The Exp instance has its own settings hive, so whatever you configured in your normal VS does **not** carry over. On a fresh Exp instance the path falls back to the default `"claude"`, and you get:
> 
> ```
> Failed to start Claude CLI: The system cannot find the file specified.
> ```
> 
> The bare default cannot work on Windows with an npm-installed CLI. The process is started with `UseShellExecute = false`, so Win32 `CreateProcess` does the lookup — it searches `PATH` but appends only `.exe` to an extensionless name, ignoring `PATHEXT`. npm installs `claude`, `claude.cmd`, and `claude.ps1`; there is no `claude.exe`, so the search fails.
> 
> Set **Tools → Options → VsAgentic → General → Claude CLI Path** to the full `.cmd`, e.g. `%AppData%\npm\claude.cmd` (expand it — the options page stores a literal string). Find yours with `(Get-Command claude.cmd).Source`. Resetting the Exp instance wipes this setting.
> 
> To confirm which binary a run actually used, grep the log:
> 
> ```powershell
> Select-String "Launching:" "$env:APPDATA\VsAgentic\logs\vsagentic-<date>*.log" | Select-Object -Last 3
> ```
> 
> Both instances write to the same log directory, so Serilog rolls the second one to `vsagentic-<date>_001.log` — check both files when the Exp instance is running alongside your normal VS.

- [ ] Point **Claude CLI Path** at something nonexistent — expect a clear "Failed to start Claude CLI" message, not a silent hang.
- [ ] Log the CLI out (`claude logout`) and send a message — expect the login banner, and expect the **Login** button to launch an interactive CLI window.

### After touching package versions or dependencies

This is the highest-risk change in the repo. See the "Assembly loading" section of `src/CLAUDE.md` before you start, then:

- [ ] Confirm the extension loads at all in the Experimental Instance — a binding failure usually manifests as the tool window silently failing to open.
- [ ] Check `%LocalAppData%\VsAgentic\resolver.log` for `no candidate found` entries, which mean a name is missing from `AssemblyResolveBootstrap.AllowList`.
- [ ] Confirm **GitHub Copilot** and other extensions still work in the same instance. A previous binding-redirect approach broke Copilot process-wide; the current resolver is scoped precisely to avoid that, and it is worth re-verifying.
- [ ] Inspect the built VSIX and confirm no VS-bundled assemblies (`Microsoft.Extensions.*`, `System.Text.Json`, …) are being shipped.

## Debugging

### Logs

| Path                                                       | Contents                                                                                                                     |
| ---------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `%AppData%\VsAgentic\logs\vsagentic-<date>.log`            | Main Serilog sink. CLI launch command line, process exit codes, CLI stderr, helper extraction path.                          |
| `%AppData%\VsAgentic\logs\vsagentic-mcp-helper-<date>.log` | The MCP helper's own log — pipe connection, handshake, every MCP request. Start here when a permission prompt never appears. |
| `%LocalAppData%\VsAgentic\resolver.log`                    | Assembly-resolve bootstrap only. Written before Serilog exists.                                                              |
| `%AppData%\VsAgentic\workspaces\<hash>\`                   | Persisted sessions, keyed by SHA-256 of the normalized solution path. Delete a folder to reset one workspace's history.      |

The `[ClaudeCli] Launching:` line in the main log contains the full argument string handed to the CLI — copy it into a terminal to reproduce a spawn problem outside the IDE.

Raw stream-json traffic is **not** logged. Unparseable stdout lines are logged at `Trace`, which the VS host does not emit (its Serilog minimum level is `Debug`, set in `VsAgenticPackage.CreateChatViewModel`). Lower it there, or use the Console host, when you need that detail.

### Attaching to the child processes

The `claude` CLI and `vsagentic-mcp-permissions.exe` are separate processes, so F5 does not attach to them. To debug the helper, use **Debug → Attach to Process** against `vsagentic-mcp-permissions.exe` once a chat window is open, or add a `Debugger.Launch()` at the top of its `Main`. Note that the helper is extracted to `%LocalAppData%\VsAgentic\helpers\<content-hash>\` — the hash changes with every helper rebuild, so old directories accumulate and are safe to delete when no IDE is running.

`ChildProcessTracker` puts the CLI in a Win32 Job Object so it dies with the IDE. If you kill `devenv.exe` from Task Manager, the CLI should go with it; a surviving `claude` process is a bug worth reporting.

## Conventions

- **Versioning** — bump `Version` in `src/VsAgentic.VSExtension/source.extension.vsixmanifest` in the same commit as your change. This is the pattern throughout `git log`, and it makes it possible to tell which build a user is running from the extension folder alone.
- **Releases** — pushing a `v*.*.*` tag runs `.github/workflows/publish-vsix.yml`, which rewrites the manifest version from the tag, builds, publishes to the VS Marketplace, and cuts a GitHub release with a commit-derived changelog. The tag is the source of truth for the published version.
- **Architecture** — `src/CLAUDE.md` documents the layering, the CLI conversation loop, the permission/question flow, and the assembly-loading scheme. Read it before changing anything in `VsAgentic.Services/ClaudeCli/` or touching package versions.
- **Layering** — each project references only the one below it (`VSExtension → UI → Services`). Keep VS SDK types out of `VsAgentic.UI` and `VsAgentic.Services`; the Desktop and Console hosts depend on that separation holding.

## Filing issues and pull requests

- Bugs: <https://github.com/adospace/vs-agentic/issues>
- Ideas and questions: <https://github.com/adospace/vs-agentic/discussions>

For bug reports, the two logs that help most are `vsagentic-<date>.log` and — for anything permission-related — `vsagentic-mcp-helper-<date>.log`. Include your VS version, the extension version from the manifest, and the output of `claude --version`.
