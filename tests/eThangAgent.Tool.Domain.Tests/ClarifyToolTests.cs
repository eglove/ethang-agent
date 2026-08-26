using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class ClarifyToolTests
{
  // Never answered: input-validation failures must short-circuit before the channel.
  private static readonly ScriptedClarifyChannel UnusedChannel = new();

  private static ClarifyTool MakeTool(ScriptedClarifyChannel channel) => new(channel);

  // ---- Missing / invalid parameters ----

  [Fact]
  public async Task MissingQuestion_ReturnsError()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"options":["a","b"],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("question", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task EmptyQuestion_ReturnsError()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"","options":["a","b"],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("question", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Question_MustBeString_NumberRejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":7,"options":["a","b"],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task MissingAllowFreeText_ReturnsError()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":["a","b"]}"""));
    Assert.True(result.IsError);
    Assert.Contains("allowFreeText", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AllowFreeText_MustBeBoolean_StringRejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":["a","b"],"allowFreeText":"yes"}"""));
    Assert.True(result.IsError);
    Assert.Contains("boolean", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  // ---- Options array rules ----

  [Fact]
  public async Task SingleOptionArray_Rejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":["only"],"allowFreeText":false}"""));
    Assert.True(result.IsError);
    Assert.Contains("at least 2", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Options_MustBeArray_StringRejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":"a","allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("array", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Options_NonStringElement_Rejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":["a",3],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Options_EmptyElement_Rejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":["a",""],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("non-empty", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Pick one","options":["a","b"],"allowFreeText":true,"timeout":5}"""));
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("timeout", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FreeTextBlocked_WithoutOptions_Unsatisfiable_Rejected()
  {
    // Scripted so that, without the gate, the call would burn a human answer
    // before dying as FreeTextNotAllowed; the gate must fire at input time.
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(Result.Success("1")))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"Pick one","allowFreeText":false}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("options", result.Content, StringComparison.Ordinal);
    Assert.Contains("allowFreeText", result.Content, StringComparison.Ordinal);
    Assert.Contains("rejected", result.Content, StringComparison.Ordinal);
  }

  // ---- Selection and free text flow ----

  [Fact]
  public async Task NumericAnswer_SelectsOptionTextVerbatim_AndBuildsQuestion()
  {
    ScriptedClarifyChannel channel = new(Result.Success("2"));
    ToolResult result = await new ClarifyTool(channel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Which color?","options":["red","green","blue"],"allowFreeText":false}"""));

    Assert.False(result.IsError);
    Assert.Equal("[clarify] answered: green", result.Content);

    ClarifyQuestion asked = channel.LastQuestion!;
    Assert.Equal("Which color?", asked.Question);
    Assert.Equal(["red", "green", "blue"], asked.Options);
    Assert.False(asked.AllowFreeText);
  }

  [Fact]
  public async Task FreeTextAllowed_PassesThroughVerbatim()
  {
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(Result.Success("teal-ish")))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"Which color?","options":["red","green"],"allowFreeText":true}"""));
    Assert.False(result.IsError);
    Assert.Equal("[clarify] answered: teal-ish", result.Content);
  }

  [Fact]
  public async Task FreeTextWithoutOptions_PassesThrough()
  {
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(Result.Success("anything goes")))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"What next?","allowFreeText":true}"""));
    Assert.False(result.IsError);
    Assert.Equal("[clarify] answered: anything goes", result.Content);
  }

  [Fact]
  public async Task FreeTextBlocked_ReturnsFreeTextNotAllowed()
  {
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(Result.Success("purple")))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"Which color?","options":["red","green"],"allowFreeText":false}"""));
    Assert.True(result.IsError);
    Assert.Contains("Error [FreeTextNotAllowed]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SelectionZero_IsInvalidSelection()
  {
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(Result.Success("0")))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"Which color?","options":["red","green","blue"],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidSelection]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SelectionAboveRange_IsInvalidSelectionNamingValidRange()
  {
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(Result.Success("4")))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"Which color?","options":["red","green","blue"],"allowFreeText":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidSelection]", result.Content, StringComparison.Ordinal);
    Assert.Contains("1", result.Content, StringComparison.Ordinal);
    Assert.Contains("3", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ChannelFailure_SurfacesVerbatim()
  {
    ToolResult result = await MakeTool(new ScriptedClarifyChannel(
                Result.Failure<string>(new DomainError("TerminalLost", "the terminal went away"))))
            .ExecuteAsync(new RawToolInput("clarify",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"question":"Which color?","options":["red","green"],"allowFreeText":false}"""));
    Assert.True(result.IsError);
    Assert.Contains("Error [TerminalLost]: the terminal went away", result.Content, StringComparison.Ordinal);
  }

  // ---- Execution budget semantics ----

  [Fact]
  public async Task HumanWait_IsNotBoundedByTimeoutSeconds()
  {
    // timeoutSeconds budgets machine work; human thinking time is not machine
    // work. A human answering after even a 1-second budget must still get their
    // answer through — before the fix this died as Error [ToolTimeout], leaking
    // the channel's Error [Cancelled] contract along the way.
    ScriptedClarifyChannel channel = new(async (_, ct) =>
    {
      await Task.Delay(2000, ct).ConfigureAwait(false); // far beyond the 1s stated budget
      return Result.Success("late answer");
    });

    ToolResult result = await new ClarifyTool(channel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":1,"question":"Take your time","allowFreeText":true}"""));

    Assert.False(result.IsError);
    Assert.Equal("[clarify] answered: late answer", result.Content);
  }


  [Fact]
  public async Task CallerCancellation_Aborts_AnUnboundedWait()
  {
    // With no budget bound, the only way off the wait is the caller's own
    // cancellation (a turn abort): cancelling the caller must settle the ask.
    using CancellationTokenSource caller = new();
    ScriptedClarifyChannel channel = new(async (_, ct) =>
    {
      TaskCompletionSource<Result<string>> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
      // Rule conflict: CA2007 wants ConfigureAwait on every await, but xUnit1030
      // forbids ConfigureAwait(false) inside test methods. 'await using' cannot take
      // one at all. Suppressed locally; the registration lives for microseconds.
#pragma warning disable CA2007
      await using CancellationTokenRegistration reg = ct.Register(() =>
          tcs.TrySetResult(Result.Failure<string>(new DomainError("Cancelled", "Cancelled by the user."))));
#pragma warning restore CA2007
      return await tcs.Task.ConfigureAwait(false);
    });

    Task<ToolResult> run = new ClarifyTool(channel).ExecuteAsync(new RawToolInput("clarify",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"question":"Take your time","allowFreeText":true}"""), caller.Token);
    await caller.CancelAsync().ConfigureAwait(true);

    ToolResult result = await run.ConfigureAwait(true);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [Cancelled]", result.Content, StringComparison.Ordinal);
  }
  private sealed class ScriptedClarifyChannel : IClarifyChannel
  {
    private readonly Func<ClarifyQuestion, CancellationToken, Task<Result<string>>>? _ask;
    private readonly Result<string>[] _answers;
    private int _index;

    public ScriptedClarifyChannel(params Result<string>[] answers) : this(null, answers) { }

    public ScriptedClarifyChannel(Func<ClarifyQuestion, CancellationToken, Task<Result<string>>> ask)
        : this(ask, []) { }

    private ScriptedClarifyChannel(
        Func<ClarifyQuestion, CancellationToken, Task<Result<string>>>? ask,
        Result<string>[] answers)
    {
      _ask = ask;
      _answers = answers;
    }

    public ClarifyQuestion? LastQuestion { get; private set; }

    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
      LastQuestion = question;
      return _ask is not null
          ? _ask(question, ct)
          : Task.FromResult(_answers[_index++]);
    }
  }
}
