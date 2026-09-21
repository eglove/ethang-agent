using System.Text.Json;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Real-pipe fake-broker tests (the brief's unit contract): framing,
///     handshake ok, auth failure, reply-id matching incl. genuinely concurrent requests,
///     oversize rejection, dropped-pipe faults, UTF-8 round trip, and error mapping.
///     The fake broker is a real NamedPipeServerStream started in-test.</summary>
public class NdjsonPipeClientTests
{
  [Fact]
  public async Task HandshakeOk_ThenRequest_ReceivesMatchedReply()
  {
    FakeBroker broker = FakeBroker.Start("ok");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      BrokerReply reply = await client.RequestAsync("list_applications", JsonDocument.Parse("{}").RootElement, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(2, reply.Id);
      Assert.True(reply.Result is not null, "expected a result payload");
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task AuthFailure_Throws_AndBrokerSawIdZero()
  {
    FakeBroker broker = FakeBroker.Start("auth");
    try
    {
      Task<NdjsonPipeClient> connect = broker.ConnectClientAsync();
      BrokerAuthException authError = await Assert.ThrowsAsync<BrokerAuthException>(() => connect).ConfigureAwait(true);
      Assert.NotNull(authError);
      Assert.Contains("token", broker.ReceivedAuth, StringComparison.Ordinal);
      Assert.Equal(0, broker.ReceivedAuthId);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task VersionMismatch_ThrowsVersionException()
  {
    FakeBroker broker = FakeBroker.Start("version");
    try
    {
      Task<NdjsonPipeClient> connect = broker.ConnectClientAsync();
      BrokerVersionException versionError = await Assert.ThrowsAsync<BrokerVersionException>(() => connect).ConfigureAwait(true);
      Assert.NotNull(versionError);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task ConcurrentRequests_IdsMatchReplies()
  {
    FakeBroker broker = FakeBroker.Start("echo");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
      Task<BrokerReply> first = Task.Run(async () =>
      {
        await gate.Task.ConfigureAwait(true);
        return await client.RequestAsync("m1", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      });
      Task<BrokerReply> second = Task.Run(async () =>
      {
        await gate.Task.ConfigureAwait(true);
        return await client.RequestAsync("m2", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      });
      gate.SetResult();
      BrokerReply[] replies = await Task.WhenAll(first, second).ConfigureAwait(true);
      Assert.Equal(2, Math.Min(replies[0].Id, replies[1].Id));
      Assert.Equal(3, Math.Max(replies[0].Id, replies[1].Id));
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task OversizeFrame_IsRejected_BrokerProtocolException_AndPendingFaulted()
  {
    FakeBroker broker = FakeBroker.Start("oversize");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      BrokerProtocolException big = await Assert.ThrowsAsync<BrokerProtocolException>(
          () => client.RequestAsync("big", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Contains("ceiling", big.Message, StringComparison.Ordinal);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task ClosedPipe_FaultsPendingRequest_WithConnectionClosed()
  {
    FakeBroker broker = FakeBroker.Start("drop");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      // The drop lands either at write (IOException - pipe is gone) or at the faulted pending
      // request (BrokerConnectionClosedException from the reader loop). Both are honest
      // connection-closed consequences; a silent success is not acceptable.
      Exception gone = await Assert.ThrowsAnyAsync<Exception>(
          () => client.RequestAsync("gone", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(gone is IOException or BrokerConnectionClosedException or BrokerProtocolException,
          "expected a transport-level failure, got: " + gone.GetType().Name);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task HelloMissingProtocolKey_FailsClosed_VersionMismatch()
  {
    FakeBroker broker = FakeBroker.Start("no-protocol");
    try
    {
      Task<NdjsonPipeClient> connect = broker.ConnectClientAsync();
      BrokerVersionException error = await Assert.ThrowsAsync<BrokerVersionException>(() => connect).ConfigureAwait(true);
      Assert.NotNull(error);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task HelloMissingPlatformKey_FailsClosed_VersionMismatch()
  {
    FakeBroker broker = FakeBroker.Start("no-platform");
    try
    {
      Task<NdjsonPipeClient> connect = broker.ConnectClientAsync();
      BrokerVersionException error = await Assert.ThrowsAsync<BrokerVersionException>(() => connect).ConfigureAwait(true);
      Assert.NotNull(error);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Unicode_MethodAndPayload_SurviveRoundTrip()
  {
    FakeBroker broker = FakeBroker.Start("echo");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      const string unicode = "\u7A97\u53E3 \u6807\u9898 \U0001F600 endv\u00E4";
      var payload = new { title = unicode, note = "\u00E4\u00F6\u00FC \ud83d\ude00" };
      string raw = JsonSerializer.Serialize(payload, RelaxedOptions.Instance);
      BrokerReply reply = await client.RequestAsync(unicode, JsonDocument.Parse(raw).RootElement, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(2, reply.Id);
      bool hasResult = reply.Result is not null;
      Assert.True(hasResult, "expected a result payload");
      JsonElement resultEl = reply.Result ?? throw new InvalidOperationException("no result");
      string echoed = resultEl.GetProperty("echoed_params").GetRawText();
      Assert.Equal(raw, echoed);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public void ErrorReply_MapsThroughToolDomainCodes()
  {
    BrokerReply reply = BrokerReply.Parse("{" + "\"id\":7,\"error\":{\"code\":\"stale_state\",\"message\":\"gone\"}}")
      ?? throw new InvalidOperationException("parse failed");
    Assert.NotNull(reply.Error);
    ComputerOutcome.Failure failure = BrokerErrorMapper.Map(reply.Error.Code, reply.Error.Message);
    Assert.Equal(ComputerErrorCodes.StaleState, failure.Code);
    Assert.Equal("gone", failure.Message);
  }
}
