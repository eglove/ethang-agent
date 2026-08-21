using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Agent.Application;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace eThangAgent.CLI;

public static class Program
{
    private const string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    /// <summary>Single-line input that raises <see cref="Submitted"/> when Enter (Command.Accept) is pressed.</summary>
    private sealed class ChatInput : TextField
    {
        public event Action<string>? Submitted;

        public ChatInput()
        {
            // Typed delegate to select the Func overload of the (otherwise ambiguous) AddCommand pair.
            Func<Nullable<bool>> accept = () =>
            {
                var text = Text ?? string.Empty;
                Text = string.Empty;
                Submitted?.Invoke(text);
                return true;
            };
            AddCommand(Command.Accept, accept);
        }
    }

    public static async Task Main()
    {
        CliDriver.Register();
        CliDriver.ApplyPerformanceSettings();
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
            await RunPlainRepl(handler, modelConfig);
        else
            RunTui(handler, modelConfig);
    }

    /// <summary>Scrolling REPL for redirected I/O (pipes, E2E tests).</summary>
    private static async Task RunPlainRepl(SendMessageCommandHandler handler, ModelConfig modelConfig)
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
            if (result.IsSuccess)
                Console.WriteLine(result.Value);
            else
                Console.WriteLine($"Error [{result.Error!.Code}]: {result.Error.Message}");

            Console.WriteLine();
        }
    }

    /// <summary>Full-screen TUI: message area on top, input field above a status line that shows the model and a spinner while waiting.</summary>
    private static void RunTui(SendMessageCommandHandler handler, ModelConfig modelConfig)
    {
        using var app = CliDriver.InitApplication();

        var modelId = modelConfig.ModelId;
        var spinnerIndex = 0;
        var cts = new CancellationTokenSource();
        Object? spinnerToken = null;

        // ── Status line (bottom row) ─────────────────────────────────────
        var status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(),
            Width = Dim.Fill(),
            Height = 1,
            Text = modelId
        };

        // ── Input field (row above the status line) ──────────────────────
        var input = new ChatInput
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1
        };

        // Slash-command autocomplete: shows the matching command inline as you type; Tab accepts it.
        input.Autocomplete = new AppendAutocomplete(input)
        {
            SuggestionGenerator = new CommandSuggestionGenerator(CliCommands.All)
        };

        // ── Message area (fills the rest) ─────────────────────────────────
        var messages = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(input),
            ReadOnly = true,
            WordWrap = true,
            CanFocus = false,
            Text = $"eThang Agent  —  {modelId}\nType a message and press Enter.  /help for commands."
        };

        void AddMessage(string text)
        {
            app.Invoke(() =>
            {
                var existing = messages.Text;
                messages.Text = string.IsNullOrEmpty(existing) ? text : existing + "\n" + text;
                messages.ScrollTo(new System.Drawing.Point(0, int.MaxValue));
                messages.SetNeedsDraw();
            });
        }

        Object? StartSpinner()
        {
            spinnerIndex = 0;
            var token = app.AddTimeout(
                TimeSpan.FromMilliseconds(80),
                () =>
                {
                    spinnerIndex = (spinnerIndex + 1) % SpinnerFrames.Length;
                    status.Text = $"{modelId}  │  {SpinnerFrames[spinnerIndex]} Thinking...";
                    status.SetNeedsDraw();
                    return true; // keep repeating
                });
            spinnerToken = token;
            return token;
        }

        void StopSpinner(Object? token)
        {
            if (token != null && ReferenceEquals(spinnerToken, token))
            {
                app.RemoveTimeout(token);
                spinnerToken = null;
            }

            app.Invoke(() =>
            {
                status.Text = modelId;
                status.SetNeedsDraw();
            });
        }

        input.Submitted += text =>
        {
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (CliCommands.IsHelp(text))
            {
                AddMessage(CliCommands.Describe());
                return;
            }

            if (CliCommands.IsQuit(text))
            {
                cts.Cancel();
                app.RequestStop();
                return;
            }

            AddMessage("> " + text);
            var token = StartSpinner();

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await handler.Handle(new SendMessageCommand(text), cts.Token);
                    if (result.IsSuccess)
                        AddMessage(result.Value!);
                    else
                        AddMessage($"Error [{result.Error!.Code}]: {result.Error.Message}");
                }
                catch (Exception ex)
                {
                    AddMessage($"Error: {ex.Message}");
                }
                finally
                {
                    StopSpinner(token);
                }
            });
        };

        var top = new Window { Title = "eThang Agent" };
        top.Add(messages);
        top.Add(input);
        top.Add(status);

        app.Run(top);
    }
}
