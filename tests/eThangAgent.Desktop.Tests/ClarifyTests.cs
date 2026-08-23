using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

public class ClarifyTests
{
    private static ClarifyQuestion Sample(bool freeText = true) =>
        new("Which approach?", ["first", "second"], freeText);

    // ── Interaction tests (transient failures surface via ValidationMessage) ──

    [Fact]
    public void Option_Selection_Completes_Once_With_Index()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.ChooseOption(1);
        Assert.Equal("1", vm.Completion.Result.Value);

        // Double-complete guarded — settlement is exactly-once.
        vm.Cancel();
        vm.ChooseOption(2);
        Assert.True(vm.Completion.Result.IsSuccess);
        Assert.Equal("1", vm.Completion.Result.Value);
    }

    [Fact]
    public void Out_Of_Range_Option_Surfaces_Validation_And_Stays_Pending()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.ChooseOption(5);

        // Transient failure: completion is NOT consumed; message is observable.
        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal("Pick an option between 1 and 2.", vm.ValidationMessage);

        vm.ChooseOption(1); // still completable after bad pick
        Assert.True(vm.Completion.Result.IsSuccess);
        Assert.Equal("1", vm.Completion.Result.Value);
    }

    [Fact]
    public void Free_Text_Submits_Typed_Answer()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.Input = "neither, do this instead";
        vm.SubmitFreeText();
        Assert.Equal("neither, do this instead", vm.Completion.Result.Value);
    }

    [Fact]
    public void Empty_Free_Text_Surfaces_Validation_And_Stays_Pending()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.Input = "   ";
        vm.SubmitFreeText();

        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal("Type an answer first.", vm.ValidationMessage);

        vm.Input = "ok";
        vm.SubmitFreeText();
        Assert.Equal("ok", vm.Completion.Result.Value);
    }

    [Fact]
    public async Task Cancel_Matches_Terminal_Cancelled_Contract()
    {
        var vm = new ClarifyViewModel(Sample());
        vm.Cancel();
        Assert.False(vm.Completion.Result.IsSuccess);
        Assert.Equal("Cancelled", vm.Completion.Result.Error!.Code);
        Assert.Equal("Cancelled by the user.", vm.Completion.Result.Error!.Message);
        await Task.CompletedTask;
    }

    // ── Channel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Channel_Presents_Question_And_Returns_Answer()
    {
        ClarifyQuestion? presented = null;
        ClarifyViewModel? vm = null;
        var channel = new AvaloniaClarifyChannel(q =>
        {
            presented = q;
            vm = new ClarifyViewModel(q);
            return Task.FromResult(vm);
        });
        var ask = channel.AskAsync(Sample());
        vm!.ChooseOption(2);
        var result = await ask;
        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value);
        Assert.Equal("Which approach?", presented!.Question);
    }

    // ── Integration: MainViewModel routing while a question is pending ────────

    [Fact]
    public async Task MainViewModel_Routes_Input_To_Pending_Clarify()
    {
        var questionGate = new TaskCompletionSource<ClarifyViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        MainViewModel vm = null!;
        // The channel presents through the MainViewModel, which publishes vm.Clarify.
        var channel = new AvaloniaClarifyChannel(async q =>
        {
            var cvm = await vm.PresentClarifyAsync(q);
            questionGate.SetResult(cvm);
            return cvm;
        });

        // Runner awaits the clarify channel mid-turn so IsBusy is true when input routes.
        vm = new MainViewModel(
            async (_, _, _, _, _, _, _) =>
            {
                var answer = await channel.AskAsync(
                    new ClarifyQuestion("Which approach?", ["first", "second"], true));
                return answer.IsSuccess
                    ? Result<string>.Success("turn done")
                    : Result<string>.Failure(answer.Error!);
            },
            new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
            "m", () => { });
        vm.AttachClarifyChannel(channel);

        var turn = vm.SubmitAsync("ask me"); // model asks a clarify question mid-turn
        var clarify = await questionGate.Task;
        Assert.NotNull(vm.Clarify);

        await vm.SubmitAsync("my free answer"); // routed to clarify, not a new turn
        await turn;                              // turn completes once the answer settles
        await vm.WaitForTurnAsync();

        Assert.Null(vm.Clarify);
        Assert.Equal(1, vm.MessageCount); // clarify answer did not count as a message
        Assert.Contains(vm.Transcript.Entries.OfType<UserMessageEntry>(),
            e => e.Text.Contains("my free answer"));
        // Turn resolved successfully — surfaced as a final-text notice (no deltas streamed).
        Assert.Contains(vm.Transcript.Entries.OfType<NoticeEntry>(),
            n => n.Text.Contains("turn done"));
    }
}
