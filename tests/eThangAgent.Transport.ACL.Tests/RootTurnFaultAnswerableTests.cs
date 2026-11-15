using eThangAgent.ChildHost;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>One frame larger than the seam admits is ANSWERED rather than allowed to fault the
///     turn that produced it. The failure the app reads is a typed refusal naming the frame and
///     the limit, the connection survives it, and a genuine host fault still reports
///     RootTurnFault.</summary>
/// <para>The emitter side of this is proved where the seam actually sits: a nested root-turn
///     event is written by the host's own send path, so the 16 MiB body below is handed to the
///     runner and the SERVER is what refuses and answers it.</para>
[Trait("Category", "LatencyContract")]
[Trait("Category", "Process")]
public class RootTurnFaultAnswerableTests
{

  private static readonly TimeSpan Limit = TestCeiling.Value;

  /// <summary>The oversize payload the observed case carried: 48,301,402 bytes, scaled down to
  ///     the smallest body that still cannot cross a 4 MiB seam so the test does not spend a
  ///     large allocation to prove one constant.</summary>

  private const int OversizeBodyBytes = 5 * 1024 * 1024;


  /// <summary>A frame the seam cannot carry is refused with a type that means exactly that: one
  ///     unsendable payload. It is NOT a closed connection, which is what every
  ///     <c>catch (TransportClosedException)</c> site means, so it must not be one.</summary>

  [Fact]
  public async Task FrameBeyondTheSeam_ThrowsItsOwnType_CarryingTheFrameAndTheLimit()
  {
    MemoryStream wire = new();
    TransportEnvelope oversize = new("root-turn-event", new string('x', TransportFraming.MaxFrameBytes), 1);

    TransportFrameTooLargeException refused = await Assert.ThrowsAsync<TransportFrameTooLargeException>(
        () => TransportFraming.WriteAsync(wire, oversize, TestContext.Current.CancellationToken))
        .ConfigureAwait(true);

    Assert.IsNotType<TransportClosedException>(refused);
    Assert.True(refused.FrameBytes > TransportFraming.MaxFrameBytes, "FRAME: " + refused.FrameBytes);
    Assert.Equal(TransportFraming.MaxFrameBytes, refused.LimitBytes);
    Assert.Equal(0, wire.Length);
  }

  /// <summary>A refused frame leaves nothing on the wire, so the same stream carries the next
  ///     frame intact: the connection is what every later turn needs, and it was never at risk.</summary>

  [Fact]
  public async Task FrameBeyondTheSeam_LeavesTheConnectionIntact()
  {
    MemoryStream wire = new();
    TransportEnvelope oversize = new("root-turn-event", new string('x', TransportFraming.MaxFrameBytes), 1);
    _ = await Assert.ThrowsAsync<TransportFrameTooLargeException>(
        () => TransportFraming.WriteAsync(wire, oversize, TestContext.Current.CancellationToken))
        .ConfigureAwait(true);

    await TransportFraming.WriteAsync(wire, new TransportEnvelope("settled", "{}", 2),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = wire.Seek(0, SeekOrigin.Begin);
    TransportEnvelope read = await TransportFraming.ReadAsync(wire, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.Equal("settled", read.Kind);
  }

  /// <summary>WriteAsync refuses the frame; the host's send path decides BEFORE that point, so
  ///     the measurement is what it relies on.</summary>

  [Fact]
  public void MeasureBytes_ReportsTheFrameSizeWriteWouldRefuse()
  {
    TransportEnvelope oversize = new("root-turn-event", new string('x', TransportFraming.MaxFrameBytes), 1);

    Assert.True(TransportFraming.MeasureBytes(oversize) > TransportFraming.MaxFrameBytes);
    Assert.True(TransportFraming.MeasureBytes(new TransportEnvelope("settled", "{}", 1))
        <= TransportFraming.MaxFrameBytes);
  }

  /// <summary>AN OVERSIZE EMITTED MID-TURN IS ANSWERED, NOT FAULTED. The model streamed one
  ///     delta larger than the seam admits, so that event cannot travel; the turn still runs to
  ///     its end and the outcome the app reads names the payload and the limit instead of
  ///     RootTurnFault. This is the last-resort case: the display budget caps what a tool
  ///     result may carry, and a single provider delta is the one size nothing else bounds.</summary>
  [Fact]
  public async Task RunTurnAsync_OversizeDeltaEvent_SettlesNamingThePayloadAndLimit()
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer",
        [new string('x', OversizeBodyBytes), " and done"]);
    List<RootTurnEvent> events = [];
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectAsync(host).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-oversize", "read the big file", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Contains("RootTurnPayloadTooLarge", result.Value, StringComparison.Ordinal);
    Assert.Contains("frame this turn produced exceeds the "
        + TransportFraming.MaxFrameBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "-byte seam limit", result.Value, StringComparison.Ordinal);
    Assert.DoesNotContain("RootTurnFault", result.Value, StringComparison.Ordinal);

    // THE HOST STAYS ALIVE: the turn gate is free, the settle loop is still serving, and the
    // next turn in the SAME session completes.
    host.Provider.Answer = "the next answer";
    host.Provider.Deltas = ["the next answer"];
    List<RootTurnEvent> following = [];
    Result<string> second = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-after", "hello", []), following.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal("the next answer", second.Value);
  }

  /// <summary>A genuine fault is still a fault: the host's isolation catch keeps its
  ///     RootTurnFault rendering.</summary>

  [Fact]
  public async Task RunTurnAsync_ToolThrows_StillSettlesRootTurnFault()
  {
    ScriptedOptions knobs = new(
        ToolCalls: [new ToolCallRequest("call-1", "probe", "{}")],
        ToolFaults: true);
    using ScriptedRootHost host = ScriptedRootHost.Start("unused", ["unused"], options: knobs);
    List<RootTurnEvent> events = [];
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectAsync(host).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-fault", "fault please", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Contains("RootTurnFault", result.Value, StringComparison.Ordinal);
    Assert.Contains("the probe tool faulted.", result.Value, StringComparison.Ordinal);
  }

  /// <summary>The production case: a refusal that fits inside the seam travels normally, and no
  ///     RootTurnPayloadTooLarge line appears anywhere in the turn.</summary>

  [Fact]
  public async Task RunTurnAsync_ResultWithinTheSeam_EmitsNoPayloadRefusal()
  {
    ScriptedOptions knobs = new(
        ToolCalls: [new ToolCallRequest("call-1", "probe", "{}")],
        ToolResultContent: "the refusal the model reads");
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"], options: knobs);
    List<RootTurnEvent> events = [];
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectAsync(host).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-small", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("the answer", result.Value);
    Assert.DoesNotContain(events, e => e.Payload.Contains("RootTurnPayloadTooLarge", StringComparison.Ordinal));
  }

  /// <summary>AN UNSENDABLE REQUEST IS THE REQUEST'S FAILURE, NOT THE HOST'S. The app's runtime
  ///     reports the payload's own code, naming the size and the limit, so a caller - the
  ///     reconnect loop included - can tell a request that cannot travel from a host that is
  ///     gone.</summary>
  [Fact]
  public async Task SendRootTurnAsync_RequestBeyondTheSeam_FailsPayloadTooLarge()
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await NamedPipeChildTransport
        .ConnectToHostAsync(host.PipeName, TestContext.Current.CancellationToken)
        .WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    RootTurnRequest oversize = new("turn-big", new string('x', OversizeBodyBytes), []);

    Result<string> refused = await runtime.SendRootTurnAsync(oversize, _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(refused.IsSuccess);
    Assert.Equal(RemoteAgentRuntime.PayloadTooLargeCode, refused.Error.Code);
    Assert.Contains("frame of ", refused.Error.Message, StringComparison.Ordinal);
    Assert.Contains(TransportFraming.MaxFrameBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        refused.Error.Message, StringComparison.Ordinal);
    // NOT HostUnavailable: the reconnect decision reads this code, and a dead host reads the
    // other one.
    Assert.NotEqual("HostUnavailable", refused.Error.Code);

    // NOTHING WAS WRITTEN: the host received no turn at all, because the frame was refused
    // before the write.
    Assert.Empty(host.Received);
  }

  /// <summary>A dead connection keeps reading HostUnavailable: the two failures stay
  ///     distinguishable, which is what a reconnect decision needs.</summary>
  [Fact]
  public async Task SendRootTurnAsync_NoConnection_StillFailsHostUnavailable()
  {
    RemoteAgentRuntime runtime = new();

    Result<string> refused = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(refused.IsSuccess);
    Assert.Equal("HostUnavailable", refused.Error.Code);
  }

  /// <summary>Drives one root turn against the REAL <see cref="ChildHostServer"/> over a real pipe,
  ///     with the rig's runner attached through the server's own seam.</summary>

  private static async Task<NamedPipeChildTransport> ConnectAsync(ScriptedRootHost host)
  {
    ChildHostServer server = host.Server;
    string pipeName = "ethang-payloadtoo-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept
        .WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
  }
}
