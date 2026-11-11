using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

public class ReportedCostWireTests
{
  // The expected value is stated as decimal text, not as a double literal: .NET 11's
  // double-to-decimal conversion is exact, so (decimal)0.0012d is the binary expansion
  // 0.0011999999999999998948757574 rather than the 0.0012 the wire actually carried.
  [Theory]
  [InlineData("0.0012", "0.0012")]
  [InlineData("0", "0")]
  public void NonStreaming_ReportedCost_IsPreserved(string cost, string expectedText)
  {
    using JsonDocument doc = JsonDocument.Parse($"{{\"output\":[],\"usage\":{{\"input_tokens\":10,\"output_tokens\":2,\"cost\":{cost}}}}}");
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(doc.RootElement);
    Assert.True(result.IsSuccess);
    decimal expected = decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture);
    Assert.Equal(expected, result.Value.Usage!.Value.ReportedCost);
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
