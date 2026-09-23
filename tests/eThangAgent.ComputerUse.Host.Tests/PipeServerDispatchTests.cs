using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>PipeServer dispatch (task 16, fix round M8/M11): request routing, the strict
///     handshake (authenticate on id 0, hello on id 1), controller-lease integration, input
///     serialization, honest non-owner stop refusal, unknown methods, and raw-frame
///     strictness. Connection ids are explicit - the serve loop assigns one per connection.</summary>
public class PipeServerDispatchTests
{
  [Fact]
  public void UnknownMethod_FromAuthenticatedConnection_ReturnsMethodNotFound()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(5, "no_such_method", null, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("method_not_found", reply.Error.Value.Code);
    Assert.Contains("no_such_method", reply.Error.Value.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Authenticate_WithCorrectToken_ReturnsOk()
  {
    PipeServer server = FakeConnectionFactory.Token("t");
    BrokerResponse reply = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Result);
    Assert.True(reply.Result.Value.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True);
  }

  [Fact]
  public void Authenticate_WithWrongToken_ReturnsNotAuthorized()
  {
    PipeServer server = FakeConnectionFactory.Token("secret");
    BrokerResponse reply = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"wrong"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("not_authorized", reply.Error.Value.Code);
  }

  [Fact]
  public void Authenticate_MissingTokenParam_IsInvalidRequest()
  {
    PipeServer server = FakeConnectionFactory.Token("t");
    BrokerResponse reply = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"other":1}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
  }

  [Fact]
  public void Authenticate_NotOnFixedIdZero_IsInvalidRequest()
  {
    PipeServer server = FakeConnectionFactory.Token("t");
    BrokerResponse reply = server.Dispatch(7, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
  }

  [Fact]
  public void Hello_NotOnIdOne_IsInvalidRequest()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(9, "hello", JsonDocument.Parse("""{"protocolVersion":1,"platform":"windows"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
  }

  [Fact]
  public void Hello_UnknownPlatform_IsVersionMismatch()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(1, "hello", JsonDocument.Parse("""{"protocolVersion":1,"platform":"linux"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("version_mismatch", reply.Error.Value.Code);
  }

  [Fact]
  public void Hello_WrongProtocol_IsVersionMismatch()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(1, "hello", JsonDocument.Parse("""{"protocolVersion":2,"platform":"windows"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("version_mismatch", reply.Error.Value.Code);
  }

  [Fact]
  public void Hello_BeforeAuthenticate_IsNotAuthorized()
  {
    PipeServer newServer = FakeConnectionFactory.Token("t");
    BrokerResponse reply = newServer.Dispatch(1, "hello", JsonDocument.Parse("""{"protocolVersion":1,"platform":"windows"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("not_authorized", reply.Error.Value.Code);
  }

  [Fact]
  public void AnyMethod_BeforeAuthenticate_IsNotAuthorized()
  {
    PipeServer newServer = FakeConnectionFactory.Token("t");
    BrokerResponse reply = newServer.Dispatch(3, "controller_status", null, connectionId: 3);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("not_authorized", reply.Error.Value.Code);
  }

  [Fact]
  public void ListApplications_ReturnsApplicationsArray()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(2, "list_applications", null, connectionId: 1);
    _ = Assert.NotNull(reply.Result);
    Assert.True(reply.Result.Value.ValueKind == JsonValueKind.Array, "expected an applications array");
  }

  [Fact]
  public void ControllerStatus_BeforeTakeover_ReportsIdle()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(2, "controller_status", null, connectionId: 1);
    _ = Assert.NotNull(reply.Result);
    Assert.True(reply.Result.Value.TryGetProperty("owned", out JsonElement owned) && owned.ValueKind == JsonValueKind.False);
  }

  [Fact]
  public void ControllerStatus_AfterTakeover_ReportsOwner()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "controller_status", null, connectionId: 1);
    _ = Assert.NotNull(reply.Result);
    Assert.True(reply.Result.Value.TryGetProperty("owned", out JsonElement owned) && owned.ValueKind == JsonValueKind.True);
  }

  [Fact]
  public void ControllerTakeover_GrantsLease_SecondConnectionIsBusyWithOwnerDetails()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse busy = server.Dispatch(3, "controller_takeover", null, connectionId: 2);
    _ = Assert.NotNull(busy.Error);
    Assert.Equal("controller_busy", busy.Error.Value.Code);
    // M7: owner travels in details as owner=<id> AND in the message text.
    Assert.Equal("owner=1", busy.Error.Value.Details);
    Assert.Contains("owner=1", busy.Error.Value.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ControllerStop_ReleasesLease_NextTakeoverSucceeds()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse released = server.Dispatch(3, "controller_stop", null, connectionId: 1);
    _ = Assert.NotNull(released.Result);
    BrokerResponse again = server.Dispatch(4, "controller_takeover", null, connectionId: 2);
    _ = Assert.NotNull(again.Result);
  }

  [Fact]
  public void ControllerStop_ByNonOwner_IsControllerBusy()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse stop = server.Dispatch(3, "controller_stop", null, connectionId: 2);
    _ = Assert.NotNull(stop.Error);
    Assert.Equal("controller_busy", stop.Error.Value.Code);
  }

  [Fact]
  public void StopComputerControl_ByNonOwner_IsHonestRefusal()
  {
    // M11: a stop from a non-owner is invalid_request naming the lease state with
    // action_sent=false, not controller_busy.
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse stop = server.Dispatch(3, "stop_computer_control", null, connectionId: 2);
    _ = Assert.NotNull(stop.Error);
    Assert.Equal("invalid_request", stop.Error.Value.Code);
    Assert.Equal("action_sent=false", stop.Error.Value.Details);
  }

  [Fact]
  public void StopComputerControl_ReleasesLeaseAndFreesInput()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse stop = server.Dispatch(3, "stop_computer_control", null, connectionId: 1);
    _ = Assert.NotNull(stop.Result);
    BrokerResponse again = server.Dispatch(4, "controller_takeover", null, connectionId: 2);
    _ = Assert.NotNull(again.Result);
    using InputOperation? op = server.InputSerializer.TryBegin("click");
    Assert.NotNull(op);
    Assert.Equal("click", server.InputSerializer.CurrentMethod);
  }

  [Fact]
  public void CaptureApp_OnSkeletonObserver_IsUnimplemented()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse reply = server.Dispatch(2, "capture_app", JsonDocument.Parse("""{"app_ref":{"pid":1}}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("unimplemented", reply.Error.Value.Code);
  }

  [Fact]
  public void Observer_IsInjectedSeam()
  {
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), new FakeObserver());
    Assert.NotNull(server.Observer);
  }

  [Fact]
  public void Hello_OkCarriesProtocolAndPlatform()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    BrokerResponse hello = server.Dispatch(1, "hello", JsonDocument.Parse("""{"protocolVersion":1,"platform":"windows"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(hello.Result);
    Assert.True(hello.Result.Value.TryGetProperty("protocol", out _) && hello.Result.Value.TryGetProperty("platform", out _));
  }

  [Fact]
  public void InputMethod_RequiresControllerLease()
  {
    PipeServer server = FakeConnectionFactory.Authorized(4);
    BrokerResponse reply = server.Dispatch(2, "press_key", JsonDocument.Parse("""{"key":"a"}""").RootElement, connectionId: 4);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("controller_busy", reply.Error.Value.Code);
  }

  [Fact]
  public void InputMethod_WithLeaseButBusySerializer_IsInputBusy_NothingDispatched()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    _ = server.InputSerializer.TryBegin("click");
    BrokerResponse busy = server.Dispatch(3, "press_key", JsonDocument.Parse("""{"key":"a"}""").RootElement, connectionId: 1);
    _ = Assert.NotNull(busy.Error);
    Assert.Equal("input_busy", busy.Error.Value.Code);
    Assert.Contains("action_sent=false", busy.Error.Value.Details ?? "", StringComparison.Ordinal);
  }

  [Fact]
  public void DropConnection_ReleasesLease_WhenOwnerDrops()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    server.DropConnection(1);
    BrokerResponse again = server.Dispatch(3, "controller_takeover", null, connectionId: 2);
    _ = Assert.NotNull(again.Result);
  }

  [Fact]
  public void DropConnection_NotifiesObserverWithOwnerLost()
  {
    FakeObserver observer = new();
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), observer);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    server.DropConnection(1);
    Assert.Equal([1], observer.LostOwners);
  }

  [Fact]
  public void FrameWithNonIntegerId_IsInvalidRequest()
  {
    PipeServer server = FakeConnectionFactory.Token("t");
    string frame = JsonSerializer.Serialize(new { id = "x", method = "controller_status" });
    BrokerResponse reply = server.DispatchRaw(frame, connectionId: 9).Response;
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
  }

  [Fact]
  public void FrameMissingMethod_IsInvalidRequest()
  {
    PipeServer server = FakeConnectionFactory.Token("t");
    string frame = JsonSerializer.Serialize(new { id = 3 });
    BrokerResponse reply = server.DispatchRaw(frame, connectionId: 9).Response;
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("invalid_request", reply.Error.Value.Code);
  }
};

/// <summary>Canned-config PipeServer factory: Token builds an unauthenticated server;
///     Authorized builds one with each given connection id already authenticated;
///     WithForeground/WithAlwaysSucceedingInput inject dispatch hooks for gate/A3 tests.</summary>
internal static class FakeConnectionFactory
{
  public static PipeServer Token(string token) => new(new BrokerConfig("ignored-pipe", token));

  public static PipeServer WithAlwaysSucceedingInput()
  {
    static bool Yes(KeyChord _) => true;
    static bool YesText(string _) => true;
    return new PipeServer(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => -1, sendChord: Yes, sendText: YesText);
  }

  public static PipeServer WithForeground(int expected, int actual)
  {
    _ = expected; // documents the test's intent; the actual foreground drives the gate.
    return new PipeServer(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => actual);
  }

  public static PipeServer WithAlwaysSucceedingDrag(int foreground)
  {
    static bool Yes(KeyChord _) => true;
    static bool YesText(string _) => true;
    static bool YesDrag(string button, int fx, int fy, int tx, int ty) => true;
    return new PipeServer(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => foreground, sendChord: Yes, sendText: YesText, sendDrag: YesDrag);
  }
  public static PipeServer WithRecordingClick(int foreground)
  {
    static bool Yes(KeyChord _) => true;
    static bool YesText(string _) => true;
    static bool YesButton(string button, int x, int y) => true;
    static bool YesWheel(string direction, int pages, int x, int y) => true;
    return new PipeServer(new BrokerConfig("ignored-pipe", "t"), foregroundPid: () => foreground, sendChord: Yes,
      sendText: YesText, sendMouseButtonAt: YesButton, sendWheelAt: YesWheel);
  }
  public static PipeServer Authorized(params int[] connectionIds)
  {
    ArgumentNullException.ThrowIfNull(connectionIds);
    PipeServer server = Token("t");
    foreach (int connectionId in connectionIds)
    {
      _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId);
    }

    return server;
  }
};

/// <summary>Fake observation seam: records OwnerLost deliveries; returns empty application
///     lists. The native surface is task 17.</summary>
internal sealed class FakeObserver : IBrokerObserver
{
  public List<int> LostOwners { get; } = [];

  public JsonElement? ListApplications() => JsonSerializer.SerializeToElement(Array.Empty<object>());

  public JsonElement? ListWindows(JsonElement? parameters) => JsonSerializer.SerializeToElement(Array.Empty<object>());

  public JsonElement? CaptureApp(JsonElement? parameters) => null;

  public void OnOwnerLost(int ownerConnectionId) => LostOwners.Add(ownerConnectionId);
};
