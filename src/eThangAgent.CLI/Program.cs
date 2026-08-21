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
    private static readonly string[] SpinnerFrames =
        ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

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
        var modelConfig = services.GetRequiredService<ModelConfig>();

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            await RunRedirectedRepl(handler);
        else
            await RunInteractiveRepl(handler, modelConfig);
    }

    /// <summary>Line-based REPL for redirected I/O (pipes, E2E tests).</summary>
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

    /// <summary>
    ///     Full-screen REPL: alternate screen, transcript pane, statusline with spinner,
    ///     and the event-driven line editor on the input row. Resize is picked up on each
    ///     new prompt via a full redraw.
    /// </summary>
    private static async Task RunInteractiveRepl(SendMessageCommandHandler handler, ModelConfig modelConfig)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* non-seekable host */ }

        var terminal = new AnsiTerminal();
        terminal.EnterAlternateScreen();
        try
        {
            var pane = new TranscriptPane();
            pane.AddMessage("Type a message and press Enter. /help lists commands.");
            var completer = new PrefixAutoCompleter(CliCommands.All.Select(c => c.Name).ToArray());
            var history = new List<string>();
            var editor = new LineEditor(terminal, terminal);
            var status = new StatusLine();
            var messages = 0;
            var state = "Ready";

            while (true)
            {
                var width = Console.WindowWidth;
                var height = Console.WindowHeight;
                var layout = TuiLayout.Compute(height);

                terminal.Clear();
                pane.Render(terminal, layout.TranscriptTop, layout.TranscriptHeight, width);
                status.Render(terminal, layout.StatusRow, width, modelConfig.ModelId, messages, state);
                terminal.SetCursorPosition(0, layout.InputRow);

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
                    pane.AddMessage(CliCommands.Describe());
                    continue;
                }

                messages++;
                pane.AddMessage($"\u203a {input}");
                state = "Thinking";

                var task = handler.Handle(new SendMessageCommand(input));
                var frame = 0;
                while (!task.IsCompleted)
                {
                    status.Render(terminal, layout.StatusRow, width, modelConfig.ModelId, messages,
                        $"{SpinnerFrames[frame % SpinnerFrames.Length]} Thinking\u2026");
                    frame++;
                    await Task.Delay(80);
                }

                var result = await task;
                state = "Ready";
                pane.AddMessage(result.IsSuccess
                    ? result.Value!
                    : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            }
        }
        finally
        {
            terminal.ExitAlternateScreen();
        }
    }
}
