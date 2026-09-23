namespace eThangAgent.ToolDomain.Tests;

public class ToolResultImageTests
{
  [Fact]
  public void Image_Construction_CarriesMediaTypeAndData()
  {
    ToolResultImage image = new("image/png", "aGVsbG8=");

    Assert.Equal("image/png", image.MediaType);
    Assert.Equal("aGVsbG8=", image.Base64Data);
  }

  [Theory]
  [InlineData("image/gif")]
  [InlineData("image/webp")]
  [InlineData("IMAGE/PNG")]
  [InlineData("")]
  [InlineData("text/plain")]
  public void Image_InvalidMediaType_IsRejected(string mediaType)
  {
    ArgumentException thrown = Assert.Throws<ArgumentException>(
        () => new ToolResultImage(mediaType, "aGVsbG8="));

    Assert.Equal("mediaType", thrown.ParamName);
  }

  [Theory]
  [InlineData("")]
  [InlineData("not base64!!")]
  [InlineData("abc")]
  public void Image_InvalidBase64_IsRejected(string base64)
  {
    ArgumentException thrown = Assert.Throws<ArgumentException>(
        () => new ToolResultImage("image/png", base64));

    Assert.Equal("base64Data", thrown.ParamName);
  }

  [Fact]
  public void Image_NullBase64_IsRejected()
  {
    ArgumentException thrown = Assert.Throws<ArgumentNullException>(
        () => new ToolResultImage("image/png", null!));

    Assert.Equal("base64Data", thrown.ParamName);
  }

  [Fact]
  public void ToolResult_WithoutImages_ImagesIsNull()
  {
    ToolResult result = new("ok", false);

    Assert.Null(result.Images);
  }

  [Fact]
  public void ToolResult_WithImages_PreservesImages()
  {
    List<ToolResultImage> images = [new("image/png", "aGVsbG8=")];

    ToolResult result = new("screenshot", false, Images: images);

    Assert.NotNull(result.Images);
    _ = Assert.Single(result.Images);
    Assert.Equal("image/png", result.Images[0].MediaType);
  }
}
