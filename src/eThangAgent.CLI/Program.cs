using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.Terminal.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Agent.Application;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.CLI;

public static class Program
{
    public static async Task Main()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
            ?? throw new InvalidOperationException(
                "OPENROUTER_API_KEY environment variable not set. " +
                "Get a key at https://openrouter.ai/keys");

        var baseUrlEnv = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
        var baseUrl = string.IsNullOrWhiteSpace(baseUrlEnv)
            ? new Uri("https://openrouter.ai")
            : new Uri(baseUrlEnv);

        var services = new ServiceCollection()
            .AddSingleton(new OpenRouterConfiguration(apiKey, baseUrl))
            .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services
            .AddSingleton(_ => ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!)
            .AddSingleton<Conversation>()
            .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
            .AddSingleton<Ag>(sp =>
            {
                var provider = sp.GetRequiredService<IModelProvider>();
                var conversation = sp.GetRequiredService<Conversation>();
                var config = sp.GetRequiredService<ModelConfig>();
                return new Ag(provider, conversation, config);
            })
            .AddSingleton<SendMessageCommandHandler>()
            .BuildServiceProvider();

        var handler = services.GetRequiredService<SendMessageCommandHandler>();

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            await RunRedirectedRepl(handler);
        else
            await RunInteractiveRepl(handler);
    }

    /// <summary>Line-based REPL for redirected I/O (pipes, E2E tests). Plain Console.ReadLine, no spinner.</summary>
    private static async Task RunRedirectedRepl(SendMessageCommandHandler handler)
    {
        Console.WriteLine("eThang Agent - type /help for commands");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine()?.Trim() ?? string.Empty;
            if (CliCommands.IsQuit(input))
                break;
            if (string.IsNullOrWhiteSpace(input))
                continue;
            if (CliCommands.IsHelp(input))
            {
                Console.WriteLine(CliCommands.Describe());
                Console.WriteLine();
                continue;
            }

            var result = await handler.Handle(new SendMessageCommand(input));
            Console.WriteLine(result.IsSuccess ? result.Value : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            Console.WriteLine();
        }
    }

    /// <summary>Interactive REPL: event-driven line editing with history, ghost autocomplete, and a thinking spinner.</summary>
    private static async Task RunInteractiveRepl(SendMessageCommandHandler handler)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-seekable host */ }

        var io = new SystemConsoleIO();
        var editor = new LineEditor(io, io);
        var spinner = new ConsoleSpinner(io);
        var completer = new PrefixAutoCompleter(CliCommands.All.Select(c => c.Name).ToArray());
        var history = new List<string>();

        Console.WriteLine("eThang Agent - type /help for commands");
        Console.WriteLine();

        while (true)
        {
            var input = editor.Read("> ", history, completer);
            if (input is null)
                break; // Ctrl+D / EOF
            input = input.Trim();
            if (CliCommands.IsQuit(input))
                break;
            if (string.IsNullOrWhiteSpace(input))
                continue;
            if (CliCommands.IsHelp(input))
            {
                Console.WriteLine(CliCommands.Describe());
                Console.WriteLine();
                continue;
            }

            var task = handler.Handle(new SendMessageCommand(input));
            await spinner.RunWhile(task, "Thinking");
            var result = await task;
            Console.WriteLine(result.IsSuccess ? result.Value : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            Console.WriteLine();
        }
    }
}
