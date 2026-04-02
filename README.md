# VsAgentic

**An agentic AI coding assistant for Visual Studio 2026** — chat with Claude to explore, understand, and modify your codebase, powered by the Claude Code CLI and your Claude subscription.

> ⭐ If VsAgentic saves you time, please consider giving it a star — it helps others discover the extension and motivates continued development!

![VsAgentic](https://github.com/user-attachments/assets/8d088aa2-cdf8-421b-9834-7f8851bdacdd)

<img alt="VsAgentic - AI coding assistant for Visual Studio 2026" src="https://github.com/user-attachments/assets/cde8d1a6-c1bd-40f1-99a3-ed11b9f5c839" />

---

## ✨ Features

### 🤖 Powered by Claude Code CLI
VsAgentic uses the [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) as its backend, leveraging your Claude subscription (Pro/Max) to understand your questions and act on your codebase — not just answer them. The CLI handles model selection, tool execution, and all agentic capabilities natively.

### ⚙️ Configurable Settings
Settings are available in the Visual Studio **Tools → Options → VsAgentic** dialog:

- **Claude CLI Path** — path to the `claude` executable (defaults to `claude` on PATH)
- **CLI Permission Mode** — controls how the CLI handles tool permissions

### 🧰 Built-in Agentic Tools
Claude Code comes with a full suite of agentic tools — file search, code search, file reading/editing, bash commands, web fetching, and sub-agent delegation. The CLI manages all tools natively; VsAgentic displays tool steps inline so you can follow every action.

### 💬 Persistent Chat Sessions
- Sessions are saved per-workspace under `%AppData%\VsAgentic\workspaces\`
- Conversation history is fully restored when you reopen VS
- Auto-generated session titles based on your first message
- Manage sessions from the **VsAgentic Sessions** panel (open, rename, delete)

### 🖼️ Rich Markdown Rendering
Responses are rendered with full Markdown support — syntax-highlighted code blocks, tables, lists, and inline formatting — via an embedded WebView2 control.

### 🪟 Multi-Window Support
- Open multiple chat sessions simultaneously as floating or docked tool windows
- Each session maintains its own independent conversation history and AI context

---

## 📋 Requirements

- **Visual Studio 2026** version **17.14 or later** (Community, Professional, or Enterprise)
- **Windows x64** (amd64)
- **[Node.js](https://nodejs.org/)** (v18 or later) — required to install the Claude Code CLI
- **An active [Claude Pro or Max subscription](https://claude.ai/pricing)** — VsAgentic uses the Claude Code CLI which requires a paid Claude subscription. API keys are **not** supported.

---

## 🚀 Installation

### Option 1 — Visual Studio Marketplace *(recommended)*
1. Visit the [VsAgentic page](https://marketplace.visualstudio.com/items?itemName=adospace.VsAgentic) on the Visual Studio Marketplace
2. Click **Install** and follow the prompts
3. Or, open Visual Studio 2026, go to **Extensions → Manage Extensions**, search for **VsAgentic**, click **Download** and restart Visual Studio

### Option 2 — Manual VSIX install
1. Download the latest `.vsix` file from the [Releases](../../releases) page
2. Double-click the `.vsix` file to launch the VSIX Installer
3. Follow the prompts and restart Visual Studio

---

## ⚙️ Setup

### 1. Install the Claude Code CLI

Follow the official [Claude Code installation guide](https://docs.anthropic.com/en/docs/claude-code/getting-started) or run:

```
npm install -g @anthropic-ai/claude-code
```

### 2. Log in with your Claude subscription

Open a terminal (PowerShell or Command Prompt) and run:

```
claude login
```

This will open your browser to authenticate with your Claude account. You need an active **Pro** or **Max** subscription.

> For detailed instructions, see the official [Claude Code authentication docs](https://docs.anthropic.com/en/docs/claude-code/getting-started#authentication).

### 3. Verify the CLI works

```
claude -p "hello"
```

If you see a response from Claude, you're all set.

### 4. Configure VsAgentic (optional)

- If `claude` is not on your PATH, go to **Tools → Options → VsAgentic → General** and set **Claude CLI Path** to the full path (e.g. `C:\Users\<you>\AppData\Roaming\npm\claude.cmd`)

### Open VsAgentic

Go to **View → Other Windows → VsAgentic** to open a new chat session.

A **VsAgentic Sessions** panel will also appear (docked next to Solution Explorer by default) where you can manage all your conversations.

### Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| **"Not logged in · Please run /login"** | The CLI is not authenticated | Run `claude login` from a terminal and complete the browser-based login flow |
| **"Invalid API key"** | An `ANTHROPIC_API_KEY` environment variable is set and interfering | Remove the `ANTHROPIC_API_KEY` env var — VsAgentic uses subscription auth, not API keys. Remove it with: `[System.Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", $null, "User")` then restart Visual Studio |
| **"Failed to start Claude CLI"** | The `claude` command was not found | Install the CLI with `npm install -g @anthropic-ai/claude-code`, or set the full path in **Tools → Options → VsAgentic** |

---

## 🎮 Usage

1. **Open a solution** — VsAgentic automatically scopes all file operations to your solution's root directory.
2. **Type a message** in the chat input and press **Enter** or click **Send**.
3. **Watch Claude work** — tool calls are shown inline as expandable steps so you can follow every action.

### Example prompts

```
Explain how the authentication flow works in this codebase.
```
```
Find all places where we catch exceptions without logging them.
```
```
Refactor the UserService to use dependency injection instead of static methods.
```
```
Add XML documentation comments to all public methods in IOrderRepository.cs
```
```
Run the unit tests and fix any failures you find.
```

---

## 🗂️ Project Structure

```
VsAgentic.sln
├── VsAgentic.VSExtension/   # VSIX entry point — commands, tool windows, package bootstrap
├── VsAgentic.UI/            # Shared WPF controls, ViewModels, Markdown renderer (WebView2)
├── VsAgentic.Services/      # Core service layer — CLI integration, session store
├── VsAgentic.Desktop/       # Standalone WPF desktop app (for development & testing)
└── VsAgentic.Console/       # Console host (for development & testing)
```

---

## 🔒 Privacy & Security

- Your code is sent to **Anthropic** via the Claude Code CLI to fulfill requests. Review [Anthropic's privacy policy](https://www.anthropic.com/privacy) before use on sensitive or proprietary codebases.
- **No API keys are stored or required.** Authentication is handled entirely by the Claude Code CLI via your Claude subscription.
- Session history (messages) is stored **locally** in `%AppData%\VsAgentic\` and never leaves your machine.

---

## 🐛 Known Limitations

- Currently supports **Visual Studio 2026 (17.14+)** only — VS Code and Rider are not yet supported.
- The extension targets **x64 Windows** only.

---

## 💬 Feedback & Support

Your feedback makes VsAgentic better! Here's how to get involved:

- 🐛 **Found a bug?** [Open an issue](../../issues/new?template=bug_report.md)
- 💡 **Have a feature idea?** [Start a discussion](../../discussions/new?category=ideas)
- ⭐ **Enjoying the extension?** A star on GitHub goes a long way — thank you!
- 🗳️ **Marketplace review** — Leaving a review on the Visual Studio Marketplace helps other developers discover VsAgentic.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

---

<p align="center">
  Built with ❤️ for Visual Studio developers &nbsp;|&nbsp; Powered by <a href="https://www.anthropic.com">Anthropic Claude</a>
</p>
