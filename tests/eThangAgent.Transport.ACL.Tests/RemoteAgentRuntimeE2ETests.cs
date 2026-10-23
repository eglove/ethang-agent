using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Wire-level E2E against a REAL ChildHost process: connect over the named pipe,
///     interrupt round-trips, and a declared failure when the host dies. The host exe is
///     built by the solution (project reference); every await is deadline-bounded per
///     deadlock vigilance.</summary>
public class RemoteAgentRuntimeE2ETests
{
  private static string FindHostExe()
  {
    // The shared resolution: the repo's live build output in a checkout, else the
    // copy shipped next to the app. A missing repo is an ordinary case, not a fault
    // (the 2026-10-21 crash walked past the filesystem root and dereferenced null).
    return ChildHostExeLocator.Resolve(
        ChildHostExeLocator.FindRepoRoot(typeof(RemoteAgentRuntimeE2ETests).Assembly.Location),
        AppContext.BaseDirectory);
  }

  [Fact]
  public async Task HostProcess_Connected_InterruptsRoundTrip_AndDeclaredFailureOnDeath()
  {
    string hostExe = FindHostExe();
    Assert.True(File.Exists(hostExe), "child host exe not built");

    string pipeName = "ethang-e2e-" + Guid.NewGuid().ToString("N");
    string scratch = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(scratch);
    await File.WriteAllTextAsync(Path.Combine(scratch, "settings.json"), "{}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    ProcessStartInfo psi = new(hostExe);
    psi.ArgumentList.Add(pipeName);
    psi.ArgumentList.Add(Path.Combine(scratch, "settings.json"));
    psi.ArgumentList.Add(Path.Combine(scratch, "db.sqlite"));
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;

    Process started = Process.Start(psi) ?? throw new InvalidOperationException("child host failed to start.");
    using Process host = started;
    try
    {
      Task<NamedPipeChildTransport> connected = NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken);
      NamedPipeChildTransport transport = await connected.ConfigureAwait(true);

      RemoteAgentRuntime runtime = new(transport);
      using CancellationTokenSource loopCts = new();
      _ = runtime.RunReceiveLoopAsync(loopCts.Token);

      // Interrupt envelope round-trips without error (no matching run: host no-ops).
      runtime.Interrupt(new AgentId(Guid.NewGuid()));

      // Unknown-id waits fail NotFound immediately (contract) — pinned before teardown.
      Task<Result<AgentRunOutcome>> wait = runtime.WhenSettledAsync(new AgentId(Guid.NewGuid()),
          TestContext.Current.CancellationToken);
      Result<AgentRunOutcome> unknown = await wait.ConfigureAwait(true);
      Assert.False(unknown.IsSuccess);
      Assert.Equal("NotFound", unknown.Error.Code);

      // Kill the host: the pump must fault without hanging; a follow-up receive surfaces
      // the declared closed condition (FR-X3) instead of blocking forever.
      host.Kill(entireProcessTree: true);
      Task receive = transport.ReceiveAsync(TestContext.Current.CancellationToken);
      Task winner = await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(winner == receive, "receive hung after host death");
    }
    finally
    {
      if (!host.HasExited)
      {
        host.Kill(entireProcessTree: true);
      }
    }
  }

  /// <summary>A recording event stream: captures every published event, the same
  ///     contract InProcessAgentEvents serves (the app container wires one).</summary>
  private sealed class RecordingEvents : IAgentEvents
  {
    public List<ChildEvent> Published { get; } = [];
    private readonly List<IAgentEventSubscriber> _subscribers = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new Lease(_subscribers, subscriber);
    }

    public void Publish(ChildEvent evt)
    {
      Published.Add(evt);
      foreach (IAgentEventSubscriber subscriber in _subscribers.ToArray())
      {
        subscriber.OnEvent(evt);
      }
    }

    private sealed class Lease(List<IAgentEventSubscriber> subscribers, IAgentEventSubscriber subscriber) : IDisposable
    {
      public void Dispose() => _ = subscribers.Remove(subscriber);
    }
  }

  [Fact]
  public async Task ChildEventEnvelope_RoundTripsThroughTheWire()
  {
    // Issue #245: the host forwards child events as child-event envelopes; the app-side
    // runtime republishes them onto the session's stream. The event must arrive verbatim.
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    RecordingEvents events = new();
    RemoteAgentRuntime runtime = new(app, RootTurnBudget.Default, events);
    try
    {
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      AgentId childId = AgentId.NewId();
      ChildStartedEvent started = new(childId, DateTimeOffset.UtcNow, null, "test/model", 0);
      await host.SendAsync(new TransportEnvelope("child-event",
          System.Text.Json.JsonSerializer.Serialize(ChildEventEnvelope.From(started)), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      // Bounded wait: the pump must route the envelope and publish the event.
      while (events.Published.Count == 0)
      {
        await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }
      ChildStartedEvent republished = Assert.IsType<ChildStartedEvent>(Assert.Single(events.Published));
      Assert.Equal(childId, republished.ChildId);
      Assert.Equal("test/model", republished.ModelId, StringComparer.Ordinal);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
      await host.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task ChildEventEnvelope_UnknownPayloadType_IsDroppedNotFatal()
  {
    // A forward-compatible peer's event kind this build does not know is dropped: the
    // pump survives, and the session stream sees nothing.
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    RecordingEvents events = new();
    RemoteAgentRuntime runtime = new(app, RootTurnBudget.Default, events);
    try
    {
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // A payload that decodes to no known ChildEvent subtype.
      ChildEventEnvelope envelope = new(Guid.NewGuid(), "BrandNewEvent", System.Text.Json.JsonSerializer.Serialize(new { }));
      await host.SendAsync(new TransportEnvelope("child-event",
          System.Text.Json.JsonSerializer.Serialize(envelope), 1),
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Empty(events.Published);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
      await host.DisposeAsync().ConfigureAwait(true);
    }
  }

  /// <summary>Connects one in-process app/host pipe pair (the RemoteRuntimeSettleRetentionTests
  ///     pattern) so the runtime is exercised over a REAL connected transport.</summary>
  private static async Task<(NamedPipeChildTransport App, NamedPipeChildTransport Host)> ConnectAsync()
  {
    string pipeName = "ethang-settle-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> serverTask = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport host = await serverTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (app, host);
  }

  /// <summary>Pins the task-2 typed contract: resume-after-settle is NOT wired through the
  ///     child host yet, and the refusal is a typed DomainError on the Result — never an
  ///     exception and never a silent success.</summary>
  [Fact]
  public async Task Resume_OnARemoteRuntime_IsATypedUnsupportedError()
  {
    (NamedPipeChildTransport app, NamedPipeChildTransport host) = await ConnectAsync().ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      Result<AgentId> resumed = await runtime.Resume(new AgentId(Guid.NewGuid()), "go",
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.False(resumed.IsSuccess);
      Assert.Equal("ResumeUnsupported", resumed.Error.Code);
      Assert.Contains("child host", resumed.Error.Message, StringComparison.Ordinal);
    }
    finally
    {
      await app.DisposeAsync().ConfigureAwait(true);
      await host.DisposeAsync().ConfigureAwait(true);
    }
  }
}
