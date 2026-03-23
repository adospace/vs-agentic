using VsAgentic.Console;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .CreateLogger();

try
{
    var workingDir = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
    workingDir = Path.GetFullPath(workingDir);

    if (!Directory.Exists(workingDir))
    {
        Console.Error.WriteLine($"Directory does not exist: {workingDir}");
        return 1;
    }

    var builder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<IOutputListener, ConsoleOutputListener>();
            services.AddVsAgenticServices(options =>
            {
                options.WorkingDirectory = workingDir;
            });
        });

    using var host = builder.Build();

    var chatService = host.Services.GetRequiredService<IChatService>();

    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║  VsAgentic - AI Coding Assistant     ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine($"  Working directory: {workingDir}");
    Console.WriteLine("  Commands: 'exit' to quit, 'clear' to reset conversation");
    Console.WriteLine();

    while (true)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("You> ");
        Console.ResetColor();

        var input = Console.ReadLine();
        if (input is null or "exit" or "quit")
            break;

        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            chatService.ClearHistory();
            Console.WriteLine("[Conversation cleared]");
            Console.WriteLine();
            continue;
        }

        try
        {
            await foreach (var _ in chatService.SendMessageAsync(input))
            {
                // Output is handled by the IOutputListener
            }

            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            Log.Error(ex, "Error processing message");
            Console.WriteLine();
        }
    }

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
