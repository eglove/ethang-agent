using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>The channel's contract (issue #234 task 3), fakes only: the live turn's
///     emitter is the ONLY path a session-request takes, so every semantic the wire
///     round trip depends on is pinned here — immediate typed refusal with no turn,
///     the serialized payload verbatim, the app's answer completing the pending task,
///     the app's typed failure propagating, disconnect failing pending requests, an
///     unknown response id ignored, and a pre-cancelled request a typed failure.</summary>
public class NewSessionRequestChannelTests
{
  private static readonly DomainError AppRefusal = new("SessionLaunchFailed", "the app refused the session launch.");

  [Fact]
  public async Task RequestAsync_WithNoConnectedTurn_FailsImmediately()
  {
    NewSessionRequestChannel channel = new();

    Result<string> answered = await channel.RequestAsync("req-1", "hello", null, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(answered.IsSuccess);
    Assert.Equal("SessionRequestUnreachable", answered.Error.Code);
  }

  [Fact]
  public async Task RequestAsync_EmitsTheSerializedPayload_AndCompletesWithTheSessionId()
  {
    NewSessionRequestChannel channel = new();
    List<string> emitted = [];
    channel.Connect(emitted.Add);

    Task<Result<string>> pending = channel.RequestAsync("req-1", "hello", "handoff text", TestContext.Current.CancellationToken);
    Assert.False(pending.IsCompleted, "the request must stay pending until the app answers");

    SessionRequestPayload? sent = JsonSerializer.Deserialize<SessionRequestPayload>(emitted.Single());
    Assert.NotNull(sent);
    Assert.Equal("req-1", sent.RequestId);
    Assert.Equal("hello", sent.Prompt);
    Assert.Equal("handoff text", sent.HandoffSummary);

    channel.Complete("req-1", Result.Success("sess-123"));

    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.True(answered.IsSuccess, answered.Error?.Message);
    Assert.Equal("sess-123", answered.Value);
  }

  [Fact]
  public async Task RequestAsync_WithNullHandoff_SerializesNullHandoffSummary()
  {
    NewSessionRequestChannel channel = new();
    List<string> emitted = [];
    channel.Connect(emitted.Add);

    Task<Result<string>> pending = channel.RequestAsync("req-2", "just a prompt", null, TestContext.Current.CancellationToken);
    channel.Complete("req-2", Result.Success("sess-9"));

    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.True(answered.IsSuccess, answered.Error?.Message);
    SessionRequestPayload? sent = JsonSerializer.Deserialize<SessionRequestPayload>(emitted.Single());
    Assert.NotNull(sent);
    Assert.Equal("req-2", sent.RequestId);
    Assert.Equal("just a prompt", sent.Prompt);
    Assert.Null(sent.HandoffSummary);
  }

  [Fact]
  public async Task Complete_WithTheAppsFailure_FailsThePendingRequestWithThatCode()
  {
    NewSessionRequestChannel channel = new();
    channel.Connect(_ => { });

    Task<Result<string>> pending = channel.RequestAsync("req-3", "hello", null, TestContext.Current.CancellationToken);
    channel.Complete("req-3", Result.Failure<string>(AppRefusal));

    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.False(answered.IsSuccess);
    Assert.Equal("SessionLaunchFailed", answered.Error.Code);
    Assert.Equal("the app refused the session launch.", answered.Error.Message);
  }

  [Fact]
  public async Task Disconnect_WithAPendingRequest_FailsItTurnEnded()
  {
    NewSessionRequestChannel channel = new();
    channel.Connect(_ => { });
    Task<Result<string>> pending = channel.RequestAsync("req-4", "hello", null, TestContext.Current.CancellationToken);

    channel.Disconnect();

    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.False(answered.IsSuccess);
    Assert.Equal("SessionRequestUnreachable", answered.Error.Code);
  }

  [Fact]
  public async Task Complete_ForAnUnknownRequestId_IsIgnored()
  {
    NewSessionRequestChannel channel = new();
    channel.Connect(_ => { });

    channel.Complete("no-such-request", Result.Success("sess-x"));
    Task<Result<string>> pending = channel.RequestAsync("req-live", "hello", null, TestContext.Current.CancellationToken);
    Assert.False(pending.IsCompleted, "the unknown id must not touch the live request");
    channel.Complete("req-live", Result.Success("sess-live"));
    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.True(answered.IsSuccess, answered.Error?.Message);
    Assert.Equal("sess-live", answered.Value);
  }

  [Fact]
  public async Task RequestAsync_WithAPreCancelledToken_FailsTyped()
  {
    NewSessionRequestChannel channel = new();
    channel.Connect(_ => { });
    using CancellationTokenSource cancelled = new();
    await cancelled.CancelAsync().ConfigureAwait(true);

    Result<string> answered = await channel.RequestAsync("req-5", "hello", null, cancelled.Token).ConfigureAwait(true);

    Assert.False(answered.IsSuccess);
    Assert.Equal("SessionRequestCancelled", answered.Error.Code);
  }

  [Fact]
  public async Task RequestAsync_CanBeCancelledWhilePending()
  {
    NewSessionRequestChannel channel = new();
    channel.Connect(_ => { });
    using CancellationTokenSource source = new();

    Task<Result<string>> pending = channel.RequestAsync("req-6", "hello", null, source.Token);
    await source.CancelAsync().ConfigureAwait(true);

    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.False(answered.IsSuccess);
    Assert.Equal("SessionRequestCancelled", answered.Error.Code);
  }

  [Fact]
  public void IsConnected_TracksTheLiveTurn()
  {
    NewSessionRequestChannel channel = new();
    Assert.False(channel.IsConnected);

    channel.Connect(_ => { });
    Assert.True(channel.IsConnected);

    channel.Disconnect();
    Assert.False(channel.IsConnected);
  }

  [Fact]
  public async Task Connect_ReplacesThePreviousTurnsEmitter()
  {
    // Turns are serialized by the turn gate, so a second Connect is the NEXT turn:
    // a pending request from the OLD turn is failed by Disconnect before the new one
    // connects, and the new turn's requests ride the new emitter.
    NewSessionRequestChannel channel = new();
    List<string> firstTurn = [];
    List<string> secondTurn = [];
    channel.Connect(firstTurn.Add);
    channel.Disconnect();
    channel.Connect(secondTurn.Add);

    Task<Result<string>> pending = channel.RequestAsync("req-7", "hello", null, TestContext.Current.CancellationToken);
    channel.Complete("req-7", Result.Success("sess-7"));

    Result<string> answered = await pending.ConfigureAwait(true);
    Assert.True(answered.IsSuccess, answered.Error?.Message);
    Assert.Empty(firstTurn);
    _ = Assert.Single(secondTurn);
  }

  [Fact]
  public async Task Disconnect_ThenComplete_IsIgnored()
  {
    // The turn is gone; a late response must not resurrect a completed failure.
    NewSessionRequestChannel channel = new();
    channel.Connect(_ => { });
    Task<Result<string>> pending = channel.RequestAsync("req-8", "hello", null, TestContext.Current.CancellationToken);
    channel.Disconnect();

    Result<string> failed = await pending.ConfigureAwait(true);
    Assert.False(failed.IsSuccess);
    channel.Complete("req-8", Result.Success("sess-late"));

    Assert.False(failed.IsSuccess);
    Assert.Equal("SessionRequestUnreachable", failed.Error.Code);
  }
}
