using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The session-response routing (issue #234 task 3): the REAL ChildHostServer's
///     dispatch switch routes a session-response envelope to the root runner the turn is
///     running on, and the runner completes the pending request on its channel. The
///     channel's own semantics are pinned in Composition.Tests; this file pins the WIRE
///     side — the envelope kind is dispatched, the payload is decoded, and the outcome
///     reaches the LIVE turn's channel through the production path.
///     <para>Every request runs INSIDE a real turn (the provider parks between deltas),
///     because the channel is connected only while a turn runs — the same shape the
///     production new_session tool sees.</para></summary>
public class ChildHostSessionResponseTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  [Fact]
  public async Task SessionResponse_OverTheRealServer_CompletesThePendingRequest()
  {
    // The turn parks inside the provider; the runner's channel is connected. A request
    // emitted on that channel is answered by the app's session-response envelope through
    // the production dispatch path.
    ServiceCollection services = new();
    _ = services.AddSingleton<NewSessionRequestChannel>();
    Conversation conversation = new();
    _ = services.AddSingleton(conversation);
    TaskCompletionSource parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Park: new ScriptedPark(_ => parked.Task)));
    // The container needs the handler the turn resolves: the REAL SendMessageCommandHandler
    // over the rig's scripted provider and THIS conversation, so the turn streams and parks
    // exactly like a production turn while the channel rides it.
    ModelConfig config = ModelConfig.Create("scripted/model", 4096, 0f, 200_000).Value!;
    _ = services.AddSingleton<IModelProvider>(host.Provider);
    _ = services.AddSingleton(config);
    _ = services.AddSingleton(new SendMessageCommandHandler(
        agent: new Ag(host.Provider, conversation, config, new EmptyToolRegistry(), new AgentOptions { Id = AgentId.NewId() }),
        conversation: conversation));
    ServiceProvider provider = services.BuildServiceProvider();
    // Attach a runner whose container HAS the channel, the way the production dispatch
    // resolves it; the rig's own runner (no channel) is replaced.
    RootRunHost runner = new(provider, AgentId.NewId(), conversation);
    host.Server.AttachRootRun(runner);
    NewSessionRequestChannel channel = provider.GetRequiredService<NewSessionRequestChannel>();
    try
    {
#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
      await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

      // The turn runs THROUGH THE SERVER (the production dispatch path): the server
      // resolves the attached runner, whose RunTurnCoreAsync connects the channel, and
      // the provider parks between its deltas. The first event proves the turn started.
      List<RootTurnEvent> seen = [];
      await app.SendAsync(new TransportEnvelope("root-turn",
          RootTurnProtocol.SerializeRequest(new RootTurnRequest("turn-session", "hello", [])),
          9199), TestContext.Current.CancellationToken).ConfigureAwait(true);
      _ = await ReadRootTurnEventAsync(app, "turn-session", kind: null, sink: seen.Add).ConfigureAwait(true);
      await WaitUntilAsync(() => host.ProviderCalls > 0,
          "provider never called; turn events so far: " + string.Join(" | ", seen.Select(e => e.Kind + ": " + e.Payload))).ConfigureAwait(true);

      Task<Result<string>> pending = channel.RequestAsync("req-wire", "hello", null,
          TestContext.Current.CancellationToken);
      Assert.False(pending.IsCompleted, "the request must stay pending before the response arrives; connected=" + channel.IsConnected);

      // The app's answer, over the REAL dispatch switch.
      await app.SendAsync(new TransportEnvelope("session-response",
          JsonSerializer.Serialize(new SessionResponseCommand("req-wire", "sess-wire-1", null, null)),
          9200), TestContext.Current.CancellationToken).ConfigureAwait(true);

      Result<string> answered = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.True(answered.IsSuccess, answered.Error?.Message);
      Assert.Equal("sess-wire-1", answered.Value);
      parked.SetResult(); // the answer arrived; let the turn finish
    }
    finally
    {
      await provider.DisposeAsync().ConfigureAwait(true);
      runner.Dispose();
    }
  }

  [Fact]
  public async Task SessionResponse_WithTheAppsRefusal_FailsThePendingRequest()
  {
    ServiceCollection services = new();
    _ = services.AddSingleton<NewSessionRequestChannel>();
    Conversation conversation = new();
    _ = services.AddSingleton(conversation);
    TaskCompletionSource parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Park: new ScriptedPark(_ => parked.Task)));
    // The container needs the handler the turn resolves: the REAL SendMessageCommandHandler
    // over the rig's scripted provider and THIS conversation, so the turn streams and parks
    // exactly like a production turn while the channel rides it.
    ModelConfig config = ModelConfig.Create("scripted/model", 4096, 0f, 200_000).Value!;
    _ = services.AddSingleton<IModelProvider>(host.Provider);
    _ = services.AddSingleton(config);
    _ = services.AddSingleton(new SendMessageCommandHandler(
        agent: new Ag(host.Provider, conversation, config, new EmptyToolRegistry(), new AgentOptions { Id = AgentId.NewId() }),
        conversation: conversation));
    ServiceProvider provider = services.BuildServiceProvider();
    RootRunHost runner = new(provider, AgentId.NewId(), conversation);
    host.Server.AttachRootRun(runner);
    NewSessionRequestChannel channel = provider.GetRequiredService<NewSessionRequestChannel>();
    try
    {
#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
      await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

      await app.SendAsync(new TransportEnvelope("root-turn",
          RootTurnProtocol.SerializeRequest(new RootTurnRequest("turn-refused", "hello", [])),
          9198), TestContext.Current.CancellationToken).ConfigureAwait(true);
      RootRunHost? resolved = await host.Server.ResolveRootHostForTest().ConfigureAwait(true);
      Assert.True(resolved is not null, "the server resolved no root runner");
      List<RootTurnEvent> seen = [];
      _ = await ReadRootTurnEventAsync(app, "turn-refused", kind: null, sink: seen.Add).ConfigureAwait(true);
      await WaitUntilAsync(() => host.ProviderCalls > 0,
          "provider never called; events seen: " + string.Join(" | ", seen.Select(e => e.Kind + ": " + e.Payload))).ConfigureAwait(true);

      Task<Result<string>> pending = channel.RequestAsync("req-refused", "hello", null,
          TestContext.Current.CancellationToken);

      await app.SendAsync(new TransportEnvelope("session-response",
          JsonSerializer.Serialize(new SessionResponseCommand("req-refused", null, "SessionLaunchFailed",
              "the app refused the session launch.")),
          9201), TestContext.Current.CancellationToken).ConfigureAwait(true);

      Result<string> answered = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.False(answered.IsSuccess);
      Assert.Equal("SessionLaunchFailed", answered.Error.Code);
      Assert.Equal("the app refused the session launch.", answered.Error.Message);
      parked.SetResult(); // the answer arrived; let the turn finish
    }
    finally
    {
      await provider.DisposeAsync().ConfigureAwait(true);
      runner.Dispose();
    }
  }

  [Fact]
  public async Task SessionResponse_MalformedPayload_NeitherKillsTheHostNorCompletesAnything()
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

    // An undecodable session-response: a peer fault that must cost the ENVELOPE, never
    // the serve loop — the next turn still settles.
    await app.SendAsync(new TransportEnvelope("session-response", "not-json", 9202),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    await app.SendAsync(new TransportEnvelope("root-turn",
        RootTurnProtocol.SerializeRequest(new RootTurnRequest("turn-after-bad", "hello", [])),
        9203), TestContext.Current.CancellationToken).ConfigureAwait(true);

    RootTurnEvent settle = await ReadRootTurnEventAsync(app, "turn-after-bad").ConfigureAwait(true);
    Assert.Equal("settled", settle.Kind);
    Assert.Equal("the answer", settle.Payload);
  }

  /// <summary>Reads frames until the FIRST root-turn event for <paramref name="turnId"/>
  ///     of the given kind, ignoring acks, declares, and other turns' events.</summary>
  private static async Task<RootTurnEvent> ReadRootTurnEventAsync(NamedPipeChildTransport app,
      string turnId, string? kind = "settled", Action<RootTurnEvent>? sink = null)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      TransportEnvelope envelope = await app.ReceiveAsync().WaitAsync(Limit,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (envelope.Kind != "root-turn-event")
      {
        continue;
      }

      RootTurnEvent evt = RootTurnProtocol.ParseEvent(envelope.Json);
      if (evt.TurnId == turnId)
      {
        sink?.Invoke(evt);
        if (kind is null || evt.Kind == kind)
        {
          return evt;
        }
      }
    }

    Assert.Fail("no root-turn event for '" + turnId + "' arrived within the limit");
    throw new InvalidOperationException("unreachable");
  }

  /// <summary>A registry with no tools: these tests exercise the channel path, not tool dispatch.</summary>
  private sealed class EmptyToolRegistry : IToolRegistry
  {
    public ITool? Find(string name) => null;

    public IReadOnlyList<ToolDefinition> Definitions => [];
  }

  /// <summary>Connects the app side to the ALREADY-BUILT server, so the test drives the
  ///     SAME server instance the rig attached its runner to.</summary>
  private static async Task<NamedPipeChildTransport> ConnectToServerAsync(ChildHostServer server)
  {
    string pipeName = "ethang-sessresp-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
  }

  private static async Task WaitUntilAsync(Func<bool> condition, string? because = null)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within the limit" + (because is null ? string.Empty : ": " + because));
      }

      await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
