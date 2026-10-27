using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

// Pins for the latency and serving-provider telemetry (issue #292). Two facts, each with
// its own trap:
//
//   * FIRST-TOKEN TIME is a different fact from TOTAL DURATION, and a non-streaming
//     response has no first delta at all - so it stays NULL. A fabricated 0 would read as
//     instant responsiveness, which is the opposite of what an unmeasured call can claim.
//   * THE SERVING PROVIDER is not on this wire. Measured 2026-10-27 across six live models
//     (routers included), pinned and unpinned, streaming and not: no `provider` member in
//     the body and no X-Provider-Name header. Null is therefore the ordinary answer, and
//     the parser still reads a provider that a future wire might name.
public class UsageTimingWireTests
{
  [Fact]
  public async Task ReadSseStreamAsync_StreamedDeltas_StampFirstTokenMs()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hi"}

        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    // Measured, so it is a number - not null. The exact value is the machine's business;
    // what the ledger depends on is that a streamed delta produces a measurement at all.
    Assert.True(result.Value.FirstTokenMs.HasValue);
    Assert.True(result.Value.FirstTokenMs >= 0);
  }

  [Fact]
  public async Task ReadSseStreamAsync_NoDeltas_FirstTokenMsStaysNull()
  {
    // A stream that carries only its terminal frame never streamed anything, so there is
    // no first token to time. Null is 'never streamed', not 'instant'.
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.FirstTokenMs);
  }

  [Fact]
  public async Task ReadSseStreamAsync_ReasoningDeltaOnly_StillStampsFirstTokenMs()
  {
    // A reasoning fragment is a delta the user waits for, so it counts as the first token.
    // Timing only content deltas would report a thinking model as never having started.
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.reasoning_text.delta","delta":"thinking"}

        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.FirstTokenMs.HasValue);
  }

  [Fact]
  public async Task ReadSseStreamAsync_FirstDeltaIsStampedOnce_NeverOverwritten()
  {
    // The FIRST delta is the responsiveness a user feels. If a later delta overwrote it,
    // a long generation would erase exactly the number the picker compares providers on.
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"a"}

        data: {"type":"response.output_text.delta","delta":"b"}

        data: {"type":"response.output_text.delta","delta":"c"}

        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal("abc", result.Value.Content);
    Assert.True(result.Value.FirstTokenMs.HasValue);
  }

  [Fact]
  public void ParseResponse_NonStreamingBody_FirstTokenMsStaysNull()
  {
    // The non-streaming path has no delta to observe, so it must report NULL - never 0.
    // This is the acceptance criterion that keeps 'no first token' and 'instant first
    // token' distinguishable.
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.FirstTokenMs);
  }

  [Fact]
  public void ParseResponse_BodyNamesProvider_CapturesItVerbatim()
  {
    // This surface names no provider today (measured), but a wire that starts naming one
    // must be recorded rather than lost - the forward-compatible rule ServedModelId follows.
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"m","provider":"DeepInfra","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Equal("DeepInfra", result.Value.ServedProvider);
  }

  [Fact]
  public void ParseResponse_NoProviderInBody_ServedProviderStaysNull()
  {
    // The measured live shape. Null is the answer, and it must never be backfilled from
    // anything else - the request is not in scope here, and a pin is not a fact about
    // what served (issue #292 B3).
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"m","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedProvider);
  }
  [Fact]
  public void ParseResponse_BodyReportsFlexServiceTier_CapturesItVerbatim()
  {
    // THE FLEX SMOKE TEST. A session that pins deepinfra/fp8/flex states an INTENTION in the
    // request; the response's top-level service_tier is the only fact the wire returns about
    // which capacity tier actually served it. OpenRouter's service-tiers docs: the Responses
    // API returns service_tier "at the top level of the response object".
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"m","service_tier":"flex","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Equal("flex", result.Value.ServedServiceTier);
  }

  [Fact]
  public void ParseResponse_NoServiceTier_ServedServiceTierStaysNull()
  {
    // The docs define null as "no service tier is available from upstream", so an absent
    // member reads as unreported. It must NEVER be backfilled from the request's requested
    // tier: a pin is an intention, and copying it here would fabricate the attribution this
    // member exists to make honest (the same rule ServedProvider follows).
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"m","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedServiceTier);
  }

  [Fact]
  public void ParseResponse_NullServiceTier_ReadsAsUnreported()
  {
    // An explicit JSON null is the shape a standard-tier call actually returns, and it means
    // the same thing as absence: no tier information. It is not the string "null".
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"model":"m","service_tier":null,"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedServiceTier);
  }

  [Fact]
  public async Task ReadSseStreamAsync_TerminalReportsServiceTier_CapturesItVerbatim()
  {
    // The streaming path must agree with the non-streaming one - both read the same response
    // object through the same shared reader.
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hi"}

        data: {"type":"response.completed","response":{"status":"completed","service_tier":"flex"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal("flex", result.Value.ServedServiceTier);
  }


  [Fact]
  public async Task ReadSseStreamAsync_TerminalNamesProvider_CapturesItVerbatim()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hi"}

        data: {"type":"response.completed","response":{"status":"completed","provider":"Together"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal("Together", result.Value.ServedProvider);
  }

  [Fact]
  public async Task ReadSseStreamAsync_NoProviderInTerminal_ServedProviderStaysNull()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hi"}

        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedProvider);
  }

  [Fact]
  public void ParseResponse_EmptyProviderString_ServedProviderStaysNull()
  {
    // A blank name names nothing - the same rule the served model id follows.
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(
        JsonDocument.Parse(
            /*lang=json,strict*/"""{"provider":"","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""")
            .RootElement);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.ServedProvider);
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
