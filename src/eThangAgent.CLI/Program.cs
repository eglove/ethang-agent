using Ag = eThangAgent.AgentDomain.Agent;
using eThangAgent.AgentDomain;
using eThangAgent.Terminal.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ToolDomain;
using eThangAgent.Agent.Application;
using eThangAgent.AgentInfrastructure;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.CapabilityDomain;
using eThangAgent.FileSystem.ACL;
using eThangAgent.Storage.ACL;
using eThangAgent.StateDomain;
using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.Configuration;
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

        // Configuration sources: optional appsettings.json next to the executable,
        // overridden by environment variables (SubAgent__DefaultModel,
        // SubAgent__ChildTimeoutSeconds, SubAgent__MaxConcurrentAgents). Binding is strict —
        // invalid values abort startup.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
        var subAgentOptions = SubAgentConfiguration.Bind(
            configuration["SubAgent:DefaultModel"],
            configuration["SubAgent:ChildTimeoutSeconds"],
            configuration["SubAgent:MaxConcurrentAgents"]);

        using var services = new ServiceCollection()
            .AddSingleton(new OpenRouterConfiguration(apiKey, baseUrl))
            .AddHttpClient("OpenRouter", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services
            .AddHttpClient<IModelProvider, OpenRouterModelProvider>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            })
            .Services
            .AddSingleton(_ => ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!)
            .AddSingleton<Conversation>()
            .AddSingleton<IConversationRepository, InMemoryConversationRepository>()
            .AddSingleton<IFileSystemAccess, PowerShellFileSystemAccess>()
            .AddSingleton(ExecOptions.Default)
            .AddSingleton<IExecOutputStore>(_ => new ExecArtifactStore())
            .AddSingleton<IExecActivitySink>(_ => NullExecActivitySink.Instance)
            .AddSingleton<AgentToolsProvider>(sp => new AgentToolsProvider("agent",
                [new AgentToolBinding(
                    new ReadTool(sp.GetRequiredService<IFileSystemAccess>()),
                    "Read lines from a text file.")]))
            .AddSingleton<IWorkspaceContext, CwdWorkspaceContext>()
            .AddSingleton<AppDatabase>()
            .AddSingleton<IStateStore, SqliteStateStore>()
            .AddSingleton<IAgentStore, SqliteAgentStore>()
            .AddSingleton(subAgentOptions)
            .AddSingleton<IModelProviderFactory>(sp => new OpenRouterModelProviderFactory(
                sp.GetRequiredService<OpenRouterConfiguration>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("OpenRouter")))
            .AddSingleton<SubAgentSpawner>()
            .AddSingleton<IAgentRuntime>(sp => new InProcessAgentRuntime(
                sp.GetRequiredService<SubAgentSpawner>(),
                sp.GetRequiredService<IAgentStore>(),
                subAgentOptions.MaxConcurrentAgents))
            .AddSingleton<IAgentSpawnCommand, StartSpawnHandler>()
            .AddSingleton<IAgentQueries, AgentQueries>()
            .AddSingleton<AgentCapabilityProvider>(sp =>
            {
                // Root agent record: depth 0, own identity, never persisted — only
                // spawned children get rows. During a child run the ambient running
                // child is the spawn parent so nested depth enforcement is correct.
                var rootRecord = AgentRecord.Spawned(AgentId.NewId(), null, 0,
                    sp.GetRequiredService<ModelConfig>().ModelId, null,
                    "root session", DateTimeOffset.UtcNow);
                return new AgentCapabilityProvider(
                    sp.GetRequiredService<IAgentSpawnCommand>(),
                    sp.GetRequiredService<IAgentQueries>(),
                    () => SubAgentSpawner.RunningChild ?? rootRecord);
            })
            .AddSingleton<EvidenceOptions>(_ => EvidenceOptions.Default)
            .AddSingleton<IEvidenceRunner, PsEvidenceRunner>()
            .AddSingleton<IStateService, StateService>()
            .AddSingleton<StateCapabilityProvider>()
            .AddSingleton<ICapabilityRegistry>(sp =>
                CapabilityRegistry.Create(
                [
                    new MergedCapabilityProvider("agent",
                    [
                        sp.GetRequiredService<AgentToolsProvider>(),
                        sp.GetRequiredService<AgentCapabilityProvider>(),
                    ]),
                    sp.GetRequiredService<StateCapabilityProvider>(),
                ]))
            .AddSingleton<IExecEngine>(sp => new PowerShellExecEngine(
                new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>()),
                sp.GetRequiredService<ExecOptions>()))
            .AddSingleton<ITool>(sp => new ExecTool(
                sp.GetRequiredService<IExecEngine>(),
                sp.GetRequiredService<ExecOptions>(),
                sp.GetRequiredService<IExecOutputStore>(),
                sp.GetRequiredService<IExecActivitySink>()))
            .AddSingleton<IToolRegistry>(sp =>
                new ToolRegistry([sp.GetRequiredService<ITool>()]))
            .AddSingleton<ISystemPromptProvider>(sp => new CompositeSystemPromptProvider(
            [
                new StaticPromptProvider(
                    "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
                    "workspace, prefer the provided tools over guessing, and keep responses tight."),
                new ExecGuidePromptProvider(
                    new Lazy<ICapabilityRegistry>(() => sp.GetRequiredService<ICapabilityRegistry>())),
            ]))
            .AddSingleton<Ag>(sp =>
            {
                var provider = sp.GetRequiredService<IModelProvider>();
                var conversation = sp.GetRequiredService<Conversation>();
                var config = sp.GetRequiredService<ModelConfig>();
                var tools = sp.GetRequiredService<IToolRegistry>();
                return new Ag(provider, conversation, config, tools,
                    sp.GetRequiredService<ISystemPromptProvider>());
            })
            .AddSingleton<SendMessageCommandHandler>()
            .BuildServiceProvider();

        var handler = services.GetRequiredService<SendMessageCommandHandler>();
        var modelConfig = services.GetRequiredService<ModelConfig>();

        // Root session bootstrap: the REPL conversation persists as an ordinary depth-0 row,
        // so its transcript survives the process and later sessions can recall it.
        var store = services.GetRequiredService<IAgentStore>();
        var conversation = services.GetRequiredService<Conversation>();
        var rootId = AgentId.NewId();
        var rootSaved = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow));
        if (!rootSaved.IsSuccess)
            throw new InvalidOperationException(
                "failed to persist root session: " +
                $"[{rootSaved.Error!.Code}] {rootSaved.Error.Message}");

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            await RunRedirectedRepl(handler, store, conversation, rootId);
        else
            await RunInteractiveRepl(handler, modelConfig, store, conversation, rootId);
    }

    /// <summary>Persists one completed exchange to the root session: the user message then the
    ///     final assistant message — the same Message instances the Conversation aggregate
    ///     holds, never re-mapped copies. An exchange that resolved no assistant response
    ///     appends nothing. Persistence failures surface on stderr; the session continues.</summary>
    private static async Task AppendExchangeAsync(IAgentStore store, AgentId rootId,
        Conversation conversation, int messageCountBefore, Result<string> result)
    {
        if (!result.IsSuccess)
            return;

        var user = await store.AppendMessageAsync(rootId, conversation.Messages[messageCountBefore]);
        if (!user.IsSuccess)
            Console.Error.WriteLine($"Error [{user.Error!.Code}]: {user.Error.Message}");

        var assistant = await store.AppendMessageAsync(rootId, conversation.Messages[^1]);
        if (!assistant.IsSuccess)
            Console.Error.WriteLine($"Error [{assistant.Error!.Code}]: {assistant.Error.Message}");
    }

    /// <summary>Marks the root session Completed on graceful quit: fetches the persisted row and
    ///     transitions it, preserving every other field. Failures surface on stderr — the exit
    ///     itself must not crash.</summary>
    private static async Task CompleteRootSessionAsync(IAgentStore store, AgentId rootId)
    {
        var record = await store.GetAsync(rootId);
        if (!record.IsSuccess)
        {
            Console.Error.WriteLine($"Error [{record.Error!.Code}]: {record.Error.Message}");
            return;
        }

        var updated = await store.UpdateAsync(record.Value! with
        {
            Status = AgentStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        if (!updated.IsSuccess)
            Console.Error.WriteLine($"Error [{updated.Error!.Code}]: {updated.Error.Message}");
    }

    /// <summary>Line-based REPL for redirected I/O (pipes, E2E tests).</summary>
    private static async Task RunRedirectedRepl(SendMessageCommandHandler handler,
        IAgentStore store, Conversation conversation, AgentId rootId)
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

            var messageCountBefore = conversation.Messages.Count;
            var result = await handler.Handle(new SendMessageCommand(input));
            await AppendExchangeAsync(store, rootId, conversation, messageCountBefore, result);
            Console.WriteLine(result.IsSuccess ? result.Value : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            Console.WriteLine();
        }

        await CompleteRootSessionAsync(store, rootId);
    }

    /// <summary>
    ///     Full-screen REPL: alternate screen, transcript pane, statusline with spinner,
    ///     and the event-driven line editor on the input row. Resize is picked up on each
    ///     new prompt via a full redraw.
    /// </summary>
    private static async Task RunInteractiveRepl(SendMessageCommandHandler handler, ModelConfig modelConfig,
        IAgentStore store, Conversation conversation, AgentId rootId)
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
                if (layout.SeparatorRow >= 0)
                {
                    terminal.SetCursorPosition(0, layout.SeparatorRow);
                    terminal.Write(new string('\u2500', width), ConsoleColor.DarkGray);
                }
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

                var messageCountBefore = conversation.Messages.Count;
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
                await AppendExchangeAsync(store, rootId, conversation, messageCountBefore, result);
                state = "Ready";
                pane.AddMessage(result.IsSuccess
                    ? result.Value!
                    : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            }

            // Both graceful exits (/exit, /quit, Ctrl+D) land here inside the try so the
            // session is completed before the alternate screen tears down.
            await CompleteRootSessionAsync(store, rootId);
        }
        finally
        {
            terminal.ExitAlternateScreen();
        }
    }
}
