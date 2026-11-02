using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>The launch port over the session-request channel (issue #338): the third caller of
///     a seam that already carried a chain launch and a compaction handoff.
///     <para>WHAT THESE PIN: the payload it emits names the caller's WORKSPACE and nothing else
///     about the relationship; the answer is parsed strictly as an id; an unreachable launch
///     fails with the channel's own code instead of hanging; and the payload grows no parent and
///     no depth - the structural half of the issue.</para></summary>
public class WireAgentLaunchPortTests
{
  private const string Workspace = "ws-alpha";

  private static (WireAgentLaunchPort Port, SessionRequestChannel Channel, List<string> Emitted) Make()
  {
    SessionRequestChannel channel = new();
    List<string> emitted = [];
    channel.Connect(emitted.Add);
    return (new WireAgentLaunchPort(channel, Workspace), channel, emitted);
  }

  [Fact]
  public async Task StartAsync_EmitsThePromptAsARequest_AndAnswersWithTheNewId()
  {
    (WireAgentLaunchPort port, SessionRequestChannel channel, List<string> emitted) = Make();
    string newSessionId = Guid.NewGuid().ToString("D");

    Task<Result<AgentId>> pending = port.StartAsync(
        new AgentLaunchRequest("audit the retry path"), TestContext.Current.CancellationToken);
    SessionRequestPayload sent = JsonSerializer.Deserialize<SessionRequestPayload>(Assert.Single(emitted))!;
    channel.Complete(sent.RequestId, Result.Success(newSessionId));

    Result<AgentId> started = await pending.ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);
    Assert.Equal(newSessionId, started.Value.ToString());
    Assert.Equal("audit the retry path", sent.Prompt);
    Assert.Null(sent.HandoffSummary);
    Assert.Null(sent.ChainStep);
  }

  /// <summary>THE SIBLING INTENT IS THE WORKSPACE AND NOTHING MORE. This asserts the payload
  ///     the app receives carries no parent and no depth, which is the acceptance rule stated as
  ///     a property of the wire rather than as a convention.</summary>
  [Fact]
  public async Task StartAsync_PayloadNamesTheCallersWorkspace_AndNoParentAndNoDepth()
  {
    (WireAgentLaunchPort port, SessionRequestChannel channel, List<string> emitted) = Make();

    Task<Result<AgentId>> pending = port.StartAsync(
        new AgentLaunchRequest("go", Label: "auditor", Model: "prov/m"),
        TestContext.Current.CancellationToken);
    string raw = Assert.Single(emitted);
    SessionRequestPayload sent = JsonSerializer.Deserialize<SessionRequestPayload>(raw)!;
    channel.Complete(sent.RequestId, Result.Success(Guid.NewGuid().ToString("D")));
    _ = await pending.ConfigureAwait(true);

    Assert.Equal(Workspace, sent.SiblingOfWorkspaceId);
    Assert.Equal("auditor", sent.Label);
    Assert.Equal("prov/m", sent.Model);
    Assert.DoesNotContain("parent", raw, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("depth", raw, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task StartAsync_WithNoLiveTurn_FailsUnreachableImmediately()
  {
    // No channel.Connect: the turn that could carry the request does not exist.
    WireAgentLaunchPort port = new(new SessionRequestChannel(), Workspace);

    Result<AgentId> started = await port
        .StartAsync(new AgentLaunchRequest("go"), TestContext.Current.CancellationToken)
        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(started.IsSuccess);
    Assert.Equal("SessionRequestUnreachable", started.Error.Code);
  }

  [Fact]
  public async Task StartAsync_WithNoPrompt_IsRefused_AndEmitsNothing()
  {
    (WireAgentLaunchPort port, SessionRequestChannel _, List<string> emitted) = Make();

    Result<AgentId> started = await port
        .StartAsync(new AgentLaunchRequest("  "), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(started.IsSuccess);
    Assert.Equal("InvalidActionInput", started.Error.Code);
    Assert.Empty(emitted);
  }

  [Fact]
  public async Task StartAsync_WhenTheAppRefuses_PropagatesItsTypedCode()
  {
    (WireAgentLaunchPort port, SessionRequestChannel channel, List<string> emitted) = Make();

    Task<Result<AgentId>> pending = port.StartAsync(
        new AgentLaunchRequest("go"), TestContext.Current.CancellationToken);
    SessionRequestPayload sent = JsonSerializer.Deserialize<SessionRequestPayload>(Assert.Single(emitted))!;
    channel.Complete(sent.RequestId, Result.Failure<string>(new DomainError("TabClosed", "gone")));

    Result<AgentId> started = await pending.ConfigureAwait(true);
    Assert.False(started.IsSuccess);
    Assert.Equal("TabClosed", started.Error.Code);
  }

  [Fact]
  public async Task StartAsync_AnsweringWithANonGuid_FailsMalformed_RatherThanInventingAnId()
  {
    (WireAgentLaunchPort port, SessionRequestChannel channel, List<string> emitted) = Make();

    Task<Result<AgentId>> pending = port.StartAsync(
        new AgentLaunchRequest("go"), TestContext.Current.CancellationToken);
    SessionRequestPayload sent = JsonSerializer.Deserialize<SessionRequestPayload>(Assert.Single(emitted))!;
    channel.Complete(sent.RequestId, Result.Success("not-a-guid"));

    Result<AgentId> started = await pending.ConfigureAwait(true);
    Assert.False(started.IsSuccess);
    Assert.Equal("SessionResponseMalformed", started.Error.Code);
  }
}
