using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The app-side session-request event handling seam (issue #234): a
///     session-request root-turn event parses on the pump thread and its forward
///     marshals onto the UI thread through the options record's UiMarshal seam —
///     the handler receives (prompt, handoffSummary) verbatim; an unreadable
///     payload surfaces a notice and invokes nothing; an unwired handler refuses
///     with a typed notice; a faulting handler surfaces a SessionRequestFault
///     notice instead of crashing; and the seed-only launch shape (empty prompt +
///     handoff summary) reaches the handler intact.</summary>
public class SessionRequestEventTests
{
  private sealed class RecordingHandler
  {
    public List<(string Prompt, string? HandoffSummary)> Calls { get; } = [];

    public Task Handle(string prompt, string? handoffSummary)
    {
      Calls.Add((prompt, handoffSummary));
      return Task.CompletedTask;
    }
  }

  private static (AgentSessionViewModel Vm, RecordingHandler Handler, FakeRootTurnChannel Host)
      Build(RecordingHandler? handler = null, List<RootTurnEvent>? script = null)
  {
    RecordingHandler recording = handler ?? new RecordingHandler();
    FakeRootTurnChannel host = new([], holdOpen: false);
    if (script is not null)
    {
      foreach (RootTurnEvent evt in script)
      {
        host.Script.Add(evt);
      }
    }

    AgentSessionViewModel vm = new(
        (cmd, _, _, _) => Task.FromResult(Result.Success("ok")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          RootTurnChannel = host,
          SessionRequestHandler = recording.Handle,
          UiMarshal = action => action(),
        });
    return (vm, recording, host);
  }

  private static string Payload(string prompt, string? handoffSummary)
      => System.Text.Json.JsonSerializer.Serialize(new SessionRequestPayload("req-1", prompt, handoffSummary));

  [Fact]
  public async Task SessionRequest_WithWiredHandler_HandlerReceivesPromptAndHandoff()
  {
    (AgentSessionViewModel vm, RecordingHandler handler, FakeRootTurnChannel _) = Build(script:
    [
        new RootTurnEvent("t", "session-request", Payload("explore the auth module", null), 0),
    ]);

    await vm.SubmitAsync("hi").ConfigureAwait(true);

    (string prompt, string? handoff) = Assert.Single(handler.Calls);
    Assert.Equal("explore the auth module", prompt, StringComparer.Ordinal);
    Assert.Null(handoff);
  }

  [Fact]
  public async Task SessionRequest_UnreadablePayload_Notice_HandlerNotInvoked()
  {
    (AgentSessionViewModel vm, RecordingHandler handler, FakeRootTurnChannel _) = Build(script:
    [
        new RootTurnEvent("t", "session-request", "garbage payload", 0),
    ]);

    await vm.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Empty(handler.Calls);
    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("unreadable session-request", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SessionRequest_NoHandlerWired_RefusedNotice_NothingThrown()
  {
    FakeRootTurnChannel host = new([], holdOpen: false);
    host.Script.Add(new RootTurnEvent("t", "session-request", Payload("explore", null), 0));
    AgentSessionViewModel vm = new(
        (cmd, _, _, _) => Task.FromResult(Result.Success("ok")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          RootTurnChannel = host,
          UiMarshal = action => action(),
        });

    await vm.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("Error [SessionRequestRefused]", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SessionRequest_HandlerThrows_FaultNotice_NoCrash()
  {
    FakeRootTurnChannel host = new([], holdOpen: false);
    host.Script.Add(new RootTurnEvent("t", "session-request", Payload("explore", null), 0));
    AgentSessionViewModel vm = new(
        (cmd, _, _, _) => Task.FromResult(Result.Success("ok")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          RootTurnChannel = host,
          SessionRequestHandler = (_, _) => throw new InvalidOperationException("the shell exploded"),
          UiMarshal = action => action(),
        });

    await vm.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("Error [SessionRequestFault]", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SessionRequest_SeedOnlyLaunch_HandlerReceivesEmptyPromptAndSummary()
  {
    (AgentSessionViewModel vm, RecordingHandler handler, FakeRootTurnChannel _) = Build(script:
    [
        new RootTurnEvent("t", "session-request", Payload("", "the handoff summary"), 0),
    ]);

    await vm.SubmitAsync("hi").ConfigureAwait(true);

    (string prompt, string? handoff) = Assert.Single(handler.Calls);
    Assert.Equal("", prompt, StringComparer.Ordinal);
    Assert.Equal("the handoff summary", handoff, StringComparer.Ordinal);
  }
}
