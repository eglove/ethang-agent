using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
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
  public async Task Option_Selection_Completes_Once_With_Index()
  {
    ClarifyViewModel vm = new(Sample());
    vm.ChooseOption(1);
    Result<string> answer = await vm.Completion.ConfigureAwait(true);
    Assert.Equal("1", answer.Value);

    // Double-complete guarded — settlement is exactly-once.
    vm.Cancel();
    vm.ChooseOption(2);
    Result<string> settled = await vm.Completion.ConfigureAwait(true);
    Assert.True(settled.IsSuccess);
    Assert.Equal("1", settled.Value);
  }

  [Fact]
  public async Task Out_Of_Range_Option_Surfaces_Validation_And_Stays_Pending()
  {
    ClarifyViewModel vm = new(Sample());
    vm.ChooseOption(5);

    // Transient failure: completion is NOT consumed; message is observable.
    Assert.False(vm.Completion.IsCompleted);
    Assert.Equal("Pick an option between 1 and 2.", vm.ValidationMessage);

    vm.ChooseOption(1); // still completable after bad pick
    Result<string> settled = await vm.Completion.ConfigureAwait(true);
    Assert.True(settled.IsSuccess);
    Assert.Equal("1", settled.Value);
  }

  [Fact]
  public async Task Free_Text_Submits_Typed_Answer()
  {
    ClarifyViewModel vm = new(Sample())
    {
      Input = "neither, do this instead"
    };
    vm.SubmitFreeText();
    Result<string> answer = await vm.Completion.ConfigureAwait(true);
    Assert.Equal("neither, do this instead", answer.Value);
  }

  [Fact]
  public async Task Empty_Free_Text_Surfaces_Validation_And_Stays_Pending()
  {
    ClarifyViewModel vm = new(Sample())
    {
      Input = "   "
    };
    vm.SubmitFreeText();

    Assert.False(vm.Completion.IsCompleted);
    Assert.Equal("Type an answer first.", vm.ValidationMessage);

    vm.Input = "ok";
    vm.SubmitFreeText();
    Result<string> answer = await vm.Completion.ConfigureAwait(true);
    Assert.Equal("ok", answer.Value);
  }

  [Fact]
  public async Task Cancel_Matches_Terminal_Cancelled_Contract()
  {
    ClarifyViewModel vm = new(Sample());
    vm.Cancel();
    Result<string> settled = await vm.Completion.ConfigureAwait(true);
    Assert.False(settled.IsSuccess);
    Assert.Equal("Cancelled", settled.Error!.Code);
    Assert.Equal("Cancelled by the user.", settled.Error!.Message);
  }

  // ── Channel ───────────────────────────────────────────────────────────────

  [Fact]
  public async Task Channel_Presents_Question_And_Returns_Answer()
  {
    ClarifyQuestion? presented = null;
    ClarifyViewModel? vm = null;
    AvaloniaClarifyChannel channel = new(q =>
    {
      presented = q;
      vm = new ClarifyViewModel(q);
      return Task.FromResult(vm);
    });
    Task<Result<string>> ask = channel.AskAsync(Sample());
    vm!.ChooseOption(2);
    Result<string> result = await ask.ConfigureAwait(true);
    Assert.True(result.IsSuccess);
    Assert.Equal("2", result.Value);
    Assert.Equal("Which approach?", presented!.Question);
  }

  [Fact]
  public async Task Channel_Cancellation_Mid_Wait_Resolves_Cancelled_Contract()
  {
    ClarifyViewModel vm = new(Sample()); // pending — nobody answers
    AvaloniaClarifyChannel channel = new(_ => Task.FromResult(vm));
    using CancellationTokenSource cts = new();

    Task<Result<string>> ask = channel.AskAsync(Sample(), cts.Token);
    Assert.False(ask.IsCompleted); // still waiting on the human

    await cts.CancelAsync(); // the user walks away mid-question

    // Bounded wait: a TimeoutException here would mean AskAsync ignored the
    // CancellationToken and hung awaiting Completion.
    _ = await ask.WaitAsync(TimeSpan.FromSeconds(5));

    Result<string> result = await ask.ConfigureAwait(true);
    Assert.False(result.IsSuccess);
    Assert.Equal("Cancelled", result.Error!.Code);
    Assert.Equal("Cancelled by the user.", result.Error!.Message);
  }

  [Fact]
  public async Task RejectInput_Surfaces_Message_Without_Consuming_Completion()
  {
    ClarifyViewModel vm = new(Sample());

    vm.RejectInput("Enter a number between 1 and 2.");

    // Transient rejection: completion is NOT consumed; message is observable.
    Assert.False(vm.Completion.IsCompleted);
    Assert.Equal("Enter a number between 1 and 2.", vm.ValidationMessage);

    vm.ChooseOption(1); // still completable after a rejection
    Result<string> settled = await vm.Completion.ConfigureAwait(true);
    Assert.True(settled.IsSuccess);
    Assert.Equal("1", settled.Value);
  }

  // ── Integration: session view-model routing while a question is pending ────────

  [Fact]
  public async Task SessionViewModel_Routes_Unroutable_Input_Through_RejectInput()
  {
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) => Task.FromResult(Result.Success("unused")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter",
        "m", workspaceRoot: @"C:\work\demo");
    _ = await vm.PresentClarifyAsync(
        new ClarifyQuestion("Which approach?", ["first", "second"], AllowFreeText: false));
    ClarifyViewModel clarify = vm.Clarify!;

    await vm.SubmitAsync("bananas"); // neither free text nor a number

    // Routed rejection: message surfaces, question stays up and pending.
    Assert.False(clarify.Completion.IsCompleted);
    Assert.Equal("Enter a number between 1 and 2.", clarify.ValidationMessage);
    Assert.NotNull(vm.Clarify);

    await vm.SubmitAsync("2"); // still routable afterwards
    Result<string> settled = await clarify.Completion.ConfigureAwait(true);
    Assert.True(settled.IsSuccess);
    Assert.Equal("2", settled.Value);
    Assert.Null(vm.Clarify);
    Assert.Contains(vm.Transcript.Entries.OfType<UserMessageEntry>(), e => e.Text == "2");
  }

  [Fact]
  public async Task SessionViewModel_Routes_Input_To_Pending_Clarify()
  {
    TaskCompletionSource<ClarifyViewModel> questionGate = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    AgentSessionViewModel vm = null!;
    // The channel presents through the session view-model, which publishes vm.Clarify.
    AvaloniaClarifyChannel channel = new(async q =>
    {
      ClarifyViewModel cvm = await vm.PresentClarifyAsync(q).ConfigureAwait(true);
      questionGate.SetResult(cvm);
      return cvm;
    });

    // Runner awaits the clarify channel mid-turn so IsBusy is true when input routes.
    vm = new AgentSessionViewModel(
        async (_, _, _, _, _, _, _, _) =>
        {
          Result<string> answer = await channel.AskAsync(
                  new ClarifyQuestion("Which approach?", ["first", "second"], true)).ConfigureAwait(true);
          return answer.IsSuccess
                  ? Result.Success("turn done")
                  : Result.Failure<string>(answer.Error!);
        },
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter",
        "m", workspaceRoot: @"C:\work\demo");
    Task turn = vm.SubmitAsync("ask me"); // model asks a clarify question mid-turn
    ClarifyViewModel clarify = await questionGate.Task.ConfigureAwait(true);
    Assert.NotNull(vm.Clarify);

    await vm.SubmitAsync("my free answer"); // routed to clarify, not a new turn
    await turn.ConfigureAwait(true);                              // turn completes once the answer settles
    await vm.WaitForTurnAsync();

    Assert.Null(vm.Clarify);
    Assert.Equal(1, vm.MessageCount); // clarify answer did not count as a message
    Assert.Contains(vm.Transcript.Entries.OfType<UserMessageEntry>(),
        e => e.Text.Contains("my free answer", StringComparison.Ordinal));
    // Turn resolved successfully — surfaced as a final-text notice (no deltas streamed).
    Assert.Contains(vm.Transcript.Entries.OfType<NoticeEntry>(),
        n => n.Text.Contains("turn done", StringComparison.Ordinal));
  }

  // ── Panel lifecycle: every settlement path must close the pending question ──

  [Fact]
  public void Settled_Event_Fires_Exactly_Once_Per_Question()
  {
    ClarifyViewModel vm = new(Sample());
    List<int> fired = [];
    vm.Settled += (_, _) => fired.Add(1);

    vm.ChooseOption(1);
    vm.Cancel();      // late settlers must be no-ops
    vm.SubmitFreeText();

    _ = Assert.Single(fired); // settlement is exactly-once, and so is the event
  }

  private static async Task<AgentSessionViewModel> PresentedSessionAsync()
  {
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) => Task.FromResult(Result.Success("unused")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter",
        "m", workspaceRoot: @"C:\work\demo");
    _ = await vm.PresentClarifyAsync(Sample()).ConfigureAwait(false);
    return vm;
  }

  [Fact]
  public async Task SessionViewModel_Closes_Panel_When_Option_Button_Settles_Question()
  {
    AgentSessionViewModel vm = await PresentedSessionAsync();
    Assert.NotNull(vm.Clarify);

    vm.Clarify.ChooseOption(1); // the option-button path — no typed input involved

    Assert.Null(vm.Clarify);
  }

  [Fact]
  public async Task SessionViewModel_Closes_Panel_When_Cancel_Button_Settles_Question()
  {
    AgentSessionViewModel vm = await PresentedSessionAsync();
    Assert.NotNull(vm.Clarify);

    vm.Clarify.Cancel(); // the Cancel button path — equally settlement

    Assert.Null(vm.Clarify);
  }
}
