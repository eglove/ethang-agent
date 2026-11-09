namespace eThangAgent.ToolDomain.Tests;

/// <summary>The co-author trailer: the model's readable name, the vendor's GitHub org,
///     and the message assembly that appends it.</summary>
public class ModelAttributionTests
{
  [Theory]
  [InlineData("anthropic/claude-opus-4-8", "Claude Opus 4.8 [https://github.com/anthropics]")]
  [InlineData("openai/gpt-5-2", "GPT 5.2 [https://github.com/openai]")]
  [InlineData("google/gemini-3-pro", "Gemini 3 Pro [https://github.com/google-deepmind]")]
  [InlineData("x-ai/grok-4.7", "Grok 4.7 [https://github.com/xai-org]")]
  [InlineData("deepseek/deepseek-r1-0528", "DeepSeek R1 [https://github.com/deepseek-ai]")]
  [InlineData("qwen/qwen3-coder", "Qwen3 Coder [https://github.com/QwenLM]")]
  public void TrailerLine_NamesModelAndLinksVendorOrg(string modelId, string expected) =>
      Assert.Equal("Co-authored-by: " + expected, ModelAttribution.TrailerLine(modelId));

  [Fact]
  public void TrailerLine_OmitsTheLinkWhenTheVendorHasNoKnownOrg() =>
      Assert.Equal("Co-authored-by: Some New Model",
          ModelAttribution.TrailerLine("unregistered-vendor/some-new-model"));

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("openrouter/auto")]
  [InlineData("vendor/")]
  public void TrailerLine_IsNullWhenTheIdNamesNoModel(string? modelId) =>
      Assert.Null(ModelAttribution.TrailerLine(modelId));

  [Fact]
  public void WithTrailer_AppendsAfterABlankLineAndKeepsOneTrailingNewline() =>
      Assert.Equal(
          "feat: add a thing\n\nCo-authored-by: Claude Opus 4.8 [https://github.com/anthropics]\n",
          ModelAttribution.WithTrailer("feat: add a thing\n", "anthropic/claude-opus-4-8"));

  [Fact]
  public void WithTrailer_LeavesTheMessageUntouchedWhenNoModelIsKnown() =>
      Assert.Equal("feat: add a thing\n", ModelAttribution.WithTrailer("feat: add a thing\n", null));

  [Fact]
  public void WithTrailer_RejectsANullMessage() =>
      _ = Assert.Throws<ArgumentNullException>(() => ModelAttribution.WithTrailer(null!, null));
}
