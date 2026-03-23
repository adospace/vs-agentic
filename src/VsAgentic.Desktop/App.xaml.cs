using VsAgentic.Services.Abstractions;
using VsAgentic.Services.DependencyInjection;
using VsAgentic.UI;
using VsAgentic.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.IO;
using System.Windows;

namespace VsAgentic.Desktop;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File("logs/vsagentic-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            var workingDir = e.Args.Length > 0 ? e.Args[0] : Environment.CurrentDirectory;
            workingDir = Path.GetFullPath(workingDir);

            if (!Directory.Exists(workingDir))
            {
                MessageBox.Show($"Directory does not exist: {workingDir}", "VsAgentic", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            _host = Host.CreateDefaultBuilder(e.Args)
                .UseSerilog()
                .ConfigureServices((_, services) =>
                {
                    services.AddSingleton<OutputListener>();
                    services.AddSingleton<IOutputListener>(sp => sp.GetRequiredService<OutputListener>());
                    services.AddSingleton<ChatSessionViewModel>();
                    services.AddVsAgenticServices(options =>
                    {
                        options.WorkingDirectory = workingDir;
                    });
                    services.AddTransient<MainWindow>();
                })
                .Build();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start");
            MessageBox.Show($"Failed to start: {ex.Message}", "VsAgentic", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
