using VsAgentic.Console;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
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

    // Wire permission + question brokers to a simple stdin y/n handler so the
    // console mode is usable without a graphical banner UI.
    var permissionBroker = host.Services.GetRequiredService<IPermissionBroker>();
    permissionBroker.PermissionRequested += request =>
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[Permission] Claude wants to use {request.ToolName}");
        try { Console.WriteLine(request.Input.GetRawText()); } catch { }
        Console.Write("Allow? (y/N): ");
        Console.ResetColor();
        var reply = Console.ReadLine();
        if (reply is not null && reply.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            var inputJson = request.Input.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? "{}"
                : request.Input.GetRawText();
            permissionBroker.Resolve(request.Id, PermissionDecision.Allow(inputJson));
        }
        else
        {
            permissionBroker.Resolve(request.Id, PermissionDecision.Deny("User denied"));
        }
    };

    var questionBroker = host.Services.GetRequiredService<IUserQuestionBroker>();
    questionBroker.QuestionRequested += request =>
    {
        var answers = new Dictionary<string, string>();
        foreach (var q in request.Questions)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[{q.Header}] {q.Question}");
            for (int i = 0; i < q.Options.Count; i++)
                Console.WriteLine($"  {i + 1}. {q.Options[i].Label} - {q.Options[i].Description}");
            Console.Write("Your choice (number or free text): ");
            Console.ResetColor();
            var reply = Console.ReadLine()?.Trim() ?? "";
            if (int.TryParse(reply, out var idx) && idx >= 1 && idx <= q.Options.Count)
                answers[q.Question] = q.Options[idx - 1].Label;
            else
                answers[q.Question] = reply;
        }
        questionBroker.Resolve(request.ToolUseId, answers);
    };

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
