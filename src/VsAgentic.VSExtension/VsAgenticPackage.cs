using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.DependencyInjection;
using VsAgentic.Services.Services;
using VsAgentic.UI;
using VsAgentic.UI.Controls;
using VsAgentic.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Serilog;
using VsAgentic.Services.Configuration;
using VsAgentic.VSExtension.Options;
using VsAgentic.VSExtension.ToolWindows;
using Task = System.Threading.Tasks.Task;

namespace VsAgentic.VSExtension;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideBindingPath]
[ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideOptionPage(typeof(VsAgenticOptionsPage), "VsAgentic", "General", 0, 0, true)]
[ProvideToolWindow(typeof(SessionListToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
[ProvideToolWindow(typeof(ChatSessionToolWindow), Style = VsDockStyle.MDI, MultiInstances = true, Transient = true)]
[Guid("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f")]
public sealed class VsAgenticPackage : AsyncPackage, IVsSolutionEvents
{
    private static VsAgenticPackage? _instance;

    // Exposed so tool windows can bind when VS restores them
    internal static SessionListViewModel? SessionListVM => _instance?._sessionListViewModel;

    private SessionListViewModel? _sessionListViewModel;
    private ISessionStore? _sessionStore;
    private string? _solutionDirectory;
    private readonly Dictionary<string, int> _sessionWindowMap = new();
    private int _nextWindowId;
    private uint _solutionEventsCookie;

    public static bool IsLoaded => _instance is not null;

    /// <summary>Raised on the UI thread after the package has fully initialized.</summary>
    internal static event Action? Initialized;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);
        _instance = this;

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        _solutionDirectory = GetSolutionDirectory()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Initialize session store
        _sessionStore = new JsonSessionStore();

        _sessionListViewModel = new SessionListViewModel();

        // Initialize persistence and load saved sessions
        await InitializeSessionPersistenceAsync();

        _sessionListViewModel.SessionOpenRequested += session =>
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await OpenOrActivateSessionAsync(session);
            });
        };

        _sessionListViewModel.SessionRemoved += session =>
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await CloseSessionWindowAsync(session);
            });
        };

        // Listen for file-open requests from rendered markdown
        ChatWebView.FileOpenRequested += OnFileOpenRequested;

        // Listen for solution open/close/switch events
        if (GetService(typeof(SVsSolution)) is IVsSolution solutionService)
        {
            solutionService.AdviseSolutionEvents(this, out _solutionEventsCookie);
        }

        Initialized?.Invoke();
    }

    private async Task InitializeSessionPersistenceAsync()
    {
        if (_sessionStore is null || _solutionDirectory is null || _sessionListViewModel is null) return;

        try
        {
            await _sessionStore.EnsureWorkspaceAsync(_solutionDirectory);
            _sessionListViewModel.Initialize(_sessionStore, _solutionDirectory);
            await _sessionListViewModel.LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VsAgentic: Failed to initialize session persistence: {ex}");
        }
    }

    private ChatSessionViewModel CreateChatViewModel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var workingDir = _solutionDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var services = new ServiceCollection();

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VsAgentic", "logs", "vsagentic-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(logPath, rollingInterval: Serilog.RollingInterval.Day,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Read persisted settings from Tools → Options → VsAgentic
        var optionsPage = (VsAgenticOptionsPage?)GetDialogPage(typeof(VsAgenticOptionsPage));

        var outputListener = new OutputListener();
        services.AddSingleton(outputListener);
        services.AddSingleton<IOutputListener>(outputListener);
        services.AddVsAgenticServices(options =>
        {
            options.WorkingDirectory = workingDir;

            if (optionsPage is not null)
            {
                options.BackendMode = optionsPage.BackendMode;
                options.ApiKey = optionsPage.ApiKey;
                options.ClaudeCliPath = optionsPage.ClaudeCliPath;
                options.CliPermissionMode = optionsPage.CliPermissionMode;
                options.ModelId = optionsPage.ModelId;
                options.GitBashPath = optionsPage.GitBashPath;
                options.BashTimeoutSeconds = optionsPage.BashTimeoutSeconds;
                options.MaxOutputChars = optionsPage.MaxOutputChars;
                options.MaxReadLines = optionsPage.MaxReadLines;
                options.SystemPrompt = optionsPage.SystemPrompt;
            }
        });

        var provider = services.BuildServiceProvider();
        var chatService = provider.GetRequiredService<IChatService>();
        var optionsAccessor = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VsAgentic.Services.Configuration.VsAgenticOptions>>();

        return new ChatSessionViewModel(chatService, outputListener, optionsAccessor);
    }

    private string? GetSolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (GetService(typeof(SVsSolution)) is IVsSolution solution)
        {
            solution.GetSolutionInfo(out string solutionDir, out _, out _);
            if (!string.IsNullOrEmpty(solutionDir))
                return solutionDir;
        }
        return null;
    }

    public static async Task ShowSessionListWindowAsync()
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();
        var window = await _instance.ShowToolWindowAsync(
            typeof(SessionListToolWindow), 0, true, _instance.DisposalToken);

        // Initialize in case the window was just created
        if (window is SessionListToolWindow slw)
        {
            slw.SessionListControl.BindIfNeeded();
        }
    }

    public static async Task ShowChatSessionWindowAsync()
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

        await ShowSessionListWindowAsync();

        // Only create a new empty session when there are no existing sessions,
        // to avoid accumulating empty sessions on every reload/startup.
        var vm = _instance._sessionListViewModel;
        if (vm is not null && vm.Sessions.Count == 0)
        {
            vm.NewSessionCommand.Execute(null);
        }
    }

    private static async Task OpenOrActivateSessionAsync(SessionInfo session)
    {
        if (_instance is null) return;

        try
        {
            await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_instance._sessionWindowMap.TryGetValue(session.Id, out int windowId))
            {
                var existing = await _instance.ShowToolWindowAsync(
                    typeof(ChatSessionToolWindow), windowId, true, _instance.DisposalToken);

                if (existing?.Frame is IVsWindowFrame existingFrame)
                {
                    existingFrame.Show();
                }
                return;
            }

            windowId = _instance._nextWindowId++;
            _instance._sessionWindowMap[session.Id] = windowId;

            var window = await _instance.ShowToolWindowAsync(
                typeof(ChatSessionToolWindow), windowId, true, _instance.DisposalToken);

            if (window is not null)
            {
                window.Caption = session.Name;

                if (window is ChatSessionToolWindow chatWindow)
                {
                    ChatSessionViewModel viewModel;
                    try
                    {
                        viewModel = _instance.CreateChatViewModel();
                    }
                    catch (Exception ex)
                    {
                        viewModel = new ChatSessionViewModel(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                        System.Diagnostics.Debug.WriteLine($"VsAgentic: Failed to create chat service: {ex}");
                        MessageBox.Show($"VsAgentic service init failed:\n\n{ex.Message}\n\n{ex.InnerException?.Message}", "VsAgentic Debug", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    // Wire up the UI event handlers BEFORE restoring messages,
                    // so the MessagesRestored event is received by the WebView.
                    chatWindow.ChatControl.Initialize(viewModel);

                    // Enable persistence on the view model
                    if (session.PersistedId.HasValue && _instance._sessionStore is not null && _instance._solutionDirectory is not null)
                    {
                        viewModel.EnablePersistence(_instance._sessionStore, _instance._solutionDirectory, session.PersistedId.Value);

                        // Restore messages from a previously saved session
                        if (!session.IsActive)
                        {
                            await viewModel.RestoreFromStoreAsync();
                            session.IsActive = true;
                            if (!string.IsNullOrEmpty(viewModel.SessionTitle) && viewModel.SessionTitle != "New Session")
                            {
                                // Keep the persisted title
                            }
                            else
                            {
                                viewModel.SessionTitle = session.Name;
                            }
                        }
                    }

                    // Link the view model to its session entry so cost updates flow back to the list
                    viewModel.SessionInfo = session;

                    // Sync generated title back to session list and window caption
                    viewModel.PropertyChanged += (_, e) =>
                    {        
                        ThreadHelper.ThrowIfNotOnUIThread();

                        if (e.PropertyName == nameof(ChatSessionViewModel.SessionTitle))
                        {
                            session.Name = viewModel.SessionTitle;
                            if (window.Frame is IVsWindowFrame f)
                            {
                                window.Caption = viewModel.SessionTitle;
                            }
                        }
                    };
                }

                // Clean up when the user closes the window (e.g. via X button)
                if (window is ChatSessionToolWindow closableWindow)
                {
                    closableWindow.Closed += () =>
                    {
                        _instance?._sessionWindowMap.Remove(session.Id);
                        session.IsActive = false;
                    };
                }

                if (window.Frame is IVsWindowFrame frame)
                {
                    frame.Show();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"VsAgentic error: {ex.Message}", "VsAgentic", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task CloseSessionWindowAsync(SessionInfo session)
    {
        if (_instance is null) return;

        await _instance.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (_instance._sessionWindowMap.TryGetValue(session.Id, out int windowId))
        {
            var window = _instance.FindToolWindow(typeof(ChatSessionToolWindow), windowId, false);
            if (window?.Frame is IVsWindowFrame frame)
            {
                frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            }
            _instance._sessionWindowMap.Remove(session.Id);
            session.IsActive = false;
        }
    }

    /// <summary>
    /// Switches the session list to a new workspace directory.
    /// Closes idle chat windows and keeps busy (waiting for AI) ones open.
    /// </summary>
    private async Task SwitchWorkspaceAsync(string newSolutionDirectory)
    {
        if (_sessionStore is null || _sessionListViewModel is null) return;
        if (string.Equals(_solutionDirectory, newSolutionDirectory, StringComparison.OrdinalIgnoreCase)) return;

        await JoinableTaskFactory.SwitchToMainThreadAsync();

        // Close idle chat windows, keep busy ones
        var sessionsToClose = new List<string>();
        foreach (var kvp in _sessionWindowMap)
        {
            var window = FindToolWindow(typeof(ChatSessionToolWindow), kvp.Value, false);
            if (window is ChatSessionToolWindow chatWindow
                && chatWindow.ChatControl.DataContext is ChatSessionViewModel vm
                && vm.IsBusy)
            {
                // Session is busy (waiting for AI response) — keep it open
                continue;
            }

            // Idle session — close the window
            if (window?.Frame is IVsWindowFrame frame)
            {
                frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            }
            sessionsToClose.Add(kvp.Key);
        }

        foreach (var id in sessionsToClose)
        {
            _sessionWindowMap.Remove(id);
        }

        // Switch to the new workspace
        _solutionDirectory = newSolutionDirectory;

        try
        {
            await _sessionStore.EnsureWorkspaceAsync(_solutionDirectory);
            _sessionListViewModel.Initialize(_sessionStore, _solutionDirectory);
            await _sessionListViewModel.LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VsAgentic: Failed to switch workspace: {ex}");
        }
    }

    // --- IVsSolutionEvents implementation ---

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var newDir = GetSolutionDirectory()
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await SwitchWorkspaceAsync(newDir);
        });
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await SwitchWorkspaceAsync(fallback);
        });
        return Microsoft.VisualStudio.VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => Microsoft.VisualStudio.VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => Microsoft.VisualStudio.VSConstants.S_OK;

    private void OnFileOpenRequested(string rawPath)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            // Parse optional :line suffix (e.g. "file.cs:42" or "file.cs:42-51")
            int line = 0;
            var lineMatch = Regex.Match(rawPath, @":(\d+)(?:-\d+)?$");
            var filePath = lineMatch.Success ? rawPath.Substring(0, lineMatch.Index) : rawPath;

            // Normalize forward slashes
            filePath = filePath.Replace('/', '\\');

            // Resolve relative paths against the solution directory
            if (!Path.IsPathRooted(filePath) && _solutionDirectory is not null)
            {
                filePath = Path.GetFullPath(Path.Combine(_solutionDirectory, filePath));
            }

            if (!File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"VsAgentic: File not found: {filePath}");
                return;
            }

            if (lineMatch.Success)
                line = int.Parse(lineMatch.Groups[1].Value);

            try
            {
                VsShellUtilities.OpenDocument(this, filePath, Guid.Empty,
                    out _, out _, out IVsWindowFrame? frame);
                frame?.Show();

                if (line > 0 && frame is not null)
                {
                    // Navigate to the specific line
                    if (VsShellUtilities.GetTextView(frame) is var textView && textView is not null)
                    {
                        textView.SetCaretPos(line - 1, 0);
                        textView.CenterLines(line - 1, 1);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VsAgentic: Failed to open file: {ex.Message}");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing)
        {
            ChatWebView.FileOpenRequested -= OnFileOpenRequested;

            if (_solutionEventsCookie != 0)
            {
                if (GetService(typeof(SVsSolution)) is IVsSolution solutionService)
                {
                    solutionService.UnadviseSolutionEvents(_solutionEventsCookie);
                }
                _solutionEventsCookie = 0;
            }
        }
        base.Dispose(disposing);
    }
}
