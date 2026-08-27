using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class TaskCategoryTests
{
  [Fact]
  public void Construction_AllProperties_RoundTrip()
  {
    TaskCategory category = new(["coding", "tool-use"], 3, true, true, 128_000, "needs vision and tools");
    Assert.Equal(["coding", "tool-use"], category.Tags);
    Assert.Equal(3, category.Complexity);
    Assert.True(category.RequiresVision);
    Assert.True(category.RequiresToolUse);
    Assert.Equal(128_000, category.MinContextWindow);
    Assert.Equal("needs vision and tools", category.Reasoning);
  }

  [Fact]
  public void Construction_NullMinContextWindow_AllowsUnconstrained()
  {
    TaskCategory category = new(["simple-lookup"], 1, false, false, null, null);
    Assert.Null(category.MinContextWindow);
    Assert.Null(category.Reasoning);
  }

  [Fact]
  public void ModelProviderEntry_RoundTripsAllFields()
  {
    ModelProviderEntry entry = new("google/gemini-2.0-flash-001", "Google", 0.000001m, 0.000002m, 1_048_576, 8192, true, false, 85.5, 80.0, 70.0, null, null, "Fast multimodal");
    Assert.Equal("google/gemini-2.0-flash-001", entry.ModelId);
    Assert.Equal(0.000001m, entry.PromptPricePerToken);
    Assert.Equal(0.000002m, entry.CompletionPricePerToken);
    Assert.Equal(1_048_576, entry.ContextLength);
    Assert.True(entry.SupportsToolUse);
    Assert.False(entry.SupportsVision);
    Assert.Equal(85.5, entry.IntelligenceScore);
    Assert.Equal("Fast multimodal", entry.Description);
  }

  [Fact]
  public void ModelFilter_AllNullable_DefaultToNull()
  {
    ModelFilter filter = new(null, null, null, null, null, null, null, null, null, null, null);
    Assert.Null(filter.MaxPromptPricePerToken);
    Assert.Null(filter.MaxCompletionPricePerToken);
    Assert.Null(filter.MinContextLength);
    Assert.Null(filter.RequireToolUse);
    Assert.Null(filter.RequireVision);
    Assert.Null(filter.MinIntelligenceScore);
  }

  [Fact]
  public void ModelSelectionResult_RoundTripsAllFields()
  {
    TaskCategory category = new(["coding"], 4, false, true, null, null);
    ModelFilter filter = new(null, null, null, null, true, null, 80.0, null, null, null, null);
    ModelSelectionResult result = new("anthropic/claude-3.5-sonnet", "Anthropic", category, filter, "best for coding");
    Assert.Equal("anthropic/claude-3.5-sonnet", result.ModelId);
    Assert.Same(category, result.Category);
    Assert.Same(filter, result.AppliedFilter);
    Assert.Equal("best for coding", result.Reasoning);
  }
}
