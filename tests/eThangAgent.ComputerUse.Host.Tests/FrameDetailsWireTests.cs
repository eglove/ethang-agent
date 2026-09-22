using eThangAgent.ComputerUse.ACL;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix-round-2 wire pin: a details-carrying error frame renders the details value FLAT
///     inside the error object ("details" carries a string value), and the client-side BrokerReply.Parse
///     decodes it (code, message, details) without faulting the connection.</summary>
public class FrameDetailsWireTests
{
  [Theory]
  [InlineData("controller_busy", "owner=2")]
  [InlineData("input_busy", "action_sent=false")]
  [InlineData("internal", "action_sent=false")]
  public void DetailsCarryingError_Frame_ParsesCleanlyClientSide(string code, string details)
  {
    BrokerResponse response = BrokerResponse.Fail(code, "the message", details);
    string frame = PipeServeLoop.FrameFor(response, id: 7);

    BrokerReply? reply = BrokerReply.Parse(frame);

    Assert.NotNull(reply);
    Assert.Equal(7, reply.Id);
    Assert.NotNull(reply.Error);
    Assert.Equal(code, reply.Error.Code);
    Assert.Equal("the message", reply.Error.Message);
    Assert.Equal(details, reply.Error.Details);
  }

  [Fact]
  public void Error_WithoutDetails_RendersWithoutDetailsKey()
  {
    string frame = PipeServeLoop.FrameFor(BrokerResponse.Fail("app_not_found", "no such app."), id: 3);
    BrokerReply? reply = BrokerReply.Parse(frame);
    Assert.NotNull(reply);
    Assert.NotNull(reply.Error);
    Assert.Equal("app_not_found", reply.Error.Code);
    Assert.Null(reply.Error.Details);
  }
}
