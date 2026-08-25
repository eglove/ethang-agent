using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

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
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"options":["a","b"],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("question", result.Content);
    }

    [Fact]
    public async Task EmptyQuestion_ReturnsError()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"","options":["a","b"],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("question", result.Content);
    }

    [Fact]
    public async Task Question_MustBeString_NumberRejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":7,"options":["a","b"],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingAllowFreeText_ReturnsError()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":["a","b"]}"""));
        Assert.True(result.IsError);
        Assert.Contains("allowFreeText", result.Content);
    }

    [Fact]
    public async Task AllowFreeText_MustBeBoolean_StringRejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":["a","b"],"allowFreeText":"yes"}"""));
        Assert.True(result.IsError);
        Assert.Contains("boolean", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Options array rules ----

    [Fact]
    public async Task SingleOptionArray_Rejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":["only"],"allowFreeText":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("at least 2", result.Content);
    }

    [Fact]
    public async Task Options_MustBeArray_StringRejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":"a","allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("array", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Options_NonStringElement_Rejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":["a",3],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Options_EmptyElement_Rejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":["a",""],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("non-empty", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await MakeTool(UnusedChannel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Pick one","options":["a","b"],"allowFreeText":true,"timeout":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("timeout", result.Content);
    }

    [Fact]
    public async Task FreeTextBlocked_WithoutOptions_Unsatisfiable_Rejected()
    {
        // Scripted so that, without the gate, the call would burn a human answer
        // before dying as FreeTextNotAllowed; the gate must fire at input time.
        var result = await MakeTool(new ScriptedClarifyChannel(Result<string>.Success("1")))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"Pick one","allowFreeText":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("options", result.Content);
        Assert.Contains("allowFreeText", result.Content);
        Assert.Contains("rejected", result.Content);
    }

    // ---- Selection and free text flow ----

    [Fact]
    public async Task NumericAnswer_SelectsOptionTextVerbatim_AndBuildsQuestion()
    {
        var channel = new ScriptedClarifyChannel(Result<string>.Success("2"));
        var result = await new ClarifyTool(channel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Which color?","options":["red","green","blue"],"allowFreeText":false}"""));

        Assert.False(result.IsError);
        Assert.Equal("[clarify] answered: green", result.Content);

        var asked = channel.LastQuestion!;
        Assert.Equal("Which color?", asked.Question);
        Assert.Equal(["red", "green", "blue"], asked.Options);
        Assert.False(asked.AllowFreeText);
    }

    [Fact]
    public async Task FreeTextAllowed_PassesThroughVerbatim()
    {
        var result = await MakeTool(new ScriptedClarifyChannel(Result<string>.Success("teal-ish")))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"Which color?","options":["red","green"],"allowFreeText":true}"""));
        Assert.False(result.IsError);
        Assert.Equal("[clarify] answered: teal-ish", result.Content);
    }

    [Fact]
    public async Task FreeTextWithoutOptions_PassesThrough()
    {
        var result = await MakeTool(new ScriptedClarifyChannel(Result<string>.Success("anything goes")))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"What next?","allowFreeText":true}"""));
        Assert.False(result.IsError);
        Assert.Equal("[clarify] answered: anything goes", result.Content);
    }

    [Fact]
    public async Task FreeTextBlocked_ReturnsFreeTextNotAllowed()
    {
        var result = await MakeTool(new ScriptedClarifyChannel(Result<string>.Success("purple")))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"Which color?","options":["red","green"],"allowFreeText":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [FreeTextNotAllowed]", result.Content);
    }

    [Fact]
    public async Task SelectionZero_IsInvalidSelection()
    {
        var result = await MakeTool(new ScriptedClarifyChannel(Result<string>.Success("0")))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"Which color?","options":["red","green","blue"],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidSelection]", result.Content);
    }

    [Fact]
    public async Task SelectionAboveRange_IsInvalidSelectionNamingValidRange()
    {
        var result = await MakeTool(new ScriptedClarifyChannel(Result<string>.Success("4")))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"Which color?","options":["red","green","blue"],"allowFreeText":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidSelection]", result.Content);
        Assert.Contains("1", result.Content);
        Assert.Contains("3", result.Content);
    }

    [Fact]
    public async Task ChannelFailure_SurfacesVerbatim()
    {
        var result = await MakeTool(new ScriptedClarifyChannel(
                Result<string>.Failure(new Error("TerminalLost", "the terminal went away"))))
            .ExecuteAsync(new RawToolInput("clarify",
                """{"timeoutSeconds":120,"question":"Which color?","options":["red","green"],"allowFreeText":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [TerminalLost]: the terminal went away", result.Content);
    }

    // ---- Execution budget semantics ----

    [Fact]
    public async Task HumanWait_IsNotBoundedByTimeoutSeconds()
    {
        // timeoutSeconds budgets machine work; human thinking time is not machine
        // work. A human answering after even a 1-second budget must still get their
        // answer through — before the fix this died as Error [ToolTimeout], leaking
        // the channel's Error [Cancelled] contract along the way.
        var channel = new ScriptedClarifyChannel(async (_, ct) =>
        {
            await Task.Delay(2000, ct); // far beyond the 1s stated budget
            return Result<string>.Success("late answer");
        });

        var result = await new ClarifyTool(channel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":1,"question":"Take your time","allowFreeText":true}"""));

        Assert.False(result.IsError);
        Assert.Equal("[clarify] answered: late answer", result.Content);
    }


    [Fact]
    public async Task CallerCancellation_Aborts_AnUnboundedWait()
    {
        // With no budget bound, the only way off the wait is the caller's own
        // cancellation (a turn abort): cancelling the caller must settle the ask.
        using var caller = new CancellationTokenSource();
        var channel = new ScriptedClarifyChannel(async (_, ct) =>
        {
            var tcs = new TaskCompletionSource<Result<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = ct.Register(() =>
                tcs.TrySetResult(Result<string>.Failure(new Error("Cancelled", "Cancelled by the user."))));
            return await tcs.Task;
        });

        var run = new ClarifyTool(channel).ExecuteAsync(new RawToolInput("clarify",
            """{"timeoutSeconds":120,"question":"Take your time","allowFreeText":true}"""), caller.Token);
        caller.Cancel();

        var result = await run;
        Assert.True(result.IsError);
        Assert.StartsWith("Error [Cancelled]", result.Content);
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
