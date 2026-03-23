# VsAgentic

**An agentic AI coding assistant for Visual Studio 2026** — chat with Claude to explore, understand, and modify your codebase using integrated tools for file search, code navigation, and inline editing.

> ⭐ If VsAgentic saves you time, please consider giving it a star — it helps others discover the extension and motivates continued development!

---

## ✨ Features

### 🤖 Powered by Anthropic Claude
VsAgentic connects directly to the Anthropic API and uses the latest Claude models (Haiku, Sonnet, Opus) to understand your questions and act on your codebase — not just answer them.

### 🧰 8 Integrated Agentic Tools
Claude doesn't just suggest code — it **uses tools** to do the work for you:

| Tool | Description |
|------|-------------|
| `bash` | Execute Git Bash commands — run builds, git operations, scripts, package installs |
| `grop` | Find files by glob pattern (e.g. `**/*.cs`, `src/**/*.json`) |
| `greb` | Search file contents with regex — like `grep` with file filtering and context lines |
| `read` | Read file contents with line numbers, with offset/limit for large files |
| `edit` | Perform exact string replacements in files — surgical, reviewable edits |
| `write` | Create new files or overwrite existing ones |
| `web_fetch` | Fetch a web page and return its content as clean Markdown (HTML converted via ReverseMarkdown) |
| `agent` | Spawn a sub-agent (Haiku) to handle a focused sub-task in parallel |

### 🧠 Smart Model Routing
VsAgentic automatically selects the right Claude model for each message:

| Mode | Behaviour |
|------|-----------|
| **Auto** *(default)* | Classifies task complexity and picks the best model automatically |
| **Simple** | Always uses Claude Haiku — fastest, great for quick lookups |
| **Moderate** | Always uses Claude Sonnet — balanced speed and reasoning |
| **Complex** | Always uses Claude Opus — maximum reasoning for hard problems |

### 💬 Persistent Chat Sessions
- Sessions are saved per-workspace under `%AppData%\VsAgentic\workspaces\`
- Conversation history and messages are fully restored when you reopen VS
- Auto-generated session titles based on your first message
- Manage sessions from the **VsAgentic Sessions** panel (open, rename, delete)

### 🖼️ Rich Markdown Rendering
Responses are rendered with full Markdown support — syntax-highlighted code blocks, tables, lists, and inline formatting — via an embedded WebView2 control.

### 🪟 Multi-Window Support
- Open multiple chat sessions simultaneously as floating or docked tool windows
- Each session maintains its own independent conversation history and AI context

### 🔁 Built-in Resilience
Automatic retry with exponential back-off on Anthropic API rate limits (429) and transient errors (502, 503, 504, 529).

---

## 📋 Requirements

- **Visual Studio 2026** version **17.14 or later** (Community, Professional, or Enterprise)
- **Windows x64** (amd64)
- **[Git for Windows](https://git-scm.com/download/win)** — required for the `bash` tool (expects `C:\Program Files\Git\bin\bash.exe`)
- An **Anthropic API key** — get one at [console.anthropic.com](https://console.anthropic.com)

---

## 🚀 Installation

### Option 1 — Visual Studio Marketplace *(recommended)*
1. Open Visual Studio 2026
2. Go to **Extensions → Manage Extensions**
3. Search for **VsAgentic**
4. Click **Download** and restart Visual Studio

### Option 2 — Manual VSIX install
1. Download the latest `.vsix` file from the [Releases](../../releases) page
2. Double-click the `.vsix` file to launch the VSIX Installer
3. Follow the prompts and restart Visual Studio

---

## ⚙️ Setup

### 1. Set your Anthropic API Key

VsAgentic reads your API key from the `ANTHROPIC_API_KEY` **environment variable**.

**Option A — System environment variable (recommended, persists across reboots):**
```powershell
# Run in an elevated PowerShell prompt
[System.Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-...", "Machine")
```
Then **restart Visual Studio** to pick up the new variable.

**Option B — User environment variable:**
1. Open **Start → Search → "Edit environment variables for your account"**
2. Click **New** under "User variables"
3. Set Variable name: `ANTHROPIC_API_KEY` and Variable value: `sk-ant-...`
4. Click **OK** and restart Visual Studio

### 2. Open VsAgentic

Go to **View → Other Windows → VsAgentic** to open a new chat session.

A **VsAgentic Sessions** panel will also appear (docked next to Solution Explorer by default) where you can manage all your conversations.

---

## 🎮 Usage

1. **Open a solution** — VsAgentic automatically scopes all file operations to your solution's root directory.
2. **Type a message** in the chat input and press **Enter** or click **Send**.
3. **Watch Claude work** — tool calls are shown inline as expandable steps so you can follow every action.
4. **Switch model modes** — use the model selector in the chat toolbar to change routing behaviour mid-conversation.

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
├── VsAgentic.UI/   # Shared WPF controls, ViewModels, Markdown renderer (WebView2)
├── VsAgentic.Services/      # Core AI logic — tools, chat service, model router, session store
├── VsAgentic.Desktop/       # Standalone WPF desktop app (for development & testing)
└── VsAgentic.Console/   # Console host (for development & testing)
```

---

## 🔒 Privacy & Security

- Your code is sent to the **Anthropic API** to fulfill requests. Review [Anthropic's privacy policy](https://www.anthropic.com/privacy) before use on sensitive or proprietary codebases.
- Your API key is stored only as an environment variable — it is **never** written to disk by the extension.
- Session history (messages) is stored **locally** in `%AppData%\VsAgentic\` and never leaves your machine.

---

## 🐛 Known Limitations

- Requires **Git for Windows** to be installed at the default path for the `bash` tool to function.
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
