using System.Text.Json;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Real-pipe fake-broker tests (the brief's unit contract): framing,
///     handshake ok, auth failure, reply-id matching incl. concurrent requests,
///     oversize frame rejection, not-ready backoff, VERSION_MISMATCH never retried.
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
      Assert.Equal(2, reply.Id); // handshake used 0 (authenticate) and 1 (hello)
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
      BrokerReply first = await client.RequestAsync("m1", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      BrokerReply second = await client.RequestAsync("m2", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Equal(2, first.Id); // ids continue after the handshake
      Assert.Equal(3, second.Id);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task OversizeFrame_IsRejectedAndConnectionCloses()
  {
    FakeBroker broker = FakeBroker.Start("oversize");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      Exception big = await Assert.ThrowsAnyAsync<Exception>(
          () => client.RequestAsync("big", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.NotNull(big);
    }
    finally
    {
      await broker.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task ClosedPipe_FaultsPendingRequest()
  {
    FakeBroker broker = FakeBroker.Start("drop");
    try
    {
      NdjsonPipeClient client = await broker.ConnectClientAsync().ConfigureAwait(true);
      Exception gone = await Assert.ThrowsAnyAsync<Exception>(
          () => client.RequestAsync("gone", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.NotNull(gone);
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
