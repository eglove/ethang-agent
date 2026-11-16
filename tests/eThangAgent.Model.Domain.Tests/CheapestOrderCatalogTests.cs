using System.Text.Json;

namespace eThangAgent.ModelDomain.Tests;

// The dominance split measured over the whole catalog, not one pair: at the workspace shape, how
// many multi-endpoint models have a winner that dominates the runner-up, and how many do not.
//
// THE FIXTURE IS CHECKED IN rather than read live, so the assertion checks the code instead of
// following the data. It is the workspace's openrouter catalog cache trimmed to the three
// advertised rates and the endpoint tag for every model carrying two or more priced endpoints,
// and it states the shape it was captured at.
public class CheapestOrderCatalogTests
{
  private const string FixtureName = "cheapest-order-catalog.json";

  // The fixture's own key names, kept apart from the record members that read them.
  private const string ShapeKey = "Shape";
  private const string ModelsKey = "Models";

  [Fact]
  public void For_OverTheCatalog_ReproducesTheMeasuredDominanceSplit()
  {
    Catalog catalog = Catalog.Load();

    int dominant = 0;
    int fragile = 0;
    List<string> fragileModels = [];
    foreach ((string modelId, IReadOnlyList<ModelProviderEntry> endpoints) in catalog.Models)
    {
      CheapestRanking ranking = CheapestOrder.For(endpoints, catalog.Shape);
      Assert.NotNull(ranking.WinnerSlug);
      Assert.NotNull(ranking.RunnerUpSlug);
      if (ranking.IsFragile)
      {
        fragile++;
        fragileModels.Add(modelId);
      }
      else
      {
        dominant++;
      }
    }

    int total = dominant + fragile;
    Assert.Equal(201, total);
    Assert.Equal(107, dominant);
    Assert.Equal(94, fragile);

    // The split is a real property of the catalog, not an artefact of one side being empty: a
    // majority winner would make the fragility claim decorative and a majority loser would make
    // it the whole answer.
    Assert.InRange(fragile, 1, total - 1);
    Assert.Contains("anthropic/claude-fable-5.2", fragileModels);
  }

  [Fact]
  public void For_OverTheCatalog_IsDeterministicInAnyInputOrder()
  {
    Catalog catalog = Catalog.Load();

    foreach ((_, IReadOnlyList<ModelProviderEntry> endpoints) in catalog.Models)
    {
      CheapestRanking forward = CheapestOrder.For(endpoints, catalog.Shape);
      CheapestRanking reversed = CheapestOrder.For([.. endpoints.Reverse()], catalog.Shape);

      Assert.Equal(forward.Slugs, reversed.Slugs);
      Assert.Equal(forward.WinnerSlug, reversed.WinnerSlug);
      Assert.Equal(forward.RunnerUpSlug, reversed.RunnerUpSlug);
      Assert.Equal(forward.IsFragile, reversed.IsFragile);
      Assert.Equal(forward.BreakEvenHitRate, reversed.BreakEvenHitRate);
    }
  }

  [Fact]
  public void For_OverTheCatalog_NeverExceedsTheCapAndNeverNamesAnEmptySlug()
  {
    Catalog catalog = Catalog.Load();

    foreach ((_, IReadOnlyList<ModelProviderEntry> endpoints) in catalog.Models)
    {
      CheapestRanking ranking = CheapestOrder.For(endpoints, catalog.Shape);

      Assert.True(ranking.Slugs.Count <= CheapestOrder.MaxOrderedEndpoints);
      Assert.All(ranking.Slugs, slug => Assert.False(string.IsNullOrEmpty(slug)));
      // The winner and the runner-up are always inside the returned list, because they are the
      // first two rows of the order the cap is applied to.
      Assert.Contains(ranking.WinnerSlug!, ranking.Slugs);
      Assert.Contains(ranking.RunnerUpSlug!, ranking.Slugs);
    }
  }

  /// <summary>The checked-in catalog fixture: the shape it was captured at, and every model
  ///     carrying two or more priced endpoints as catalog rows.
  ///     </summary>
  private sealed record Catalog(CostShape Shape, IReadOnlyList<(string ModelId, IReadOnlyList<ModelProviderEntry> Endpoints)> Models)
  {
    public static Catalog Load()
    {
      string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName);
      using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
      JsonElement root = document.RootElement;

      JsonElement shape = root.GetProperty(ShapeKey);
      CostShape costShape = new(shape.GetProperty("HitRate").GetDecimal(),
          shape.GetProperty("OutputPerInputRatio").GetDecimal());

      List<(string, IReadOnlyList<ModelProviderEntry>)> models = [];
      foreach (JsonElement model in root.GetProperty(ModelsKey).EnumerateArray())
      {
        string modelId = model.GetProperty("ModelId").GetString()!;
        List<ModelProviderEntry> endpoints = [];
        foreach (JsonElement endpoint in model.GetProperty("Endpoints").EnumerateArray())
        {
          endpoints.Add(new ModelProviderEntry(modelId, "Provider",
              Price(endpoint, "Input"), Price(endpoint, "CacheRead"), Price(endpoint, "Output"),
              128_000, 8_192, SupportsToolUse: true, SupportsVision: false,
              SupportsTextOutput: true, IntelligenceScore: null, CodingScore: null,
              AgenticScore: null, LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
          {
            Variant = new EndpointVariant(endpoint.GetProperty("Slug").GetString()),
          });
        }

        models.Add((modelId, endpoints));
      }

      return new Catalog(costShape, models);
    }

    private static decimal? Price(JsonElement endpoint, string name)
        => endpoint.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null
            : endpoint.GetProperty(name).GetDecimal();
  }
}
