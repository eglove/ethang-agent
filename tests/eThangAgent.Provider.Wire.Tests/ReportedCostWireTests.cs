using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

public class ReportedCostWireTests
{
  [Theory]
  [InlineData("0.0012", 0.0012)]
  [InlineData("0", 0)]
  public void NonStreaming_ReportedCost_IsPreserved(string cost, double expected)
  {
    using JsonDocument doc = JsonDocument.Parse($"{{\"output\":[],\"usage\":{{\"input_tokens\":10,\"output_tokens\":2,\"cost\":{cost}}}}}");
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(doc.RootElement);
    Assert.True(result.IsSuccess);
    Assert.Equal((decimal)expected, result.Value.Usage!.Value.ReportedCost);
  }

  [Theory]
  [InlineData("null")]
  [InlineData("-1")]
  [InlineData("\"1.2\"")]
  public void NonStreaming_InvalidCost_IsUnknown(string cost)
  {
    using JsonDocument doc = JsonDocument.Parse($"{{\"output\":[],\"usage\":{{\"input_tokens\":10,\"output_tokens\":2,\"cost\":{cost}}}}}");
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(doc.RootElement);
    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Usage!.Value.ReportedCost);
  }

  [Fact]
  public async Task Streaming_TerminalCost_IsPreserved()
  {
    const string sse = "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"cost\":0.004}}}\n\ndata: [DONE]\n\n";
    using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    Result<ModelResponse> result = await ResponsesApiStreamCore.ReadSseStreamAsync(response, null, null, TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    Assert.Equal(0.004m, result.Value.Usage!.Value.ReportedCost);
  }
}
