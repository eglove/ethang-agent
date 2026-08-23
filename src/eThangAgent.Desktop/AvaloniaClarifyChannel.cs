using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop;

/// <summary>
///     Clarify channel for the desktop UI. Presents the question by asking the injected
///     <paramref name="present"/> hook to build (and surface) a <see cref="ClarifyViewModel"/>,
///     then awaits that view-model's one-shot completion. The hook is where the UI-thread
///     marshalling lives (wired in Task 12); keeping it injectable lets unit tests drive the
///     channel without a Dispatcher.
/// </summary>
public sealed class AvaloniaClarifyChannel(Func<ClarifyQuestion, Task<ClarifyViewModel>> present)
    : IClarifyChannel
{
    public async Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
        var vm = await present(question);

        // A cancelled token must not hang on an unanswered question — it cancels the
        // presented view-model, settling Completion with the terminal Cancelled contract.
        using var reg = ct.Register(() => vm.Cancel());

        return await vm.Completion;
    }
}
