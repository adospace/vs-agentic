using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VsAgentic.Services.Agents;

/// <summary>Where a role is defined.</summary>
public enum AgentRoleSource
{
    /// <summary>Comes with the CLI; reported by <c>initialize</c>, no file.</summary>
    BuiltIn,

    /// <summary><c>~/.claude/agents\*.md</c>: every project.</summary>
    User,

    /// <summary><c>&lt;project&gt;\.claude\agents\*.md</c>: this project only.</summary>
    Project,
}

/// <summary>
/// A subagent the main session can start: what the Options page lists as a
/// role. <see cref="Model"/> and <see cref="Effort"/> are the values as the
/// file spells them (<c>haiku</c>, <c>inherit</c>, <c>high</c>), or null when
/// the file leaves them to the CLI.
/// </summary>
public sealed class AgentRole
{
    public AgentRole(string name, AgentRoleSource source, string? filePath, string? description, string? model, string? effort)
    {
        Name = name;
        Source = source;
        FilePath = filePath;
        Description = description;
        Model = model;
        Effort = effort;
    }

    public string Name { get; }
    public AgentRoleSource Source { get; }

    /// <summary>Null for a built-in role.</summary>
    public string? FilePath { get; }

    public string? Description { get; }
    public string? Model { get; }
    public string? Effort { get; }

    /// <summary>The definitions this one hides, in the order the CLI would fall back to them.</summary>
    public IReadOnlyList<AgentRoleSource> Overrides { get; internal set; } = Array.Empty<AgentRoleSource>();

    public bool CanEdit => FilePath is not null;
}

/// <summary>
/// A built-in subagent as the CLI reports it in <c>initialize.agents[]</c>.
/// The answer gives only name, description and model; it does not say which
/// entries come from a file, so the files are read separately.
/// </summary>
public sealed record ReportedAgent(string Name, string? Description, string? Model);

/// <summary>
/// The roles a session in a project can use: the user's and the project's
/// subagent files, plus the CLI's built-in agents.
///
/// The CLI looks a name up project first, then user, then built-in (a
/// project <c>Explore.md</c> replaced the built-in Explore's prompt and model,
/// measured on 2.1.319). It reads the files when it starts: a running session
/// does not see a file written later.
/// </summary>
public static class AgentRoleCatalog
{
    private static readonly object s_lock = new object();
    private static readonly Dictionary<string, IReadOnlyList<ReportedAgent>> s_reported =
        new Dictionary<string, IReadOnlyList<ReportedAgent>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The agents the last CLI started in <paramref name="workingDirectory"/>
    /// reported. Null until one has answered <c>initialize</c>. Kept per
    /// folder because the list includes that project's files.
    /// </summary>
    public static IReadOnlyList<ReportedAgent>? Reported(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        lock (s_lock)
            return s_reported.TryGetValue(Key(workingDirectory!), out var agents) ? agents : null;
    }

    public static void SetReported(string workingDirectory, IReadOnlyList<ReportedAgent> agents)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return;
        lock (s_lock)
            s_reported[Key(workingDirectory)] = agents;
    }

    private static string Key(string folder)
    {
        try { return Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return folder;
        }
    }

    /// <summary><c>~/.claude/agents</c>.</summary>
    public static string UserAgentsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "agents");

    /// <summary>
    /// <c>&lt;project&gt;\.claude\agents</c>, or null when the project folder
    /// is the user profile itself (no solution open), where it would be the
    /// user folder again.
    /// </summary>
    public static string? ProjectAgentsFolder(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return null;
        var folder = Path.Combine(projectFolder!.Trim(), ".claude", "agents");
        return SamePath(folder, UserAgentsFolder) ? null : folder;
    }

    /// <summary>
    /// The roles, in the order the page shows them: files first (project,
    /// then user), then the built-in agents no file replaces. A name defined
    /// in more than one place is listed once, as the definition the CLI uses.
    /// </summary>
    public static IReadOnlyList<AgentRole> Load(string userFolder, string? projectFolder, IReadOnlyList<ReportedAgent>? builtIn)
    {
        var project = ReadFolder(projectFolder, AgentRoleSource.Project);
        var user = ReadFolder(userFolder, AgentRoleSource.User);
        var reported = (builtIn ?? Array.Empty<ReportedAgent>())
            .Where(a => !IsMainAgent(a.Name))
            .ToList();

        var byName = new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase);
        var hidden = new Dictionary<string, List<AgentRoleSource>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Add(AgentRole role)
        {
            if (byName.ContainsKey(role.Name))
            {
                hidden[role.Name].Add(role.Source);
                return;
            }
            byName[role.Name] = role;
            hidden[role.Name] = new List<AgentRoleSource>();
            order.Add(role.Name);
        }

        foreach (var r in project) Add(r);
        foreach (var r in user) Add(r);
        // The CLI reports the effective model of a name a file replaces, not
        // the built-in one, so a reported entry with a file says nothing new.
        foreach (var a in reported)
            Add(new AgentRole(a.Name, AgentRoleSource.BuiltIn, null, a.Description, a.Model, null));

        // A file that replaces a built-in agent the CLI did not list this time
        // (no chat started yet) is still worth marking.
        foreach (var name in order)
        {
            if (byName[name].Source != AgentRoleSource.BuiltIn
                && KnownBuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !hidden[name].Contains(AgentRoleSource.BuiltIn))
                hidden[name].Add(AgentRoleSource.BuiltIn);
        }

        return order.Select(n =>
        {
            var role = byName[n];
            role.Overrides = hidden[n];
            return role;
        }).ToList();
    }

    /// <summary>
    /// The built-in agents of CLI 2.1.319, for marking a file that replaces
    /// one before any chat has reported the list.
    /// </summary>
    public static IReadOnlyList<string> KnownBuiltIn { get; } = new[] { "Explore", "general-purpose", "Plan", "statusline-setup" };

    // "claude" is the main conversation itself, listed so @-mentions can
    // name it; it is not a role a task can be handed to.
    private static bool IsMainAgent(string name) => string.Equals(name, "claude", StringComparison.OrdinalIgnoreCase);

    private static List<AgentRole> ReadFolder(string? folder, AgentRoleSource source)
    {
        var roles = new List<AgentRole>();
        if (folder is null || !Directory.Exists(folder)) return roles;

        foreach (var path in Directory.EnumerateFiles(folder, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var fields = AgentRoleFile.ReadFields(text);
            // The CLI names an agent by its frontmatter, not its file name.
            if (!fields.TryGetValue("name", out var name) || name.Length == 0) continue;
            roles.Add(new AgentRole(name, source, path,
                fields.TryGetValue("description", out var d) ? d : null,
                fields.TryGetValue("model", out var m) && m.Length > 0 ? m : null,
                fields.TryGetValue("effort", out var e) && e.Length > 0 ? e : null));
        }
        return roles;
    }

    /// <summary>
    /// Sets <c>model</c> and <c>effort</c> in a role's file. Null or empty
    /// removes the line. Returns false when the file could not be written.
    /// </summary>
    public static bool Save(AgentRole role, string? model, string? effort, out string? error)
    {
        error = null;
        if (role.FilePath is null) { error = "A built-in role has no file."; return false; }
        try
        {
            var text = File.ReadAllText(role.FilePath);
            var updated = AgentRoleFile.WithField(AgentRoleFile.WithField(text, "model", model), "effort", effort);
            if (updated != text) File.WriteAllText(role.FilePath, updated, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>A role "Create default roles" writes.</summary>
    public sealed record DefaultRole(string Name, string Model, string? Tools, string Description, string Prompt);

    /// <summary>
    /// The default split of work: cheap models where a mistake is cheap to
    /// spot, larger ones for edits and judgement. Matches
    /// <see cref="AgentSplitText.ModelFor"/>.
    ///
    /// Explore is the CLI's own search agent, which otherwise runs on the
    /// main session's model, up to Opus. A file with that name replaces it.
    /// </summary>
    public static IReadOnlyList<DefaultRole> DefaultRoles { get; } = new[]
    {
        new DefaultRole("Explore", "haiku", "Read, Grep, Glob",
            "Fast read-only search agent for finding files and code.",
            "You are a read-only search agent. Find the files, symbols and code the task asks for. Report file paths with line numbers and short excerpts. Do not change any file."),
        new DefaultRole("explorer", "haiku", "Read, Grep, Glob",
            "Finds files, symbols and usages, and reads logs. Read-only.",
            "You find things in the code base. Search, read and report what you found: file paths with line numbers, short excerpts, and what is still unclear. Do not change any file."),
        new DefaultRole("implementer", "sonnet", null,
            "Makes the code changes a task describes and builds them.",
            "You change code. Make the change the task describes, keep to the style of the surrounding code, and build or run the relevant check when you can. Report the files you changed and anything you could not finish."),
        new DefaultRole("reviewer", "opus", "Read, Grep, Glob",
            "Reviews a change for bugs and missed cases. Read-only.",
            "You review changes. Read the changed code and its callers, look for bugs, missed cases and broken contracts, and report each finding with the file, the line and why it is wrong. Do not change any file."),
        new DefaultRole("test-runner", "haiku", "Read, Grep, Glob, Bash",
            "Runs builds and tests and reports the failures.",
            "You run builds and tests. Run what the task asks for, and report the result: what passed, what failed, and the relevant lines of the output. Do not change any file."),
    };

    /// <summary>
    /// Writes the default roles into <paramref name="folder"/>. A role whose
    /// name is already defined there, in any file, is skipped. Returns the
    /// names written.
    /// </summary>
    public static IReadOnlyList<string> CreateDefaults(string folder)
    {
        Directory.CreateDirectory(folder);
        var existing = new HashSet<string>(ReadFolder(folder, AgentRoleSource.User).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();

        foreach (var role in DefaultRoles)
        {
            if (existing.Contains(role.Name)) continue;
            var path = Path.Combine(folder, role.Name + ".md");
            if (File.Exists(path)) continue;
            File.WriteAllText(path, AgentRoleFile.Create(role.Name, role.Description, role.Model, role.Tools, role.Prompt), new UTF8Encoding(false));
            written.Add(role.Name);
        }
        return written;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
