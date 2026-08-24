namespace eThangAgent.Desktop;

using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

/// <summary>
///     Clarify channel for the desktop UI. Presents a question through the injected
///     <paramref name="present" /> hook, which builds (and surfaces) a
///     <see cref="ClarifyViewModel"/>, then awaits that view-model's one-shot completion.
///     The hook starts as a placeholder that reports PresenterUnavailable — a structured,
///     model-actionable failure — until <see cref="SetPresenter"/> installs the real one
///     once the view-model exists. Keeping it injectable lets unit tests drive the channel
///     without a Dispatcher.
/// </summary>
public sealed class AvaloniaClarifyChannel(Func<ClarifyQuestion, Task<ClarifyViewModel>>? present)
    : IClarifyChannel
{
    private Func<ClarifyQuestion, Task<ClarifyViewModel>>? _present = present;

    /// <summary>Installs the real presentation hook (called by the host when the main
    ///     view-model exists). Passing null reverts to the unavailable placeholder.</summary>
    public void SetPresenter(Func<ClarifyQuestion, Task<ClarifyViewModel>>? present)
        => _present = present;

    public async Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
        if (_present is null)
            return Result<string>.Failure(new Error("PresenterUnavailable",
                "No clarify presenter is attached in this context. Ask your question directly in chat instead."));

        var vm = await _present(question);

        // A cancelled token must not hang on an unanswered question — it cancels the
        // presented view-model, settling Completion with the terminal Cancelled contract.
        using var reg = ct.Register(() => vm.Cancel());

        return await vm.Completion;
    }
}
