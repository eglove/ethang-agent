using System.Net;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

/// <summary>The openrouter:image_generation server tool returns the generated image INLINE
///     in the response's output item - under "result" / "imageUrl" as a
///     data:&lt;media&gt;;base64,&lt;payload&gt; URL, never under "action". The wire cores must
///     carry that payload into the surfaced call; before this the image was dropped on the
///     floor and the user paid for an artifact nobody could reach (issue #230).
///     Live shape verified 2026-10 against the OpenRouter responses endpoint.</summary>
public class ServerToolCallImageParsingTests
{
  // A minimal PNG payload: the 8-byte signature plus three bytes. The parser carries
  // bytes verbatim; format validation belongs to the persistence step.
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  private static string ImageGenerationItem(string status, string? result)
  {
    string payload = result is null
        ? ""
        : ",\"result\":\"" + result + "\",\"imageUrl\":\"" + result + "\"";
    return "{\"type\":\"openrouter:image_generation\",\"id\":\"ig_1\",\"status\":\""
        + status + "\",\"prompt\":\"a red cube\"" + payload + "}";
  }

  [Fact]
  public void ParseResponse_ImageGenerationItem_CarriesImage()
  {
    string body = "{\"status\":\"completed\",\"output\":["
        + ImageGenerationItem("completed", "data:image/png;base64," + PngBase64)
        + ",{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"here it is\"}]}]}";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("image_generation", call.Tool);
    Assert.NotNull(call.Image);
    Assert.Equal("image/png", call.Image.MediaType);
    Assert.Equal(PngBase64, call.Image.Base64Data);
  }

  [Fact]
  public void ParseResponse_ImageGenerationItemWithoutResult_CarriesNoImage()
  {
    // Tolerance: an in-progress item (or one whose result the provider omitted) parses
    // as a call with no image - never a parse failure.
    string body = "{\"status\":\"completed\",\"output\":["
        + ImageGenerationItem("in_progress", null) + "]}";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("image_generation", call.Tool);
    Assert.Null(call.Image);
  }

  [Fact]
  public void ParseResponse_ImageGenerationItemWithNonDataUrl_CarriesNoImage()
  {
    // A remote https URL is not a payload this harness can persist; it parses as a call
    // with no image rather than throwing.
    string body = "{\"status\":\"completed\",\"output\":["
        + ImageGenerationItem("completed", "https://cdn.example/x.png") + "]}";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Null(call.Image);
  }

  [Fact]
  public void ParseResponse_WebSearchItem_CarriesNoImage()
  {
    // The existing shapes must parse byte-identically: no image member appears.
    const string body = "{\"status\":\"completed\",\"output\":[{\"type\":\"openrouter:web_search\",\"id\":\"st1\",\"status\":\"completed\",\"action\":{\"type\":\"search\",\"query\":\"dotnet\",\"sources\":[{\"type\":\"url\",\"url\":\"https://a.example/x\"}]}}]}";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("web_search", call.Tool);
    Assert.Equal("dotnet", call.Detail);
    Assert.Equal("https://a.example/x", Assert.Single(call.Sources));
    Assert.Null(call.Image);
  }

  [Fact]
  public async Task Streaming_ImageGenerationDoneEvent_CarriesImage()
  {
    // The live stream delivers the image ONLY on output_item.done: added is
    // status in_progress with no result. Registering on added alone would lose it.
    string added = JsonSerializer.Serialize(new
    {
      type = "response.output_item.added",
      output_index = 0,
      item = new { type = "openrouter:image_generation", id = "ig_1", status = "in_progress" }
    });
    string done = JsonSerializer.Serialize(new
    {
      type = "response.output_item.done",
      output_index = 0,
      item = new
      {
        type = "openrouter:image_generation",
        id = "ig_1",
        status = "completed",
        prompt = "a red cube",
        result = "data:image/png;base64," + PngBase64,
        imageUrl = "data:image/png;base64," + PngBase64
      }
    });
    string sse =
        "data: " + added + "\n\n" +
        "data: " + done + "\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}}\n\n" +
        "data: [DONE]\n\n";
    using HttpResponseMessage response = new(HttpStatusCode.OK)
    {
      Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
    };

    Result<ModelResponse> result = await ResponsesApiStreamCore.ReadSseStreamAsync(response, null, null, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("image_generation", call.Tool);
    Assert.NotNull(call.Image);
    Assert.Equal("image/png", call.Image.MediaType);
    Assert.Equal(PngBase64, call.Image.Base64Data);
  }

  [Fact]
  public async Task Streaming_WebSearchDoneEvent_CarriesNoImage()
  {
    string sse =
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"st1\",\"type\":\"openrouter:web_search\",\"status\":\"completed\",\"action\":{\"type\":\"search\",\"query\":\"q\"}}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}}\n\n" +
        "data: [DONE]\n\n";
    using HttpResponseMessage response = new(HttpStatusCode.OK)
    {
      Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
    };

    Result<ModelResponse> result = await ResponsesApiStreamCore.ReadSseStreamAsync(response, null, null, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Null(call.Image);
  }
}
