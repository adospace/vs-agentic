using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Agents;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// Tools › Options › VsAgentic › Agents: the split suggestions and the roles
/// (subagent files) the main chat can hand work to.
///
/// The page stores nothing of its own. The suggestion settings stay on the
/// General page, where E65 put them, so no one loses a choice already made;
/// the roles live in the CLI's own <c>agents\*.md</c> files. Nothing is
/// written until OK or Apply, except by the two buttons that say so.
/// </summary>
[ComVisible(true)]
[Guid("6f0d6b8e-3c1a-4d5e-9b2f-7a8c4e1d2b30")]
public sealed class AgentsOptionsPage : UIElementDialogPage
{
    private AgentsOptionsControl? _control;

    protected override UIElement Child => _control ??= new AgentsOptionsControl();

    private static VsAgenticOptionsPage? GeneralPage =>
        VsAgenticPackage.Instance?.GetGeneralOptionsPage();

    protected override void OnActivate(CancelEventArgs e)
    {
        base.OnActivate(e);
        ThreadHelper.ThrowIfNotOnUIThread();
        ((AgentsOptionsControl)Child).Load(GeneralPage, VsAgenticPackage.Instance?.GetOpenSolutionDirectory());
    }

    protected override void OnApply(PageApplyEventArgs e)
    {
        if (e.ApplyBehavior == ApplyKind.Apply && _control is not null)
        {
            if (!_control.Apply(GeneralPage, out var error))
            {
                // Keeps the dialog open on the page, so the message is seen.
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                MessageBox.Show(error, "VsAgentic", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        base.OnApply(e);
    }

    // Nothing of this page goes to the registry: see the class comment.
    public override void SaveSettingsToStorage() { }
    public override void LoadSettingsFromStorage() { }
}

/// <summary>The content of <see cref="AgentsOptionsPage"/>.</summary>
internal sealed class AgentsOptionsControl : ScrollViewer
{
    private const string NotSet = "Not set";
    private const string Inherit = "inherit";

    // What the model list offers, as the file spells it, and as the page shows it.
    private static readonly (string Value, string Label)[] Models =
    {
        ("", NotSet),
        (Inherit, "Same as main chat"),
        ("haiku", "Haiku"),
        ("sonnet", "Sonnet"),
        ("opus", "Opus"),
        ("fable", "Fable"),
    };

    private static readonly string[] Efforts = { "", "low", "medium", "high", "xhigh", "max" };

    private readonly CheckBox _suggest = new CheckBox { Content = "Suggest splitting a task across agents", Margin = new Thickness(0, 4, 0, 2) };
    private readonly ListBox _offProjects = new ListBox { MinHeight = 48, MaxHeight = 110, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Button _removeProject = new Button { Content = "Turn suggestions back on", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _roles = new Grid { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _openProject = new Button { Content = "Open project folder", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };

    private readonly List<RoleRow> _rows = new List<RoleRow>();
    private List<string> _offFolders = new List<string>();
    private string? _projectFolder;

    public AgentsOptionsControl()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        var panel = new StackPanel { Margin = new Thickness(4) };

        panel.Children.Add(Heading("Suggestions"));
        panel.Children.Add(_suggest);
        panel.Children.Add(Note("After a turn whose steps show a clear saving, a line above the message box suggests splitting the task across subagents on cheaper models. At most once per chat. The ⚡ button and /split work either way."));

        panel.Children.Add(Heading("Projects with suggestions turned off"));
        panel.Children.Add(_offProjects);
        _removeProject.Click += (_, _) => RemoveSelectedProject();
        _offProjects.SelectionChanged += (_, _) => _removeProject.IsEnabled = _offProjects.SelectedItem is not null;
        panel.Children.Add(_removeProject);

        panel.Children.Add(Heading("Roles"));
        panel.Children.Add(Note("Subagents the main chat can hand work to. A file in the project's .claude\\agents folder replaces a user file with the same name, and both replace a built-in agent. A running chat keeps the roles it started with: a change applies to chats started after it."));
        panel.Children.Add(_roles);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var create = new Button
        {
            Content = "Create default roles",
            Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "Writes explorer (Haiku), implementer (Sonnet), reviewer (Opus), test-runner (Haiku) and Explore (Haiku, replaces the built-in search agent, which otherwise runs on the main chat's model) to " + Tilde(AgentRoleCatalog.UserAgentsFolder) + ". A role that already has a file there is left alone.",
        };
        create.Click += (_, _) => CreateDefaults();
        var openUser = new Button { Content = "Open user folder", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        openUser.Click += (_, _) => OpenFolder(AgentRoleCatalog.UserAgentsFolder);
        _openProject.Click += (_, _) => { if (AgentRoleCatalog.ProjectAgentsFolder(_projectFolder) is { } f) OpenFolder(f); };
        buttons.Children.Add(create);
        buttons.Children.Add(openUser);
        buttons.Children.Add(_openProject);
        panel.Children.Add(buttons);
        panel.Children.Add(_status);

        Content = panel;
    }

    public void Load(VsAgenticOptionsPage? general, string? projectFolder)
    {
        _projectFolder = projectFolder;
        _status.Text = "";

        _suggest.IsChecked = general?.SuggestAgentSplit ?? true;
        _suggest.IsEnabled = general is not null;
        _offFolders = SplitFolders(general?.SplitSuggestionsOffProjects);
        RefreshOffProjects();

        _openProject.IsEnabled = AgentRoleCatalog.ProjectAgentsFolder(projectFolder) is not null;
        LoadRoles(keepPending: false);
    }

    /// <summary>Writes what changed. False, with a message, when a file could not be written.</summary>
    public bool Apply(VsAgenticOptionsPage? general, out string error)
    {
        error = "";
        if (general is not null)
        {
            general.SuggestAgentSplit = _suggest.IsChecked == true;
            general.SplitSuggestionsOffProjects = string.Join(";", _offFolders);
            general.SaveSettingsToStorage();
        }

        var failed = new List<string>();
        foreach (var row in _rows.Where(r => r.IsChanged))
        {
            if (AgentRoleCatalog.Save(row.Role, row.SelectedModel, row.SelectedEffort, out var e))
                row.MarkSaved();
            else
                failed.Add($"{row.Role.Name}: {e}");
        }
        if (failed.Count == 0) return true;

        error = "Some roles could not be saved:\n\n" + string.Join("\n", failed);
        return false;
    }

    private void LoadRoles(bool keepPending)
    {
        var pending = keepPending
            ? _rows.Where(r => r.IsChanged && r.Role.FilePath is not null)
                   .ToDictionary(r => r.Role.FilePath!, r => (r.SelectedModel, r.SelectedEffort), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<AgentRole> roles;
        try
        {
            roles = AgentRoleCatalog.Load(
                AgentRoleCatalog.UserAgentsFolder,
                AgentRoleCatalog.ProjectAgentsFolder(_projectFolder),
                AgentRoleCatalog.Reported(_projectFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            roles = Array.Empty<AgentRole>();
            _status.Text = "The role files could not be read: " + ex.Message;
        }

        _rows.Clear();
        _roles.Children.Clear();
        _roles.RowDefinitions.Clear();
        _roles.ColumnDefinitions.Clear();
        foreach (var w in new[] { GridLength.Auto, new GridLength(150), new GridLength(90), new GridLength(1, GridUnitType.Star) })
            _roles.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        AddRow(Bold("Role"), Bold("Model"), Bold("Effort"), Bold("Source"));
        AddRow(Text("orchestrator", "The main chat. It plans, hands parts to the roles below and checks their results."),
               Text("Main chat model"), Text(""), Text("model picker under the chat input"));

        foreach (var role in roles)
        {
            var row = new RoleRow(role);
            if (role.FilePath is not null && pending.TryGetValue(role.FilePath, out var p))
                row.Select(p.Item1, p.Item2);
            _rows.Add(row);
            AddRow(Text(role.Name, role.Description), row.ModelControl, row.EffortControl, Text(SourceText(role), role.FilePath));
        }

        if (roles.Count == 0)
            AddRow(Text("No roles yet. Create default roles adds them to your user folder."), null, null, null, span: true);
        else if (AgentRoleCatalog.Reported(_projectFolder) is null)
            AddRow(Note("Built-in agents are listed after a chat has started in this solution."), null, null, null, span: true);
    }

    private void AddRow(UIElement? a, UIElement? b, UIElement? c, UIElement? d, bool span = false)
    {
        var index = _roles.RowDefinitions.Count;
        _roles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var cells = new[] { a, b, c, d };
        for (var col = 0; col < cells.Length; col++)
        {
            if (cells[col] is not FrameworkElement cell) continue;
            cell.Margin = new Thickness(0, 2, 10, 2);
            cell.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(cell, index);
            Grid.SetColumn(cell, col);
            if (span) Grid.SetColumnSpan(cell, 4);
            _roles.Children.Add(cell);
        }
    }

    private string SourceText(AgentRole role)
    {
        var where = role.Source switch
        {
            AgentRoleSource.BuiltIn => "built-in",
            AgentRoleSource.User => Tilde(role.FilePath!),
            _ => ProjectRelative(role.FilePath!),
        };
        if (role.Overrides.Contains(AgentRoleSource.User)) where += " · overrides user";
        else if (role.Overrides.Contains(AgentRoleSource.BuiltIn)) where += " · replaces built-in";
        return where;
    }

    private string ProjectRelative(string path)
    {
        if (string.IsNullOrEmpty(_projectFolder)) return path;
        var root = _projectFolder!.TrimEnd('\\', '/');
        return path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(root) + path.Substring(root.Length)
            : path;
    }

    private static string Tilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        return path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path.Substring(home.Length) : path;
    }

    private void CreateDefaults()
    {
        try
        {
            var written = AgentRoleCatalog.CreateDefaults(AgentRoleCatalog.UserAgentsFolder);
            LoadRoles(keepPending: true);
            _status.Text = written.Count == 0
                ? "Every default role already has a file in " + Tilde(AgentRoleCatalog.UserAgentsFolder) + "."
                : $"Created {string.Join(", ", written)} in {Tilde(AgentRoleCatalog.UserAgentsFolder)}. Start a new chat to use them.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "The default roles could not be created: " + ex.Message;
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = "The folder could not be opened: " + ex.Message;
        }
    }

    private void RefreshOffProjects()
    {
        _offProjects.Items.Clear();
        foreach (var f in _offFolders) _offProjects.Items.Add(f);
        // "None" as a disabled entry, so the empty list still reads as a list.
        if (_offFolders.Count == 0) _offProjects.Items.Add(new ListBoxItem { Content = "None", IsEnabled = false });
        _removeProject.IsEnabled = false;
    }

    private void RemoveSelectedProject()
    {
        if (_offProjects.SelectedItem is not string folder) return;
        _offFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RefreshOffProjects();
    }

    private static List<string> SplitFolders(string? value) =>
        (value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim().TrimEnd('\\', '/'))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static TextBlock Heading(string text) =>
        new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) };

    private static TextBlock Note(string text) =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 2) };

    private static TextBlock Bold(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold };

    private static TextBlock Text(string text, string? tooltip = null) =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip };

    /// <summary>One role: its two dropdowns and what they started at.</summary>
    private sealed class RoleRow
    {
        private string _savedModel;
        private string _savedEffort;

        public RoleRow(AgentRole role)
        {
            Role = role;
            _savedModel = role.Model ?? "";
            _savedEffort = role.Effort ?? "";

            ModelControl = Combo(Models.Select(m => (m.Value, m.Label)), _savedModel);
            EffortControl = Combo(Efforts.Select(e => (e, e.Length == 0 ? NotSet : e)), _savedEffort);

            if (!role.CanEdit)
            {
                ModelControl.IsEnabled = EffortControl.IsEnabled = false;
                ModelControl.ToolTip = "A built-in agent. Create a file with the same name to change it.";
            }
        }

        public AgentRole Role { get; }
        public ComboBox ModelControl { get; }
        public ComboBox EffortControl { get; }

        public string? SelectedModel => Value(ModelControl);
        public string? SelectedEffort => Value(EffortControl);

        public bool IsChanged =>
            Role.CanEdit
            && (!string.Equals(SelectedModel ?? "", _savedModel, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(SelectedEffort ?? "", _savedEffort, StringComparison.OrdinalIgnoreCase));

        public void Select(string? model, string? effort)
        {
            SelectValue(ModelControl, model ?? "");
            SelectValue(EffortControl, effort ?? "");
        }

        public void MarkSaved()
        {
            _savedModel = SelectedModel ?? "";
            _savedEffort = SelectedEffort ?? "";
        }

        private static ComboBox Combo(IEnumerable<(string Value, string Label)> items, string current)
        {
            var combo = new ComboBox { MinWidth = 80 };
            foreach (var (value, label) in items)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            SelectValue(combo, current);
            return combo;
        }

        // A value the list does not offer (a full model id, a newer effort
        // level) is shown as written and kept unless the user picks another.
        private static void SelectValue(ComboBox combo, string value)
        {
            var item = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, value, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new ComboBoxItem { Content = value, Tag = value };
                combo.Items.Add(item);
            }
            combo.SelectedItem = item;
        }

        private static string? Value(ComboBox combo) =>
            combo.SelectedItem is ComboBoxItem { Tag: string v } && v.Length > 0 ? v : null;
    }
}
