using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

/// <summary>Composition root for the desktop frontend: shared core + desktop-specific seams.
///     Startup validation failures surface as an error dialog followed by exit code 1.</summary>
public static class DesktopHost
{
    public static async Task<MainWindow> CreateMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = AgentConfiguration.Load();
        if (settings.ApiKey is null)
        {
            await ShowErrorAndExitAsync(desktop,
                "OPENROUTER_API_KEY environment variable not set. Get a key at https://openrouter.ai/keys");
            throw new UnreachableException("unreachable after error dialog shutdown");
        }

        var services = new ServiceCollection()
            .AddEThangAgentCore(settings, settings.ApiKey,
                ModelConfig.Create("stealth/ox-alpha", 1024, 0.7f).Value!,
                new AgentHostOptions(
                    new AvaloniaClarifyChannel(PresentLater),
                    new FixedWorkspaceContext("app"),
                    new UnrootedPathResolver()))
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

        var conversation = services.GetRequiredService<Conversation>();
        var handler = services.GetRequiredService<SendMessageCommandHandler>();
        var lifecycle = services.GetRequiredService<RootSessionLifecycle>();
        var modelConfig = services.GetRequiredService<ModelConfig>();

        // The clarify channel's present hook resolves the view-model lazily: the channel is
        // registered before the VM exists, but presenting only happens mid-turn when it does.
        MainViewModel? viewModel = null;
        var channel = new AvaloniaClarifyChannel(q =>
            PresentOnUIThread(() => viewModel!.PresentClarifyAsync(q)));

        var vm = new MainViewModel(
            (command, ct, content, reasoning, iterationEnd, toolCall, toolResult) =>
                handler.Handle(command, ct, content, reasoning, iterationEnd, toolCall, toolResult),
            lifecycle,
            rootId,
            conversation,
            modelConfig.ModelId,
            requestClose: () => Dispatcher.UIThread.Post(() =>
                (desktop.MainWindow as Window)?.Close()));
        viewModel = vm;
        vm.AttachClarifyChannel(channel);

        return new MainWindow(vm);
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
}