using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>new_session (issue #234 task 1): strict argument validation, one
///     port call per invocation, the verbatim confirmation line carrying the
///     port's session id, and verbatim port-failure codes.</summary>
public class NewSessionToolTests
{
  [Fact]
  public async Task Success_ConfirmationCarriesPortSessionId()
  {
    FakePort port = new("sess-abc-123");
    NewSessionTool tool = new(port);

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":\"x\"}"),
        TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[new session sess-abc-123 started; the conversation continues there.]", result.Content);
    Assert.Equal("x", port.LastPrompt);
  }

  [Fact]
  public async Task Success_CallsPortExactlyOnce()
  {
    FakePort port = new("sess-abc-123");
    NewSessionTool tool = new(port);

    _ = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":\"x\"}"),
        TestContext.Current.CancellationToken);
    Assert.Equal(1, port.Calls);
  }

  [Fact]
  public async Task Cancellation_PropagatesToPort()
  {
    using CancellationTokenSource cts = new();
    FakePort port = new("sess-abc-123");
    NewSessionTool tool = new(port);

    _ = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":\"x\"}"), cts.Token);
    Assert.True(port.LastCancellationToken.CanBeCanceled);
  }

  [Fact]
  public async Task PortFailure_FailsVerbatim()
  {
    NewSessionTool tool = new(new FakePort(new DomainError("SessionLaunchFailed", "the host is down.")));

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":\"x\"}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Equal("Error [SessionLaunchFailed]: the host is down.", result.Content);
  }

  [Fact]
  public async Task Missing_Prompt_FailsWithMissingParameter()
  {
    NewSessionTool tool = new(new FakePort("sess-abc-123"));

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
    Assert.Contains("'prompt'", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public async Task Empty_Prompt_FailsWithInvalidInput(string prompt)
  {
    NewSessionTool tool = new(new FakePort("sess-abc-123"));

    string args = "{\"timeoutSeconds\":120,\"prompt\":\"" + prompt + "\"}"; // JSON assembled per case (JSON001: the concatenation is not a complete literal).
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("new_session", args), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidInput]", result.Content, StringComparison.Ordinal);
    Assert.Contains("prompt must be a non-empty string.", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WrongType_Prompt_FailsWithInvalidParameterType()
  {
    NewSessionTool tool = new(new FakePort("sess-abc-123"));

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":123}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterType]", result.Content, StringComparison.Ordinal);
    Assert.Contains("'prompt' must be a string, but got Number.", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Unknown_Parameter_FailsWithUnknownParameter()
  {
    NewSessionTool tool = new(new FakePort("sess-abc-123"));

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":\"x\",\"workspace\":\"C:/tmp\"}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [UnknownParameter]", result.Content, StringComparison.Ordinal);
    Assert.Contains("workspace", result.Content, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds, prompt", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task HandoffSummary_AlwaysNull_FromTheTool()
  {
    FakePort port = new("sess-abc-123");
    NewSessionTool tool = new(port);

    _ = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"prompt\":\"x\"}"),
        TestContext.Current.CancellationToken);
    Assert.Equal(1, port.Calls);
    Assert.Null(port.LastHandoffSummary);
  }

  [Fact]
  public async Task Missing_TimeoutSeconds_FailsWithMissingParameter()
  {
    NewSessionTool tool = new(new FakePort("sess-abc-123"));

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("new_session", /*lang=json,strict*/ "{\"prompt\":\"x\"}"),
        TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Null_Input_Throws()
  {
    NewSessionTool tool = new(new FakePort("sess-abc-123"));
    _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        tool.ExecuteAsync(null!, TestContext.Current.CancellationToken));
  }

  [Fact]
  public void Null_Port_Throws() =>
      Assert.Throws<ArgumentNullException>(() => new NewSessionTool(null!));

  /// <summary>Fake port: returns the configured session id, or fails with the
  ///     configured error; records calls, the handoff summary, and the
  ///     cancellation token for the one-call, null-handoff, and
  ///     token-preservation tests.</summary>
  private sealed class FakePort : INewSessionPort
  {
    private readonly string? _sessionId;
    private readonly DomainError? _error;

    public FakePort(string sessionId) => _sessionId = sessionId;

    public FakePort(DomainError error) => _error = error;

    public int Calls { get; private set; }

    public string? LastPrompt { get; private set; }

    public string? LastHandoffSummary { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<Result<NewSessionPortResult>> StartAsync(string prompt, string? handoffSummary, CancellationToken ct = default)
    {
      Calls++;
      LastPrompt = prompt;
      LastHandoffSummary = handoffSummary;
      LastCancellationToken = ct;
      return _error is not null
          ? Task.FromResult(Result.Failure<NewSessionPortResult>(_error))
          : Task.FromResult(Result.Success(new NewSessionPortResult(_sessionId!)));
    }
  }
}
