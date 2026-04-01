using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.DependencyInjection;
using VsAgentic.Services.Services;
using VsAgentic.UI;
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
[ProvideOptionPage(typeof(VsAgenticOptionsPage), "VsAgentic", "General", 0, 0, true)]
[ProvideToolWindow(typeof(SessionListToolWindow), Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
[ProvideToolWindow(typeof(ChatSessionToolWindow), Style = VsDockStyle.MDI, MultiInstances = true, Transient = true)]
[Guid("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f")]
public sealed class VsAgenticPackage : AsyncPackage
{
    private static VsAgenticPackage? _instance;

    // Exposed so tool windows can bind when VS restores them
    internal static SessionListViewModel? SessionListVM => _instance?._sessionListViewModel;

    private SessionListViewModel? _sessionListViewModel;
    private ISessionStore? _sessionStore;
    private string? _solutionDirectory;
    private readonly Dictionary<string, int> _sessionWindowMap = new();
    private int _nextWindowId;

    public static bool IsLoaded => _instance is not null;

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

                    chatWindow.ChatControl.Initialize(viewModel);

                    // Link the view model to its session entry so cost updates flow back to the list
                    viewModel.SessionInfo = session;

                    // Sync generated title back to session list and window caption
                    viewModel.PropertyChanged += (_, e) =>
                    {
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
        }
    }
}
