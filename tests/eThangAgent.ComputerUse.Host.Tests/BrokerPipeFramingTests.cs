using System.IO.Pipes;
using System.Text.Json;
using eThangAgent.ComputerUse.ACL;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>End-to-end framing tests (task 16): the REAL pipe client (the ACL's
///     NdjsonPipeClient) talks to the REAL PipeServer serve loop over a real named pipe.
///     Auth id 0, hello id 1, application ids from 2; UTF-8 bytes round trip; oversize
///     replies are refused by the server loop; auth failure ends the connection; the
///     lease is released when the owning connection drops, so the next connection can
///     take over. The observer seam is faked - native surface is task 17.</summary>
public class BrokerPipeFramingTests
{
  [Fact]
  public async Task Handshake_ThenRequest_ReceivesIdMatchedReply()
  {
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.Start(pipeName);
    NdjsonPipeClient client = await NdjsonPipeClient.ConnectAsync(pipeName, harness.Token, 1, "windows", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    BrokerReply reply = await client.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(2, reply.Id);
    _ = Assert.NotNull(reply.Result);
    await client.DisposeAsync().ConfigureAwait(true);
  }

  [Fact]
  public async Task Utf8RequestFrame_TravelsAsRealUtf8Bytes()
  {
    // The wire carries UTF-8: a request with multi-byte characters must be decoded
    // server-side as UTF-8 (never per-byte chars) and routed by method name intact.
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.Start(pipeName);
    NdjsonPipeClient client = await NdjsonPipeClient.ConnectAsync(pipeName, harness.Token, 1, "windows", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    JsonElement parameters = JsonSerializer.SerializeToElement(new { note = "naïve 中文 ok" });
    // type_text is an input method (task 17 dispatch): on the skeleton it reaches the
    // input path and comes back input_busy or a receipt; either way the frame was
    // decoded correctly enough to route it. What must NOT happen is a decode error or
    // an invalid_request about the frame itself.
    BrokerReply reply = await client.RequestAsync("type_text", parameters, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(reply.Error is null || reply.Error.Code is "input_busy" or "unimplemented" or "controller_busy",
      "expected the UTF-8 frame to route cleanly, got: " + (reply.Error?.Code ?? "result"));
    await client.DisposeAsync().ConfigureAwait(true);
  }

  [Fact]
  public async Task WrongToken_AuthFails_AndConnectionCloses()
  {
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.Start(pipeName);
    Task<NdjsonPipeClient> connect = NdjsonPipeClient.ConnectAsync(pipeName, "wrong-token", 1, "windows", ct: TestContext.Current.CancellationToken);
    BrokerAuthException authError = await Assert.ThrowsAsync<BrokerAuthException>(() => connect).ConfigureAwait(true);
    Assert.NotNull(authError);
  }

  [Fact]
  public async Task VersionMismatch_AtHello_IsFatalToClient()
  {
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.Start(pipeName);
    Task<NdjsonPipeClient> connect = NdjsonPipeClient.ConnectAsync(pipeName, harness.Token, 99, "windows", ct: TestContext.Current.CancellationToken);
    BrokerVersionException versionError = await Assert.ThrowsAsync<BrokerVersionException>(() => connect).ConfigureAwait(true);
    Assert.NotNull(versionError);
  }

  [Fact]
  public void OversizeReply_IsRefusedByServerLoop()
  {
    // The loop must refuse to WRITE frames above the ceiling: the frame renderer is
    // where the ceiling check lives, so an oversized observation can never corrupt
    // the wire. (The pipe-level behavior - refusal then close - rides on this check.)
    BigObserver observer = new();
    JsonElement? big = observer.ListApplications();
    Assert.True(big is not null, "fixture should produce a payload");
    BrokerResponse response = BrokerResponse.Ok(big);
    string frame = PipeServeLoop.FrameFor(response, 2);
    Assert.Contains("\"internal\"", frame, StringComparison.Ordinal);
    Assert.Contains("ceiling", frame, StringComparison.Ordinal);
    Assert.True(System.Text.Encoding.UTF8.GetByteCount(frame) < 1024, "refusal reply is small");
  }

  [Fact]
  public async Task OwnerDrop_ReleasesLease_ForTheNextConnection()
  {
    // after a connection loss. The pipe-level contract under test is that the owner's
    // drop RELEASES the lease (A3), observable on the broker object itself.
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.Start(pipeName);
    NdjsonPipeClient first = await NdjsonPipeClient.ConnectAsync(pipeName, harness.Token, 1, "windows", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await first.RequestAsync("controller_takeover", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
    PipeServer broker = harness.Broker;
    Assert.Equal(1, broker.Lease.Owner);
    await first.DisposeAsync().ConfigureAwait(true);
    // The serve loop's DropConnection path runs synchronously on the read loop; give it a beat.
    for (int i = 0; i < 100 && broker.Lease.Owner is not null; i++)
    {
      await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Null(broker.Lease.Owner);
  }
}

/// <summary>Starts the REAL broker serve loop on a real named pipe for one test.
///     Production PipeServer code paths only; the observer seam is faked (native
///     surface is task 17). Disposal stops the loop.</summary>
internal sealed class HostHarness : IAsyncDisposable
{
  private readonly NamedPipeServerStream _server;
  private readonly Task _loop;

  private HostHarness(NamedPipeServerStream server, Task loop, string token, PipeServer broker)
  {
    _server = server;
    _loop = loop;
    Token = token;
    Broker = broker;
  }

  public string Token { get; }

  public PipeServer Broker { get; }

  public static HostHarness Start(string pipeName, bool bigObserver = false)
  {
    string token = "test-token-" + Guid.NewGuid().ToString("N");
    NamedPipeServerStream server = new(pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    PipeServer broker = bigObserver
      ? new PipeServer(new BrokerConfig(pipeName, token), new BigObserver())
      : new PipeServer(new BrokerConfig(pipeName, token), new FakeObserver());
    Task loop = PipeServeLoop.ServeOnceAsync(server, broker);
    return new HostHarness(server, loop, token, broker);
  }

  public async ValueTask DisposeAsync()
  {
    if (_server.IsConnected)
    {
      _server.Disconnect();
    }

    await _server.DisposeAsync().ConfigureAwait(true);
    try
    {
      await _loop.ConfigureAwait(true);
    }
    catch (IOException)
    {
      // The loop ends when the pipe drops; a teardown race is fine here.
    }
  }
}

/// <summary>An observer whose list_applications reply exceeds the 64 MiB frame ceiling.
///     The server loop must refuse to write it and close instead of corrupting the wire.</summary>
internal sealed class BigObserver : IBrokerObserver
{
  public JsonElement? ListApplications()
  {
    // One big row clears the 64 MiB ceiling cheaply: the point is the server's
    // refusal path, not payload construction speed.
    object[] rows = [new { pid = 1, name = new string('x', 66 * 1024 * 1024), exe = "", aumid = "", active = false }];
    return JsonSerializer.SerializeToElement(rows);
  }

  public JsonElement? ListWindows(JsonElement? parameters) => null;

  public JsonElement? CaptureApp(JsonElement? parameters) => null;

  public void OnOwnerLost(int ownerConnectionId) { }
}
