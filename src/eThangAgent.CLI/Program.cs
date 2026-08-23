using System.Collections.Concurrent;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Terminal.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.CLI;

public static class Program
{
    private static readonly string[] SpinnerFrames =
        ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

    public static async Task Main()
    {
        var settings = AgentConfiguration.Load();
        var apiKey = settings.ApiKey
            ?? throw new InvalidOperationException(
                "OPENROUTER_API_KEY environment variable not set. " +
                "Get a key at https://openrouter.ai/keys");

        using var services = new ServiceCollection()
            .AddEThangAgentCore(settings, apiKey,
                ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!,
                new AgentHostOptions(
                    Console.IsInputRedirected
                        ? new PipedClarifyChannel(Console.In)
                        : new InteractiveClarifyChannel(new AnsiTerminal(), new AnsiTerminal()),
                    new CwdWorkspaceContext(),
                    new WorkspacePathResolver(Path.GetFullPath("."))))
            .BuildServiceProvider();

        var handler = services.GetRequiredService<SendMessageCommandHandler>();
        var modelConfig = services.GetRequiredService<ModelConfig>();
        var lifecycle = services.GetRequiredService<RootSessionLifecycle>();

        // Root session bootstrap: identical to before.
        var store = services.GetRequiredService<IAgentStore>();
        var conversation = services.GetRequiredService<Conversation>();
        var rootId = AgentId.NewId();
        var rootSaved = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow));
        if (!rootSaved.IsSuccess)
            throw new InvalidOperationException(
                "failed to persist root session: " +
                $"[{rootSaved.Error!.Code}] {rootSaved.Error.Message}");

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            await RunRedirectedRepl(handler, lifecycle, conversation, rootId);
        else
            await RunInteractiveRepl(handler, modelConfig, lifecycle, conversation, rootId);
    }

    /// <summary>Line-based REPL for redirected I/O (pipes, E2E tests).</summary>
    private static async Task RunRedirectedRepl(SendMessageCommandHandler handler,
        RootSessionLifecycle lifecycle, Conversation conversation, AgentId rootId)
    {
        Console.WriteLine("eThang Agent - type /help for commands");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null)
                break; // EOF: stdin closed (pipe broken, host gone). Exiting beats spinning.
            var input = line.Trim();
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
            await lifecycle.AppendExchangeAsync(rootId, conversation, messageCountBefore,
                result, Console.Error.WriteLine);
            Console.WriteLine(result.IsSuccess ? result.Value : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            Console.WriteLine();
        }

        await lifecycle.CompleteAsync(rootId, Console.Error.WriteLine);
    }

    /// <summary>
    ///     Full-screen REPL: alternate screen, transcript pane, statusline with spinner,
    ///     and the event-driven line editor on the input row. Resize is picked up on each
    ///     new prompt via a full redraw.
    /// </summary>
    private static async Task RunInteractiveRepl(SendMessageCommandHandler handler, ModelConfig modelConfig,
        RootSessionLifecycle lifecycle, Conversation conversation, AgentId rootId)
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

                // Streaming: agent callbacks enqueue events from whatever thread they run on;
                // only this frame loop touches the terminal, redrawing the pane (~12 fps)
                // until the turn resolves.
                var streamEvents = new ConcurrentQueue<StreamEvent>();
                var streamedAny = false;
                var messageCountBefore = conversation.Messages.Count;
                var task = handler.Handle(new SendMessageCommand(input),
                    onContentDelta: delta =>
                    {
                        streamedAny = true;
                        streamEvents.Enqueue(new StreamEvent.Delta(delta));
                    },
                    onReasoningDelta: reasoning =>
                        streamEvents.Enqueue(new StreamEvent.Reasoning(reasoning)),
                    onIterationEnd: () => streamEvents.Enqueue(new StreamEvent.IterationEnd()),
                    onToolCall: (name, args) =>
                        streamEvents.Enqueue(new StreamEvent.ToolCallEvent(name, args)),
                    onToolResult: (name, summary) =>
                        streamEvents.Enqueue(new StreamEvent.ToolResultEvent(name, summary)));
                pane.BeginStream();

                var frame = 0;
                while (!task.IsCompleted)
                {
                    // Re-read the console geometry every frame: a resize during the turn must
                    // never leave the panes targeting rows past the shrunken buffer, which
                    // throws ArgumentOutOfRangeException from SetCursorPosition.
                    width = Console.WindowWidth;
                    height = Console.WindowHeight;
                    layout = TuiLayout.Compute(height);
                    DrainStream(streamEvents, pane);
                    var phase = streamedAny ? "Streaming" : "Thinking";
                    status.Render(terminal, layout.StatusRow, width, modelConfig.ModelId, messages,
                        $"{SpinnerFrames[frame % SpinnerFrames.Length]} {phase}\u2026");
                    pane.Render(terminal, layout.TranscriptTop, layout.TranscriptHeight, width);
                    frame++;
                    await Task.Delay(80);
                }
                DrainStream(streamEvents, pane);

                var result = await task;
                await lifecycle.AppendExchangeAsync(rootId, conversation, messageCountBefore,
                    result, Console.Error.WriteLine);
                state = "Ready";
                // Streamed content is already on screen; only failures and turns that produced
                // no deltas (non-streaming fallback) print a line here.
                if (!result.IsSuccess || !streamedAny)
                    pane.AddMessage(result.IsSuccess
                        ? result.Value!
                        : $"Error [{result.Error!.Code}]: {result.Error.Message}");
            }

            // Both graceful exits (/exit, /quit, Ctrl+D) land here inside the try so the
            // session is completed before the alternate screen tears down.
            await lifecycle.CompleteAsync(rootId, Console.Error.WriteLine);
        }
        finally
        {
            terminal.ExitAlternateScreen();
        }
    }

    /// <summary>Presentation-side stream events queued by agent callbacks. The REPL frame loop
    ///     owns every terminal write; callbacks running on arbitrary threads only enqueue.</summary>
    private abstract class StreamEvent
    {
        public sealed class Delta(string text) : StreamEvent
        {
            public string Text { get; } = text;
        }

        public sealed class IterationEnd : StreamEvent;

        public sealed class Reasoning(string text) : StreamEvent
        {
            public string Text { get; } = text;
        }

        public sealed class ToolCallEvent(string name, string arguments) : StreamEvent
        {
            public string Name { get; } = name;
            public string Arguments { get; } = arguments;
        }

        public sealed class ToolResultEvent(string name, string summary) : StreamEvent
        {
            public string Name { get; } = name;
            public string Summary { get; } = summary;
        }
    }

    /// <summary>Drains queued stream events into the transcript pane between frames. An iteration
    ///     end re-opens the stream so the next provider iteration starts on a separated line —
    ///     and reuses the empty stream-start line when an iteration streamed no content.</summary>
    private static void DrainStream(ConcurrentQueue<StreamEvent> events, TranscriptPane pane)
    {
        while (events.TryDequeue(out var streamEvent))
        {
            switch (streamEvent)
            {
                case StreamEvent.Delta delta:
                    pane.AppendStream(delta.Text);
                    break;
                case StreamEvent.IterationEnd:
                    pane.BeginStream();
                    break;
                case StreamEvent.Reasoning r:
                    pane.AppendReasoning(r.Text);
                    break;
                case StreamEvent.ToolCallEvent tc:
                    pane.AppendToolCall(tc.Name, tc.Arguments);
                    break;
                case StreamEvent.ToolResultEvent tr:
                    pane.AppendToolResult(tr.Name, tr.Summary);
                    break;
            }
        }
    }
}
