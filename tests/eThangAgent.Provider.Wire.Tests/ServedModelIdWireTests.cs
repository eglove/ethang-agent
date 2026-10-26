using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

// Pins for the SERVED model id (issue #290): the wire's "model" field names what actually
// answered, which is a different fact from the requested model the request carried. Both
// parse paths must capture it, and a provider that names nothing must leave it null -
// never coerced onto the requested id, because a fabricated served id is the same lie as
// no served id at all.
public class ServedModelIdWireTests
{
  [Fact]
  public void ParseResponse_ResponseBodyCarriesModel_CapturesItVerbatim()
  {
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"cohere/north-mini-code:free","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Equal("cohere/north-mini-code:free", result.Value.ServedModelId);
  }

  [Fact]
  public void ParseResponse_NoModelInBody_ServedModelIdStaysNull()
  {
    // "Unreported" is a real answer. The parser must not invent one from anything else
    // in the body - there is no fallback to a request id here, and there cannot be: the
    // request is not in scope.
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedModelId);
  }

  [Fact]
  public void ParseResponse_EmptyModelString_ServedModelIdStaysNull()
  {
    // An empty string names nothing. Treating it as a name would put a blank model id on
    // the ledger row and price it against an empty catalog key.
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedModelId);
  }

  [Fact]
  public async Task ReadSseStreamAsync_TerminalResponseCarriesModel_CapturesItVerbatim()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hi"}

        data: {"type":"response.completed","response":{"model":"cohere/north-mini-code:free","status":"completed","usage":{"input_tokens":5,"output_tokens":2}}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal("cohere/north-mini-code:free", result.Value.ServedModelId);
    Assert.Equal("Hi", result.Value.Content);
  }

  [Fact]
  public async Task ReadSseStreamAsync_NoModelInTerminal_ServedModelIdStaysNull()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hi"}

        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedModelId);
  }

  private static async Task<Result<ModelResponse>> ReadAsync(string sse)
  {
    using HttpResponseMessage response = new(HttpStatusCode.OK)
    {
      Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
    };
    return await ResponsesApiStreamCore.ReadSseStreamAsync(response, null, null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
  }
}
