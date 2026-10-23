namespace eThangAgent.Transport.ACL.Tests;

/// <summary>A tool result's IMAGES must ride the wire (issue #230). The payload carried
///     only text, so a host-run turn could never render any image at all — the computer
///     screenshot path and the generated-image artifact both went dark in production
///     (every conversation runs in a host process).</summary>
public class RootTurnToolResultImagePayloadTests
{
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  [Fact]
  public void Serialize_WithImages_CarriesThemThroughParse()
  {
    string json = RootTurnToolResultPayload.Serialize("image_generation", "ok", null,
        ".ethang/images/a.png", ".ethang/images/a.png",
        [new RootTurnImage("image/png", PngBase64)]);

    RootTurnToolResultPayload? parsed = RootTurnToolResultPayload.Parse(json);

    Assert.NotNull(parsed);
    RootTurnImage image = Assert.Single(parsed.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(PngBase64, image.Base64Data);
  }

  [Fact]
  public void Serialize_WithoutImages_CarriesAnEmptyList()
  {
    string json = RootTurnToolResultPayload.Serialize("read", "ok", null, "ok", "content");

    RootTurnToolResultPayload? parsed = RootTurnToolResultPayload.Parse(json);

    Assert.NotNull(parsed);
    Assert.Empty(parsed.Images);
  }

  [Fact]
  public void Parse_LegacyPayloadWithoutImages_YieldsAnEmptyList()
  {
    const string json = /*lang=json,strict*/
        """{"Name":"read","Status":"ok","Title":null,"Summary":"ok","Content":"x"}""";

    RootTurnToolResultPayload? parsed = RootTurnToolResultPayload.Parse(json);

    Assert.NotNull(parsed);
    Assert.Empty(parsed.Images);
  }
}
