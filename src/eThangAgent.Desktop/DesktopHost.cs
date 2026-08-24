using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

/// <summary>Everything <see cref="CreateMainWindow"/> needs, prepared OFF the UI thread:
///     configuration, service provider, and the persisted root session.</summary>
public sealed record DesktopBootstrap(
    IServiceProvider Services,
    AgentId RootId,
    Conversation Conversation,
    SendMessageCommandHandler Handler,
    RootSessionLifecycle Lifecycle,
    string ModelId);

/// <summary>Composition root for the desktop frontend: shared core + desktop-specific seams.
///     Startup begins with workspace selection (a native folder picker with a re-prompt loop);
///     the chosen directory roots path resolution, workspace identity, process cwd, and — when
///     an AGENTS.md exists there — a verbatim system-prompt injection announcing it as read.
///     Bootstrap validation failures surface as an error dialog followed by exit code 1.</summary>
public static class DesktopHost
{
    /// <summary>Background-thread-safe preparation: strict config load, provider build, and
    ///     root-session persistence. Constructs NO Avalonia controls (they are thread-affine
    ///     and must be built on the UI thread via <see cref="CreateMainWindow"/>).
    ///     <paramref name="workspaceRoot"/> must exist; it becomes the agent's working
    ///     directory in every sense: path resolution, workspace identity, process cwd.</summary>
    public static async Task<DesktopBootstrap> PrepareAsync(
        IClassicDesktopStyleApplicationLifetime desktop, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            await ShowErrorAndExitAsync(desktop,
                $"workspace directory not found: '{workspaceRoot}'.");
            throw new UnreachableException("unreachable after error dialog shutdown");
        }

        workspaceRoot = Path.GetFullPath(workspaceRoot);

        // exec scripts resolve relative paths through their globals' Workspace, which
        // captures Environment.CurrentDirectory at execution time — align it with the
        // chosen root so every tool sees one consistent workspace.
        Environment.CurrentDirectory = workspaceRoot;

        var settings = AgentConfiguration.Load();
        if (settings.ApiKey is null)
        {
            await ShowErrorAndExitAsync(desktop,
                "OPENROUTER_API_KEY environment variable not set. Get a key at https://openrouter.ai/keys");
            throw new UnreachableException("unreachable after error dialog shutdown");
        }

        var services = new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey,
                ModelConfig.Create("stealth/ox-alpha", 32 * 1024, 0.7f).Value!,
                new AgentHostOptions(
                    new AvaloniaClarifyChannel(PresentLater),
                    new FixedWorkspaceContext(workspaceRoot),
                    new WorkspacePathResolver(workspaceRoot),
                    [new WorkspaceInstructionsPromptProvider(workspaceRoot)]))
            .BuildServiceProvider();

        var store = services.GetRequiredService<IAgentStore>();
        var rootId = AgentId.NewId();
        var saved = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow));
        if (!saved.IsSuccess)
        {
            await ShowErrorAndExitAsync(desktop,
                $"failed to persist root session: [{saved.Error!.Code}] {saved.Error.Message}");
            throw new UnreachableException("unreachable after error dialog shutdown");
        }

        return new DesktopBootstrap(
            services,
            rootId,
            services.GetRequiredService<Conversation>(),
            services.GetRequiredService<SendMessageCommandHandler>(),
            services.GetRequiredService<RootSessionLifecycle>(),
            services.GetRequiredService<ModelConfig>().ModelId);
    }

    /// <summary>Opens the platform folder picker and returns the chosen directory's local
    ///     path, or null when the user cancels. MUST run on the UI thread: native folder
    ///     dialogs need a parent window handle, supplied by a transient 1x1 host window.</summary>
    public static async Task<string?> PickWorkspaceFolderAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.VerifyAccess();

        var host = TransientHostWindow();
        host.Show();
        try
        {
            var folders = await host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the directory eThang Agent will work in",
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>Shown after a cancelled pick: the workspace is mandatory. Returns true to
    ///     re-open the folder picker, false to exit the application. Closing the dialog
    ///     counts as declining (false). MUST run on the UI thread.</summary>
    public static async Task<bool> ShowRequiredDialogAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        Dispatcher.UIThread.VerifyAccess();

        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chooseAgain = new Button { Content = "Choose again" };
        var exit = new Button { Content = "Exit" };
        chooseAgain.Click += (_, _) => { choice.TrySetResult(true); };
        exit.Click += (_, _) => { choice.TrySetResult(false); };

        var dialog = new Window
        {
            Title = "eThang Agent — working directory required",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "A working directory is required before eThang Agent can start.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 12,
                        Children = { chooseAgain, exit },
                    },
                },
            },
        };
        dialog.Closed += (_, _) => choice.TrySetResult(false);

        var host = TransientHostWindow();
        host.Show();
        try
        {
            await dialog.ShowDialog(host);
            return await choice.Task;
        }
        finally
        {
            host.Close();
        }
    }

    private static Window TransientHostWindow() => new()
    {
        ShowInTaskbar = false,
        SystemDecorations = SystemDecorations.None,
        ShowActivated = false,
        Width = 1,
        Height = 1,
    };

    /// <summary>Builds the view-model and main window. MUST run on the UI thread — Avalonia
    ///     controls are thread-affine (calling this off-thread throws "Call from invalid thread").</summary>
    public static MainWindow CreateMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop, DesktopBootstrap boot)
    {
        Dispatcher.UIThread.VerifyAccess();

        // The clarify channel's present hook resolves the view-model lazily: the channel is
        // created before the VM exists, but presenting only happens mid-turn when it does.
        MainViewModel? viewModel = null;
        var channel = new AvaloniaClarifyChannel(q =>
            PresentOnUIThread(() => viewModel!.PresentClarifyAsync(q)));

        var vm = new MainViewModel(
            OffUiThread((command, ct, content, reasoning, iterationEnd, toolCall, toolResult) =>
                boot.Handler.Handle(command, ct, content, reasoning,
                    iterationEnd, toolCall, toolResult)),
            boot.Lifecycle,
            boot.RootId,
            boot.Conversation,
            boot.ModelId,
            requestClose: () => Dispatcher.UIThread.Post(() => desktop.MainWindow?.Close()),
            uiStreamSink: evt => viewModel!.ApplyUiStreamEventOnUIThreadAsync(evt));
        viewModel = vm;
        vm.AttachClarifyChannel(channel);

        return new MainWindow(vm);
    }

    /// <summary>Shows the startup-error dialog on the UI thread and shuts down with exit code 1
    ///     when it closes. Safe to call from any thread.</summary>
    public static async Task ShowErrorAndExitAsync(
        IClassicDesktopStyleApplicationLifetime desktop, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var exit = new Button { Content = "Exit" };
            var dialog = new Window
            {
                Title = "eThang Agent — startup error",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 480,
                        },
                        exit,
                    },
                },
            };
            exit.Click += (_, _) => dialog.Close();
            dialog.Closed += (_, _) => desktop.Shutdown(1); // non-zero exit per spec
            dialog.Show();
        });
    }

    /// <summary>Wraps a turn runner so each turn executes on the worker pool. The agent
    /// loop must never run on the UI thread: its awaits would post back to Avalonia's
    /// SynchronizationContext, and one sync-blocking tool or script would deadlock the
    /// app (observed in production as a frozen turn with nothing persisted). UI updates
    /// flow back only through the stream sink and clarify channel, which marshal
    /// explicitly onto the dispatcher.</summary>
    public static TurnRunner OffUiThread(TurnRunner inner)
    {
        return (command, ct, contentDelta, reasoningDelta, iterationEnd, toolCall, toolResult) =>
        {
            // Suppress the execution context along with the thread switch: Task.Run alone
            // still flows the caller's SynchronizationContext (.NET 6+), which would pin
            // the domain loop's continuations to the UI pump.
            Task<Result<string>> scheduled;
            using (ExecutionContext.SuppressFlow())
                scheduled = Task.Run(() => inner(command, ct, contentDelta,
                    reasoningDelta, iterationEnd, toolCall, toolResult));
            return scheduled;
        };
    }

    private static Task<ClarifyViewModel> PresentLater(ClarifyQuestion question) =>
        throw new InvalidOperationException(
            "clarify presenter is attached by the host once the view-model exists");

    /// <summary>Marshals clarify presentation onto the UI thread, propagating both the
    ///     presented view-model and any fault back to the awaiting agent thread.</summary>
    private static async Task<ClarifyViewModel> PresentOnUIThread(
        Func<Task<ClarifyViewModel>> present)
    {
        var tcs = new TaskCompletionSource<ClarifyViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try { tcs.SetResult(await present()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task;
    }
}